using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;

namespace DiscordChatHUD.Services;
internal sealed partial class MediaCache
{
    /// <summary>Threads a relay export may use to decode upcoming frames (client decoding stays at 1).</summary>
    internal static int RelayDecodeParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 1, 4);

    private sealed record WebpFrame(long Offset, int Length, Rectangle Bounds, int Delay, byte Flags);
    private static int Read24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
    private static void Write24(Span<byte> bytes, int value)
    { bytes[0] = (byte)value; bytes[1] = (byte)(value >> 8); bytes[2] = (byte)(value >> 16); }

    // Demux ANMF rectangles, decode ONE still WebP at a time, then compose on a
    // single canvas. No ImageSharp animation collection of full-sized frames.
    // Spec: https://developers.google.com/speed/webp/docs/riff_container
    private static DecodedMedia DecodeWebpSequentially(Stream stream, Action<Bitmap>? firstFrameReady = null)
    {
        if (!stream.CanSeek || stream.Length > MaxDownloadBytes) throw new InvalidDataException("Invalid WebP stream.");
        stream.Position = 0;
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header[..12]);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("Invalid WebP header.");
        long end = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) + 8L;
        if (end > stream.Length || end < 12) throw new InvalidDataException("Truncated WebP.");
        int width = 0, height = 0;
        bool animation = false;
        var frames = new List<WebpFrame>();
        while (stream.Position + 8 <= end)
        {
            stream.ReadExactly(header[..8]);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            var payload = stream.Position;
            var next = payload + length + (length & 1);
            if (next > end) throw new InvalidDataException("Truncated WebP chunk.");
            if (kind == 0x58385056) // VP8X
            {
                if (length != 10 || width != 0) throw new InvalidDataException("Invalid WebP canvas.");
                stream.ReadExactly(header[..10]);
                animation = (header[0] & 2) != 0;
                width = Read24(header.Slice(4, 3)) + 1; height = Read24(header.Slice(7, 3)) + 1;
                // Bound peak allocations for one canvas + one decoder, independent of frame count.
                if ((long)width * height > 8_000_000L) throw new MediaBudgetExceededException();
            }
            else if (kind == 0x464d4e41) // ANMF
            {
                if (!animation || width == 0 || length < 24 || frames.Count >= 10000)
                    throw new InvalidDataException("Invalid WebP animation frame.");
                stream.ReadExactly(header);
                var bounds = new Rectangle(Read24(header[..3]) * 2, Read24(header.Slice(3, 3)) * 2,
                    Read24(header.Slice(6, 3)) + 1, Read24(header.Slice(9, 3)) + 1);
                if (bounds.Right > width || bounds.Bottom > height) throw new InvalidDataException("WebP frame outside canvas.");
                frames.Add(new(stream.Position, checked((int)length - 16), bounds,
                    Math.Max(MinimumSourceFrameDelayMilliseconds, Read24(header.Slice(12, 3))), header[15]));
            }
            stream.Position = next;
        }
        if (frames.Count < 2) throw new InvalidDataException("Missing WebP animation frames.");
        var sourceDelays = frames.Select(f => f.Delay).ToArray();
        var scale = Math.Min(1d, MaxAnimatedDimension / (double)Math.Max(width, height));
        var size = new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        var budget = Math.Max(2, (int)Math.Min(MaxAnimatedFrames, MaxAnimatedFramePixels / ((long)size.Width * size.Height)));
        var indexes = SelectTimelineFrameIndexes(sourceDelays, Math.Min(frames.Count, budget));
        var delays = new int[indexes.Count];
        LiveExport.Value?.Begin(size,indexes,sourceDelays);
        using var builder = new CompressedBitmapFrames.Builder(indexes.Count, size);
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        // Frame bounds are encoded pixels, independent of the desktop DPI.
        canvas.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.Transparent);
        graphics.CompositingQuality = CompositingQuality.AssumeLinear;
        int selected = 0;
        double best = double.NegativeInfinity; int representative = 0;
        var scoreIndexes = SampleIndexes(indexes.Count).ToHashSet();
        // Each ANMF bitstream decodes independently; only composing depends on the
        // previous canvas. The client decodes one frame at a time to stay light
        // beside the game. A relay export decodes a small window of upcoming
        // frames on the server's other cores and still composes strictly in
        // order, so the canvas (and every pack byte) is the same.
        var parallelism = ExportProfile.Value == ExportProfileClient ? 1 : ExportDecoderThreads;
        var window = new Queue<Task<Bitmap>>();
        var started = 0;
        try
        {
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (parallelism > 1)
            {
                while (started < frames.Count && window.Count < parallelism)
                {
                    var upcoming = frames[started++];
                    var encodedFrame = ReadStandaloneWebpFrame(stream, upcoming);
                    window.Enqueue(Task.Run(() => DecodeStandaloneWebpFrame(encodedFrame, upcoming)));
                }
            }
            using (var bitmap = parallelism > 1
                       ? window.Dequeue().GetAwaiter().GetResult()
                       : DecodeStandaloneWebpFrame(ReadStandaloneWebpFrame(stream, frame), frame))
            {
                graphics.CompositingMode = (frame.Flags & 2) != 0 ? CompositingMode.SourceCopy : CompositingMode.SourceOver;
                graphics.DrawImage(bitmap, frame.Bounds, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
            }
            if (selected < indexes.Count && indexes[selected] == i)
            {
                graphics.Flush(FlushIntention.Sync);
                using var scaled = CloneScaled(canvas, MaxAnimatedDimension);
                if (selected == 0) firstFrameReady?.Invoke(scaled);
                builder.SetFrame(selected, scaled);
                LiveExport.Value?.Add(selected,scaled);
                if (scoreIndexes.Contains(selected))
                {
                    var score = ScoreFrame(scaled, selected == 0);
                    if (score > best) { best = score; representative = selected; }
                }
                var stop = selected + 1 < indexes.Count ? indexes[selected + 1] : frames.Count;
                long delay = 0; for (var j = i; j < stop; j++) delay += sourceDelays[j];
                delays[selected++] = (int)Math.Min(delay, int.MaxValue);
            }
            if ((frame.Flags & 1) != 0)
            {
                // Like browser decoders, use transparent background for animated media.
                graphics.CompositingMode = CompositingMode.SourceCopy;
                using var clear = new SolidBrush(Color.Transparent);
                graphics.FillRectangle(clear, frame.Bounds);
            }
        }
        }
        finally
        {
            // A failed frame must not leak the bitmaps already decoded ahead of it.
            while (window.Count > 0)
            {
                var pending = window.Dequeue();
                try { pending.GetAwaiter().GetResult().Dispose(); } catch { }
            }
        }
        return new DecodedMedia(builder.Build(), delays, representative) { CompressionEvaluated = true };
    }

    /// <summary>Decode one standalone ANMF bitstream (takes ownership of <paramref name="encoded"/>).</summary>
    private static Bitmap DecodeStandaloneWebpFrame(MemoryStream encoded, WebpFrame frame)
    {
        using (encoded)
        {
            var options = new DecoderOptions { Configuration = DecoderConfiguration, MaxFrames = 1 };
            var info = SharpImage.Identify(options, encoded);
            if (info.Width != frame.Bounds.Width || info.Height != frame.Bounds.Height)
                throw new InvalidDataException("WebP bitstream dimensions disagree with ANMF.");
            encoded.Position = 0;
            using var image = SharpImage.Load<Bgra32>(options, encoded);
            return CopyImageSharpFrameToBitmap(image.Frames.RootFrame);
        }
    }

    private static MemoryStream ReadStandaloneWebpFrame(Stream source, WebpFrame frame)
    {
        var result = new MemoryStream(frame.Length + 30);
        try
        {
            // VP8X preserves separate ALPH chunks on lossy WebP rectangles.
            Span<byte> head = stackalloc byte[30]; head.Clear();
            "RIFF"u8.CopyTo(head); BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(4, 4), (uint)(frame.Length + 22));
            "WEBPVP8X"u8.CopyTo(head[8..]); BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(16, 4), 10);
            head[20] = 0x10; Write24(head.Slice(24, 3), frame.Bounds.Width - 1); Write24(head.Slice(27, 3), frame.Bounds.Height - 1);
            result.Write(head); source.Position = frame.Offset;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int remaining = frame.Length;
                while (remaining > 0)
                { int n = Math.Min(remaining, buffer.Length); source.ReadExactly(buffer.AsSpan(0, n)); result.Write(buffer, 0, n); remaining -= n; }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            var bytes = result.GetBuffer();
            bool alpha = false, hasImage = false;
            int position = 30;
            while (position + 8 <= result.Length)
            {
                var chunk = bytes.AsSpan(position, 8);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                long next = position + 8L + length + (length & 1);
                if (next > result.Length) throw new InvalidDataException("Invalid WebP frame subchunk.");
                if (chunk[..4].SequenceEqual("ALPH"u8)) alpha = true;
                if (chunk[..4].SequenceEqual("VP8 "u8) || chunk[..4].SequenceEqual("VP8L"u8))
                {
                    if (hasImage) throw new InvalidDataException("Multiple bitstreams in one WebP frame.");
                    hasImage = true;
                    if (chunk[..4].SequenceEqual("VP8L"u8) && length >= 5)
                        alpha |= (bytes[position + 12] & 0x10) != 0;
                }
                position = (int)next;
            }
            if (!hasImage || position != result.Length) throw new InvalidDataException("Missing WebP frame bitstream.");
            bytes[20] = alpha ? (byte)0x10 : (byte)0;
            result.Position = 0; return result;
        }
        catch { result.Dispose(); throw; }
    }
}
