using System.Buffers.Binary;
using SharpImage = SixLabors.ImageSharp.Image;
using SharpConfiguration = SixLabors.ImageSharp.Configuration;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiscordChatHUD.Services;

internal sealed partial class CompressedBitmapFrames
{
    internal const long MaxPackBytes = 64L * 1024 * 1024;
    private static ReadOnlySpan<byte> PackMagic => "DHSFRM01"u8;
    private static ReadOnlySpan<byte> HighQualityPackMagic => "DHSFRM02"u8;
    // Preview 305 / Relay 242: the same frames stored as lossless WebP, about half
    // the bytes of the LZ4 blocks. The payload is the exact premultiplied BGRA of
    // each frame, so decoding reproduces the LZ4 pack's pixels byte for byte.
    private static ReadOnlySpan<byte> WebpPackMagic => "DHSFRW01"u8;
    private static ReadOnlySpan<byte> HighQualityWebpPackMagic => "DHSFRW02"u8;
    private static readonly SharpConfiguration WebpConfiguration = CreateWebpConfiguration();
    private static SharpConfiguration CreateWebpConfiguration()
    {
        var config = SharpConfiguration.Default.Clone();
        config.MaxDegreeOfParallelism = 1;
        return config;
    }

    /// <summary>
    /// Relay: rewrite an LZ4 frame pack as a lossless WebP pack. Frames are encoded
    /// in parallel (independent), then written in order.
    /// </summary>
    internal static void TranscodePackToWebp(Stream lz4Pack, Stream output, int parallelism, WebpEncodingMethod method = WebpEncodingMethod.Level0, int quality = 0, Func<bool>? shouldYield = null)
    {
        using var frames = ReadPack(lz4Pack, out var delays, out var representative);
        var highQuality = IsHighQualityPack(lz4Pack);
        frames.WriteWebpPack(output, delays, representative, highQuality, parallelism, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Method = method, Quality = quality, TransparentColorMode = WebpTransparentColorMode.Preserve }, shouldYield);
    }

    private void WriteWebpPack(Stream output, IReadOnlyList<int> delays, int representative, bool highQuality, int parallelism, WebpEncoder encoder, Func<bool>? shouldYield)
    {
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(highQuality ? HighQualityWebpPackMagic : WebpPackMagic); writer.Write(_size.Width); writer.Write(_size.Height);
        writer.Write(Count); writer.Write(representative);
        for (var i = 0; i < Count; i++) writer.Write(delays[i]);
        var frameBytes = checked(_size.Width * _size.Height * 4);
        var batch = Math.Max(1, parallelism) * 2;
        for (var start = 0; start < Count; start += batch)
        {
            if (shouldYield?.Invoke() == true) throw new OperationCanceledException("Foreground media takes priority");
            var end = Math.Min(Count, start + batch);
            // Storage reads stay sequential; only the encoding runs in parallel.
            var pixels = new byte[end - start][];
            for (var i = start; i < end; i++)
            {
                var payload = new byte[_lengths[i]];
                _storage.Position = _offsets[i];
                _storage.ReadExactly(payload);
                if (_rawFrames) { pixels[i - start] = payload; continue; }
                var decoded = new byte[frameBytes];
                AnimationFrameCodec.Decode(payload, decoded);
                pixels[i - start] = decoded;
            }
            var encoded = new byte[end - start][];
            Parallel.For(0, end - start, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) }, k =>
            {
                using var image = SharpImage.LoadPixelData<Bgra32>(WebpConfiguration, pixels[k], _size.Width, _size.Height);
                using var stream = new MemoryStream();
                image.Save(stream, encoder);
                encoded[k] = stream.ToArray();
            });
            foreach (var frame in encoded) { writer.Write(frame.Length); writer.Write(frame); }
        }
    }

    internal static bool IsWebpPack(Stream input)
    {
        if (!input.CanSeek || input.Length < 8) return false;
        long position = input.Position;
        try { input.Position = 0; Span<byte> bytes = stackalloc byte[8]; input.ReadExactly(bytes); return bytes.SequenceEqual(WebpPackMagic) || bytes.SequenceEqual(HighQualityWebpPackMagic) || DeltaMagic(bytes); }
        finally { input.Position = position; }
    }

    internal void WritePack(Stream output, IReadOnlyList<int> delays, int representative, bool highQuality = false)
    {
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(highQuality ? HighQualityPackMagic : PackMagic); writer.Write(_size.Width); writer.Write(_size.Height);
        writer.Write(Count); writer.Write(representative);
        for (var i = 0; i < Count; i++) writer.Write(delays[i]);
        var buffer = new byte[64 * 1024];
        for (var i = 0; i < Count; i++)
        {
            if (_rawFrames)
            {
                var raw = new byte[_lengths[i]]; _storage.Position = _offsets[i]; _storage.ReadExactly(raw);
                var encoded = AnimationFrameCodec.Encode(raw); writer.Write(encoded.Length); writer.Write(encoded); continue;
            }
            writer.Write(_lengths[i]); _storage.Position = _offsets[i];
            var remaining = _lengths[i];
            while (remaining > 0)
            {
                int take = Math.Min(remaining, buffer.Length);
                _storage.ReadExactly(buffer.AsSpan(0, take)); writer.Write(buffer, 0, take); remaining -= take;
            }
        }
    }

    internal static bool IsHighQualityPack(Stream input)
    {
        if (!input.CanSeek || input.Length < 8) return false;
        long position = input.Position;
        try { input.Position = 0; Span<byte> bytes = stackalloc byte[8]; input.ReadExactly(bytes); return bytes.SequenceEqual(HighQualityPackMagic) || bytes.SequenceEqual(HighQualityWebpPackMagic) || bytes.SequenceEqual(HighQualityDeltaPackMagic); }
        finally { input.Position = position; }
    }
    internal static bool IsPack(Stream input)
    {
        if (!input.CanSeek || input.Length < 8) return false;
        long position = input.Position;
        try
        {
            input.Position = 0; Span<byte> bytes = stackalloc byte[8]; input.ReadExactly(bytes);
            return bytes.SequenceEqual(PackMagic) || bytes.SequenceEqual(HighQualityPackMagic)
                || bytes.SequenceEqual(WebpPackMagic) || bytes.SequenceEqual(HighQualityWebpPackMagic) || DeltaMagic(bytes);
        }
        finally { input.Position = position; }
    }

    internal static CompressedBitmapFrames ReadPack(Stream input, out int[] delays, out int representative, bool takeOwnership = false)
    {
        if (!input.CanSeek || input.Length > MaxPackBytes || input.Length < 24) throw new InvalidDataException("Frame pack size");
        input.Position = 0;
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(8);
        bool delta = DeltaMagic(magic);
        bool webp = magic.AsSpan().SequenceEqual(WebpPackMagic) || magic.AsSpan().SequenceEqual(HighQualityWebpPackMagic);
        bool highQuality = magic.AsSpan().SequenceEqual(HighQualityPackMagic) || magic.AsSpan().SequenceEqual(HighQualityWebpPackMagic) || magic.AsSpan().SequenceEqual(HighQualityDeltaPackMagic);
        if (!delta && !webp && !highQuality && !magic.AsSpan().SequenceEqual(PackMagic)) throw new InvalidDataException("Frame pack version");
        int width = reader.ReadInt32(), height = reader.ReadInt32(), count = reader.ReadInt32();
        representative = reader.ReadInt32();
        if (width < 1 || width > (highQuality ? 800 : 640) || height < 1 || height > (highQuality ? 800 : 640) || count < 2 || count > (highQuality ? 240 : 180)
            || representative < 0 || representative >= count || (long)width * height * count * 4 > (highQuality ? 120_000_000 : 60_000_000))
            throw new InvalidDataException("Frame pack dimensions");
        delays = new int[count];
        for (var i = 0; i < count; i++)
        {
            delays[i] = reader.ReadInt32();
            if (delays[i] < 10 || delays[i] > 3_600_000) throw new InvalidDataException("Frame pack timeline");
        }
        var offsets = new long[count]; var lengths = new int[count];
        int frameBytes = checked(width * height * 4);
        int maximumPayload = 5 + K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(frameBytes);
        Span<byte> header = stackalloc byte[5];
        if (webp || delta) return ReadWebpFrames(reader, input, count, width, height, frameBytes, delays, representative, highQuality, delta);

        // Network and persistent-cache downloads are already temporary files.
        // Transfer that file directly into the frame cache instead of copying a
        // 10-60 MB pack into another temporary file before first playback.
        if (takeOwnership && input is FileStream ownedStorage)
        {
            long payloadBytes = 0;
            for (var i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 5 || length > maximumPayload || length > input.Length - input.Position)
                    throw new InvalidDataException("Frame pack block");
                var offset = input.Position;
                input.ReadExactly(header);
                if (BinaryPrimitives.ReadInt32LittleEndian(header) != frameBytes
                    || header[4] > 1 || (header[4] == 1 && frameBytes % 4 != 0))
                    throw new InvalidDataException("Frame pack payload header");
                input.Position = offset + length;
                offsets[i] = offset; lengths[i] = length; payloadBytes += length;
            }
            if (input.Position != input.Length) throw new InvalidDataException("Frame pack trailing data");
            return new CompressedBitmapFrames(ownedStorage, offsets, lengths, new Size(width, height), payloadBytes);
        }

        string path = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-frames-" + Guid.NewGuid().ToString("N") + ".tmp");
        var storage = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        try
        {
            var copyBuffer = new byte[64 * 1024];
            for (var i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < 5 || length > maximumPayload || length > input.Length - input.Position)
                    throw new InvalidDataException("Frame pack block");

                // Server packs already contain independently compressed lossless
                // frames. v276 decoded every frame here and rewrote ~5 seconds of
                // animation as RAW pixels before the first animated frame could be
                // shown. Keep the validated compressed blocks instead and decode
                // only the frame the renderer actually asks for. This spreads the
                // same work across playback rather than creating a first-load CPU
                // burst, while preserving exact pixels, frame count and timeline.
                input.ReadExactly(header);
                if (BinaryPrimitives.ReadInt32LittleEndian(header) != frameBytes
                    || header[4] > 1 || (header[4] == 1 && frameBytes % 4 != 0))
                    throw new InvalidDataException("Frame pack payload header");

                offsets[i] = storage.Position;
                lengths[i] = length;
                storage.Write(header);
                var remaining = length - header.Length;
                while (remaining > 0)
                {
                    int take = Math.Min(remaining, copyBuffer.Length);
                    input.ReadExactly(copyBuffer.AsSpan(0, take));
                    storage.Write(copyBuffer, 0, take);
                    remaining -= take;
                }
            }
            if (input.Position != input.Length) throw new InvalidDataException("Frame pack trailing data");
            storage.Flush();
            return new CompressedBitmapFrames(storage, offsets, lengths, new Size(width, height), storage.Length);
        }
        catch { storage.Dispose(); throw; }
    }

    // Client: turn the lossless WebP frames back into the usual LZ4 blocks once,
    // so playback decodes frames exactly as cheaply as before.
    private static CompressedBitmapFrames ReadWebpFrames(BinaryReader reader, Stream input, int count, int width, int height, int frameBytes, int[] delays, int representative, bool highQuality, bool delta = false)
    {
        string path = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-frames-" + Guid.NewGuid().ToString("N") + ".tmp");
        var storage = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        try
        {
            using var writer = new BinaryWriter(storage, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(highQuality ? HighQualityPackMagic : PackMagic);
            writer.Write(width); writer.Write(height); writer.Write(count); writer.Write(representative);
            foreach (var delay in delays) writer.Write(delay);
            var offsets = new long[count]; var lengths = new int[count];
            var pixels = new byte[frameBytes];
            var options = new DecoderOptions { Configuration = WebpConfiguration, MaxFrames = 1 };
            for (var i = 0; i < count; i++)
            {
                int length = reader.ReadInt32();
                if (length < (delta ? 21 : 20) || length > frameBytes + 4097 || length > input.Length - input.Position)
                    throw new InvalidDataException("Frame pack block");
                var encoded = reader.ReadBytes(length);
                if (delta) pixels = DecodeDelta(encoded, new Size(width, height), i == 0 ? null : pixels);
                else using (var image = SharpImage.Load<Bgra32>(options, encoded))
                {
                    if (image.Width != width || image.Height != height || image.Frames.Count != 1)
                        throw new InvalidDataException("Frame pack WebP frame");
                    image.CopyPixelDataTo(pixels);
                }
                var lengthPosition = storage.Position;
                writer.Write(0);
                offsets[i] = storage.Position;
                lengths[i] = AnimationFrameCodec.EncodeTo(pixels, storage);
                var end = storage.Position;
                storage.Position = lengthPosition; writer.Write(lengths[i]); storage.Position = end;
            }
            if (input.Position != input.Length) throw new InvalidDataException("Frame pack trailing data");
            storage.Flush();
            return new CompressedBitmapFrames(storage, offsets, lengths, new Size(width, height), lengths.Sum(x => (long)x)) { ReusablePackPath = path };
        }
        catch { storage.Dispose(); throw; }
    }
}
