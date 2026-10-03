using System.Buffers.Binary;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace DiscordChatHUD.Services;

internal sealed partial class CompressedBitmapFrames
{
    // All reads/writes and publication are serialized by MediaCache._gate.
    internal bool IsProgressive { get; private init; }
    internal bool TransferComplete { get; private set; }
    private int _availableFrames;
    internal int AvailableFrames => IsProgressive ? _availableFrames : Count;

    internal static CompressedBitmapFrames BeginProgressive(PackStreamReader reader)
    {
        var path = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-stream-" + Guid.NewGuid().ToString("N") + ".tmp");
        var storage = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        try
        {
            using var writer = new BinaryWriter(storage, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(reader.HighQuality ? HighQualityPackMagic : PackMagic);
            writer.Write(reader.Size.Width); writer.Write(reader.Size.Height);
            writer.Write(reader.Delays.Length); writer.Write(reader.Representative);
            foreach (var delay in reader.Delays) writer.Write(delay);
            return new(storage, new long[reader.Delays.Length], new int[reader.Delays.Length], reader.Size, 0)
                { IsProgressive = true, ReusablePackPath = path };
        }
        catch { storage.Dispose(); throw; }
    }

    internal void AppendProgressive(byte[] payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsProgressive || TransferComplete || _availableFrames >= Count) throw new InvalidOperationException();
        _storage.Position = _storage.Length;
        using var writer = new BinaryWriter(_storage, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(payload.Length);
        _offsets[_availableFrames] = _storage.Position;
        _lengths[_availableFrames] = payload.Length;
        writer.Write(payload);
        _availableFrames++;
    }

    internal void CompleteProgressive()
    {
        if (_availableFrames != Count) throw new InvalidDataException("Incomplete frame pack");
        _storage.Flush(); TransferComplete = true;
    }

    // Parse complete records only. Existing relay248 packs already contain the
    // full timeline followed by independent frames; no protocol change is needed.
    internal sealed class PackStreamReader
    {
        internal Size Size { get; private init; }
        internal int[] Delays { get; private init; } = [];
        internal int Representative { get; private init; }
        internal bool HighQuality { get; private init; }
        private bool Webp { get; init; }
        private bool Delta { get; init; }
        private byte[]? _previousPixels;
        private long _position;
        internal int Received { get; private set; }
        internal static PackStreamReader? TryHeader(Stream input)
        {
            if (input.Length > MaxPackBytes) throw new InvalidDataException("Frame pack size");
            if (input.Length < 24) return null;
            input.Position = 0;
            using var r = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
            var magic = r.ReadBytes(8);
            bool delta = DeltaMagic(magic);
            bool webp = magic.AsSpan().SequenceEqual(WebpPackMagic) || magic.AsSpan().SequenceEqual(HighQualityWebpPackMagic);
            bool hq = magic.AsSpan().SequenceEqual(HighQualityPackMagic) || magic.AsSpan().SequenceEqual(HighQualityWebpPackMagic) || magic.AsSpan().SequenceEqual(HighQualityDeltaPackMagic);
            if (!delta && !webp && !hq && !magic.AsSpan().SequenceEqual(PackMagic)) throw new InvalidDataException("Frame pack version");
            int width=r.ReadInt32(), height=r.ReadInt32(), count=r.ReadInt32(), representative=r.ReadInt32();
            if (width < 1 || width > (hq ? 800 : 640) || height < 1 || height > (hq ? 800 : 640)
                || count < 2 || count > (hq ? 240 : 180) || representative < 0 || representative >= count
                || (long)width * height * count * 4 > (hq ? 120_000_000 : 60_000_000))
                throw new InvalidDataException("Frame pack dimensions");
            if (input.Length < 24L + count * 4) return null;
            var delays = new int[count];
            for (int i=0; i<count; i++)
            {
                delays[i]=r.ReadInt32();
                if (delays[i] < 10 || delays[i] > 3_600_000) throw new InvalidDataException("Frame pack timeline");
            }
            return new() { Size=new(width,height), Delays=delays, Representative=representative,
                HighQuality=hq, Webp=webp, Delta=delta, _position=input.Position };
        }
        internal byte[]? ReadNext(Stream input)
        {
            if (input.Length > MaxPackBytes) throw new InvalidDataException("Frame pack size");
            if (Received == Delays.Length || input.Length - _position < 4) return null;
            input.Position = _position;
            using var r = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
            int length = r.ReadInt32(), frameBytes = checked(Size.Width * Size.Height * 4);
            int maximum = Delta ? frameBytes + 4097 : Webp ? frameBytes + 4096 : 5 + K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(frameBytes);
            if (length < (Delta ? 21 : Webp ? 20 : 5) || length > maximum) throw new InvalidDataException("Frame pack block");
            if (input.Length - input.Position < length) return null;
            var payload = r.ReadBytes(length);
            if (Delta)
            {
                _previousPixels=DecodeDelta(payload,Size,_previousPixels);
                payload=AnimationFrameCodec.Encode(_previousPixels);
            }
            else if (Webp)
            {
                var options = new DecoderOptions { Configuration=WebpConfiguration, MaxFrames=1 };
                using var image = SixLabors.ImageSharp.Image.Load<Bgra32>(options, payload);
                if (image.Width != Size.Width || image.Height != Size.Height || image.Frames.Count != 1)
                    throw new InvalidDataException("Frame pack WebP frame");
                var pixels = new byte[frameBytes]; image.CopyPixelDataTo(pixels);
                payload = AnimationFrameCodec.Encode(pixels);
            }
            else if (BinaryPrimitives.ReadInt32LittleEndian(payload) != frameBytes || payload[4] > 1)
                throw new InvalidDataException("Frame pack payload header");
            _position += 4L + length; Received++;
            if (Received == Delays.Length) _previousPixels=null;
            return payload;
        }
        internal void ValidateEnd(Stream input)
        {
            if (Received != Delays.Length || input.Length != _position) throw new InvalidDataException("Incomplete or trailing frame pack data");
        }
    }
}
