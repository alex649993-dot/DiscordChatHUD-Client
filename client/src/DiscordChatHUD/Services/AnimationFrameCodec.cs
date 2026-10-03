using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using K4os.Compression.LZ4;

namespace DiscordChatHUD.Services;

// Independent lossless frames: seeking/looping never replays GIF disposal or
// accumulates deltas. The pixels and original timeline stay unchanged.
internal static class AnimationFrameCodec
{
    // Unlike the shared pool's thread-local caches, this bounded pool does not
    // retain another large buffer for each load-worker thread that runs a decode.
    private static readonly ArrayPool<byte> ScratchPool = ArrayPool<byte>.Create(1024 * 1024, 2);
    private static int _decoderWarmed;

    // Relay frame packs arrive already LZ4-compressed. Pay the one-time JIT cost
    // when the HUD media service starts instead of on the first uploaded GIF.
    // The payload is a valid 4x4 transparent BGRA frame produced by this codec.
    public static void WarmUpDecoder()
    {
        if (Interlocked.Exchange(ref _decoderWarmed, 1) != 0) return;
        try
        {
            ReadOnlySpan<byte> payload = [
                0x40, 0x00, 0x00, 0x00, 0x01, 0x1F, 0x00, 0x01,
                0x00, 0x27, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00
            ];
            Span<byte> pixels = stackalloc byte[64];
            Decode(payload, pixels);
        }
        catch { /* Warm-up must never affect media availability. */ }
    }

    public static byte[] Encode(ReadOnlySpan<byte> pixels)
    {
        var planar = ScratchPool.Rent(pixels.Length);
        byte[]? output = null;
        try
        {
            output = ScratchPool.Rent(checked(5 + LZ4Codec.MaximumOutputSize(pixels.Length)));
            var planes = pixels.Length % 4 == 0;
            if (planes) SplitPlanes(pixels, planar);
            else pixels.CopyTo(planar);
            BinaryPrimitives.WriteInt32LittleEndian(output, pixels.Length);
            output[4] = planes ? (byte)1 : (byte)0;
            var written = LZ4Codec.Encode(planar.AsSpan(0, pixels.Length), output.AsSpan(5));
            if (written <= 0 && pixels.Length != 0) throw new InvalidDataException("Animation compression failed.");
            return output.AsSpan(0, 5 + Math.Max(0, written)).ToArray();
        }
        finally
        {
            ScratchPool.Return(planar);
            if (output is not null) ScratchPool.Return(output);
        }
    }

    // Stream the pooled encoded buffer directly: no per-frame payload array.
    public static int EncodeTo(ReadOnlySpan<byte> pixels, Stream destination)
    {
        var planar = ScratchPool.Rent(pixels.Length);
        byte[]? output = null;
        try
        {
            output = ScratchPool.Rent(checked(5 + LZ4Codec.MaximumOutputSize(pixels.Length)));
            var planes = pixels.Length % 4 == 0;
            if (planes) SplitPlanes(pixels, planar); else pixels.CopyTo(planar);
            BinaryPrimitives.WriteInt32LittleEndian(output, pixels.Length);
            output[4] = planes ? (byte)1 : (byte)0;
            var written = LZ4Codec.Encode(planar.AsSpan(0, pixels.Length), output.AsSpan(5));
            if (written <= 0 && pixels.Length != 0) throw new InvalidDataException("Animation compression failed.");
            var length = 5 + Math.Max(0, written);
            destination.Write(output.AsSpan(0, length));
            return length;
        }
        finally
        {
            ScratchPool.Return(planar);
            if (output is not null) ScratchPool.Return(output);
        }
    }

    public static void Decode(ReadOnlySpan<byte> payload, Span<byte> pixels)
    {
        if (payload.Length < 5 || BinaryPrimitives.ReadInt32LittleEndian(payload) != pixels.Length
            || payload[4] > 1 || (payload[4] == 1 && pixels.Length % 4 != 0))
            throw new InvalidDataException("Unexpected animation frame length or format.");
        if (pixels.Length == 0) return;
        if (payload[4] == 0)
        {
            if (LZ4Codec.Decode(payload.Slice(5), pixels) != pixels.Length)
                throw new InvalidDataException("Invalid animation frame payload.");
            return;
        }
        var planar = ScratchPool.Rent(pixels.Length);
        try
        {
            if (LZ4Codec.Decode(payload.Slice(5), planar.AsSpan(0, pixels.Length)) != pixels.Length)
                throw new InvalidDataException("Invalid animation frame payload.");
            JoinPlanes(planar.AsSpan(0, pixels.Length), pixels);
        }
        finally { ScratchPool.Return(planar); }
    }

