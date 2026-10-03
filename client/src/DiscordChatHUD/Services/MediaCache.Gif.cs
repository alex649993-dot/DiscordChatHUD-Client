using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;
namespace DiscordChatHUD.Services;
internal sealed partial class MediaCache
{
    private sealed record GifFrame(long Offset, int Length, Rectangle Bounds, byte Packed, byte[] Control);
    // Decode image rectangles independently, then apply GIF disposal on one canvas.
    // The number of original frames never determines peak decoded memory.
    private static DecodedMedia DecodeGifSequentially(Stream stream, IReadOnlyList<int> sourceDelays, Action<Bitmap>? firstFrameReady = null)
    {
        stream.Position = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        var head = reader.ReadBytes(13);
        if (head.Length != 13) throw new InvalidDataException("Truncated GIF header.");
        int width = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6)), height = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
        if (width == 0 || height == 0 || (long)width * height > MaxDecodedPixels) throw new MediaBudgetExceededException();
        var global = (head[10] & 128) != 0 ? reader.ReadBytes(3 * (1 << ((head[10] & 7) + 1))) : [];
        var frames = new List<GifFrame>(sourceDelays.Count);
        byte[] control = new byte[4];
        while (stream.Position < stream.Length)
        {
            var marker = reader.ReadByte();
            if (marker == 0x3b) break;
            if (marker == 0x21)
            {
                var label = reader.ReadByte();
                if (label == 0xf9)
                {
                    if (reader.ReadByte() != 4) throw new InvalidDataException("Invalid GIF control.");
                    control = reader.ReadBytes(4);
                    if (control.Length != 4 || reader.ReadByte() != 0) throw new InvalidDataException("Truncated GIF control.");
                }
                else
                {
                    if (!SkipGifSubBlocks(reader)) throw new InvalidDataException("Truncated GIF extension.");
                    if (label == 0x01) control = new byte[4];
                }
                continue;
            }
            if (marker != 0x2c) throw new InvalidDataException("Invalid GIF block.");
            var descriptor = reader.ReadBytes(9);
            if (descriptor.Length != 9) throw new InvalidDataException("Truncated GIF frame.");
            var bounds = new Rectangle(BinaryPrimitives.ReadUInt16LittleEndian(descriptor), BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2)),
                BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(6)));
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Right > width || bounds.Bottom > height) throw new InvalidDataException("GIF frame outside canvas.");
            var offset = stream.Position;
            if ((descriptor[8] & 128) != 0 && !SkipGifBytes(reader, 3 * (1 << ((descriptor[8] & 7) + 1)))) throw new InvalidDataException("Truncated palette.");
            _ = reader.ReadByte();
            if (!SkipGifSubBlocks(reader)) throw new InvalidDataException("Truncated GIF LZW data.");
            frames.Add(new(offset, checked((int)(stream.Position - offset)), bounds, descriptor[8], control));
            control = new byte[4];
        }
        if (frames.Count != sourceDelays.Count) throw new InvalidDataException("GIF frame count mismatch.");
        var scale = Math.Min(1d, MaxAnimatedDimension / (double)Math.Max(width, height));
        var size = new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        var budget = Math.Max(2, (int)Math.Min(MaxAnimatedFrames, MaxAnimatedFramePixels / ((long)size.Width * size.Height)));
        var indexes = SelectTimelineFrameIndexes(sourceDelays, Math.Min(frames.Count, budget));
        var delays = new int[indexes.Count];
        LiveExport.Value?.Begin(size,indexes,sourceDelays);
        using var builder = new CompressedBitmapFrames.Builder(indexes.Count, size);
        using var canvas = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        canvas.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(canvas);
        graphics.CompositingQuality = CompositingQuality.AssumeLinear;
        var backgroundIndex = head[11] * 3;
        var background = (frames[0].Control[0] & 1) == 0 && backgroundIndex + 2 < global.Length
            ? Color.FromArgb(global[backgroundIndex], global[backgroundIndex + 1], global[backgroundIndex + 2]) : Color.Transparent;
        graphics.Clear(background);
        var options = new DecoderOptions { Configuration = DecoderConfiguration, MaxFrames = 1 };
        var samples = SampleIndexes(indexes.Count).ToHashSet();
        int selected = 0, representative = 0;
        double best = double.NegativeInfinity;
        var last = indexes[^1];

        // Cut each frame out of the file as a standalone one-frame GIF. Reading is
        // sequential because it seeks in the source; decoding the result is not.
        MemoryStream CutStandaloneFrame(int index)
        {
            var frame = frames[index];
            var payload = new MemoryStream(frame.Length + head.Length + global.Length + 32);
            // Preserve palette/interlace/transparency; the standalone image is the rectangle itself.
            var localHead = (byte[])head.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(localHead.AsSpan(6), (ushort)frame.Bounds.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(localHead.AsSpan(8), (ushort)frame.Bounds.Height);
            payload.Write(localHead); payload.Write(global);
            payload.WriteByte(0x21); payload.WriteByte(0xf9); payload.WriteByte(4);
            payload.Write(frame.Control); payload.WriteByte(0);
            payload.WriteByte(0x2c);
            var singleDescriptor = new byte[9];
            BinaryPrimitives.WriteUInt16LittleEndian(singleDescriptor.AsSpan(4), (ushort)frame.Bounds.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(singleDescriptor.AsSpan(6), (ushort)frame.Bounds.Height);
            singleDescriptor[8] = frame.Packed; payload.Write(singleDescriptor);
            stream.Position = frame.Offset;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = frame.Length;
                while (remaining > 0) { var count = Math.Min(buffer.Length, remaining); stream.ReadExactly(buffer.AsSpan(0,count)); payload.Write(buffer,0,count); remaining -= count; }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            payload.WriteByte(0x3b); payload.Position = 0;
            return payload;
        }
        SixLabors.ImageSharp.Image<Bgra32> DecodeStandaloneFrame(MemoryStream payload)
        {
            using (payload) return SharpImage.Load<Bgra32>(options, payload);
        }

        // Relay 244: the same trick 242 gave the WebP path. Each frame's LZW data
        // decodes on its own; only compositing depends on the canvas before it, and
        // that stays strictly in order, so the canvas is byte for byte the same.
        // The client keeps decoding one frame at a time beside the game.
        var parallelism = ExportProfile.Value == ExportProfileClient ? 1 : ExportDecoderThreads;
        var window = new Queue<Task<SixLabors.ImageSharp.Image<Bgra32>>>();
        var started = 0;
        try
        {
        for (var i = 0; i <= last; i++)
        {
            var frame = frames[i];
            var disposal = (frame.Control[0] >> 2) & 7;
            using var previous = disposal == 3 ? canvas.Clone(frame.Bounds, PixelFormat.Format32bppPArgb) : null;
            if (parallelism > 1)
            {
                while (started <= last && window.Count < parallelism)
                {
                    var payload = CutStandaloneFrame(started++);
                    window.Enqueue(Task.Run(() => DecodeStandaloneFrame(payload)));
                }
            }
            using (var image = parallelism > 1
                       ? window.Dequeue().GetAwaiter().GetResult()
                       : DecodeStandaloneFrame(CutStandaloneFrame(i)))
            {
                graphics.Flush(FlushIntention.Sync);
                CompositeGifFrame(image.Frames.RootFrame, canvas, frame.Bounds);
            }
            if (selected < indexes.Count && indexes[selected] == i)
            {
                graphics.Flush(FlushIntention.Sync);
                using var scaled = CloneScaled(canvas, MaxAnimatedDimension);
                if (selected == 0) firstFrameReady?.Invoke(scaled);
                builder.SetFrame(selected, scaled);
                LiveExport.Value?.Add(selected,scaled);
                if (samples.Contains(selected))
                {
                    var score = ScoreFrame(scaled, selected == 0);
                    if (score > best) { best = score; representative = selected; }
                }
                var next = selected + 1 < indexes.Count ? indexes[selected + 1] : sourceDelays.Count;
                long delay = 0;
                for (int j = i; j < next; j++) delay += sourceDelays[j];
                delays[selected++] = (int)Math.Clamp(delay, 10, int.MaxValue);
            }
            if (disposal == 2)
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                using var clear = new SolidBrush((frame.Control[0] & 1) != 0 ? Color.Transparent : background);
                graphics.FillRectangle(clear, frame.Bounds);
            }
            else if (previous is not null)
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(previous, frame.Bounds.Location);
            }
        }
        }
        finally
        {
            // A failed frame must not leak the frames already decoded ahead of it.
            while (window.Count > 0)
            {
                try { window.Dequeue().GetAwaiter().GetResult().Dispose(); } catch { }
            }
        }
        return new DecodedMedia(builder.Build(), delays, representative) { CompressionEvaluated = true };
    }
    // GIF transparency is binary: transparent pixels retain the canvas and
    // opaque pixels can be copied directly, without a second Bitmap/GDI draw.
    private static unsafe void CompositeGifFrame(SixLabors.ImageSharp.ImageFrame<Bgra32> frame, Bitmap canvas, Rectangle bounds)
    {
        var bits = canvas.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            frame.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var source = System.Runtime.InteropServices.MemoryMarshal.Cast<Bgra32, uint>(accessor.GetRowSpan(y));
                    var target = new Span<uint>((byte*)bits.Scan0 + y * bits.Stride, bounds.Width);
                    for (var x = 0; x < source.Length; x++)
                        if ((source[x] & 0xff000000U) != 0) target[x] = source[x];
                }
            });
        }
        finally { canvas.UnlockBits(bits); }
    }

}