    private static void SplitPlanes(ReadOnlySpan<byte> pixels, Span<byte> planes)
    {
        var count = pixels.Length / 4;
        var i = 0;
        if (Ssse3.IsSupported)
        {
            ref var source = ref MemoryMarshal.GetReference(pixels);
            ref var destination = ref MemoryMarshal.GetReference(planes);
            var shuffle = Vector128.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15);
            for (; i <= count - 16; i += 16)
            {
                var a = Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(i * 4)), shuffle).AsUInt32();
                var b = Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(i * 4 + 16)), shuffle).AsUInt32();
                var c = Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(i * 4 + 32)), shuffle).AsUInt32();
                var d = Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(i * 4 + 48)), shuffle).AsUInt32();
                var abLow = Sse2.UnpackLow(a, b).AsUInt64();
                var abHigh = Sse2.UnpackHigh(a, b).AsUInt64();
                var cdLow = Sse2.UnpackLow(c, d).AsUInt64();
                var cdHigh = Sse2.UnpackHigh(c, d).AsUInt64();
                Sse2.UnpackLow(abLow, cdLow).AsByte().StoreUnsafe(ref destination, (nuint)i);
                Sse2.UnpackHigh(abLow, cdLow).AsByte().StoreUnsafe(ref destination, (nuint)(count + i));
                Sse2.UnpackLow(abHigh, cdHigh).AsByte().StoreUnsafe(ref destination, (nuint)(count * 2 + i));
                Sse2.UnpackHigh(abHigh, cdHigh).AsByte().StoreUnsafe(ref destination, (nuint)(count * 3 + i));
            }
        }
        for (; i < count; i++)
        {
            planes[i] = pixels[i * 4];
            planes[count + i] = pixels[i * 4 + 1];
            planes[count * 2 + i] = pixels[i * 4 + 2];
            planes[count * 3 + i] = pixels[i * 4 + 3];
        }
    }

    private static void JoinPlanes(ReadOnlySpan<byte> planes, Span<byte> pixels)
    {
        var count = pixels.Length / 4;
        var i = 0;
        if (Sse2.IsSupported)
        {
            ref var source = ref MemoryMarshal.GetReference(planes);
            ref var destination = ref MemoryMarshal.GetReference(pixels);
            for (; i <= count - 16; i += 16)
            {
                var b = Vector128.LoadUnsafe(ref source, (nuint)i);
                var g = Vector128.LoadUnsafe(ref source, (nuint)(count + i));
                var r = Vector128.LoadUnsafe(ref source, (nuint)(count * 2 + i));
                var a = Vector128.LoadUnsafe(ref source, (nuint)(count * 3 + i));
                var bgLow = Sse2.UnpackLow(b, g).AsUInt16();
                var bgHigh = Sse2.UnpackHigh(b, g).AsUInt16();
                var raLow = Sse2.UnpackLow(r, a).AsUInt16();
                var raHigh = Sse2.UnpackHigh(r, a).AsUInt16();
                Sse2.UnpackLow(bgLow, raLow).AsByte().StoreUnsafe(ref destination, (nuint)(i * 4));
                Sse2.UnpackHigh(bgLow, raLow).AsByte().StoreUnsafe(ref destination, (nuint)(i * 4 + 16));
                Sse2.UnpackLow(bgHigh, raHigh).AsByte().StoreUnsafe(ref destination, (nuint)(i * 4 + 32));
                Sse2.UnpackHigh(bgHigh, raHigh).AsByte().StoreUnsafe(ref destination, (nuint)(i * 4 + 48));
            }
        }
        for (; i < count; i++)
        {
            pixels[i * 4] = planes[i];
            pixels[i * 4 + 1] = planes[count + i];
            pixels[i * 4 + 2] = planes[count * 2 + i];
            pixels[i * 4 + 3] = planes[count * 3 + i];
        }
    }
}
