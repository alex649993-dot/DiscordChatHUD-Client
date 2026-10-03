using System.Numerics;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;
namespace DiscordChatHUD.Services;
internal sealed partial class CompressedBitmapFrames
{
    private static ReadOnlySpan<byte> DeltaPackMagic => "DHSFRD01"u8;
    private static ReadOnlySpan<byte> HighQualityDeltaPackMagic => "DHSFRD02"u8;
    private static bool DeltaMagic(ReadOnlySpan<byte> magic) => magic.SequenceEqual(DeltaPackMagic) || magic.SequenceEqual(HighQualityDeltaPackMagic);

    // Optional server cache optimization. Reuse the existing full-frame WebP when
    // XOR against the preceding frame is larger. Only one batch of raw frames lives
    // in memory; the wire delta is converted back to independent LZ4 on the client.
    internal static void TranscodePackToDelta(Stream lz4Pack, Stream webpPack, Stream output, int parallelism, Func<bool>? shouldYield = null)
    {
        using var frames = ReadPack(lz4Pack, out var delays, out var representative);
        var header = PackStreamReader.TryHeader(webpPack) ?? throw new InvalidDataException("Missing base pack header");
        if (!IsWebpPack(webpPack) || header.Size != frames._size || !header.Delays.SequenceEqual(delays))
            throw new InvalidDataException("Delta base mismatch");
        webpPack.Position = 24 + 4 * frames.Count;
        using var reader = new BinaryReader(webpPack, System.Text.Encoding.UTF8, true);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true);
        writer.Write(IsHighQualityPack(lz4Pack) ? HighQualityDeltaPackMagic : DeltaPackMagic);
        writer.Write(frames._size.Width); writer.Write(frames._size.Height); writer.Write(frames.Count); writer.Write(representative);
        foreach (var delay in delays) writer.Write(delay);
        var frameBytes = checked(frames._size.Width * frames._size.Height * 4);
        var parallel = Math.Clamp(parallelism, 1, 4);
        var encoder = new WebpEncoder { FileFormat=WebpFileFormatType.Lossless, Method=WebpEncodingMethod.Level0, Quality=0, TransparentColorMode=WebpTransparentColorMode.Preserve };
        byte[]? previous = null;
        for (int start=0; start<frames.Count; start+=parallel*2)
        {
            if (shouldYield?.Invoke() == true) throw new OperationCanceledException("Foreground media takes priority");
            var count = Math.Min(parallel*2, frames.Count-start);
            var differences = new byte[count][];
            var full = new byte[count][];
            for (int k=0; k<count; k++)
            {
                int i=start+k, length=reader.ReadInt32();
                if (length<20 || length>frameBytes+4096 || length>webpPack.Length-webpPack.Position) throw new InvalidDataException("Delta base block");
                full[k]=reader.ReadBytes(length);
                var packed=new byte[frames._lengths[i]];
                frames._storage.Position=frames._offsets[i]; frames._storage.ReadExactly(packed);
                var pixels=new byte[frameBytes];
                if (frames._rawFrames) packed.CopyTo(pixels,0); else AnimationFrameCodec.Decode(packed,pixels);
                if (previous is not null) { var diff=(byte[])pixels.Clone(); XorPixels(diff,previous); differences[k]=diff; }
                previous=pixels;
            }
            var payloads=new byte[count][];
            Parallel.For(0,count,new ParallelOptions{MaxDegreeOfParallelism=parallel},k=>
            {
                byte mode=0; var selected=full[k];
                if (differences[k] is {} diff)
                {
                    using var image=SharpImage.LoadPixelData<Bgra32>(WebpConfiguration,diff,frames._size.Width,frames._size.Height);
                    using var memory=new MemoryStream(); image.Save(memory,encoder);
                    if (memory.Length<selected.Length) { selected=memory.ToArray(); mode=1; }
                }
                var payload=new byte[selected.Length+1];payload[0]=mode;selected.CopyTo(payload,1);payloads[k]=payload;
            });
            foreach(var payload in payloads){writer.Write(payload.Length);writer.Write(payload);}
        }
        if (webpPack.Position!=webpPack.Length) throw new InvalidDataException("Delta base trailing data");
    }

    private static void XorPixels(Span<byte> pixels, ReadOnlySpan<byte> previous)
    {
        int i=0;
        for (; i<=pixels.Length-Vector<byte>.Count; i+=Vector<byte>.Count)
            (new Vector<byte>(pixels.Slice(i)) ^ new Vector<byte>(previous.Slice(i))).CopyTo(pixels.Slice(i));
        for (;i<pixels.Length;i++) pixels[i]^=previous[i];
    }
    private static byte[] DecodeDelta(byte[] payload, Size size, byte[]? previous)
    {
        if(payload.Length<21 || payload[0]>1 || (payload[0]==1 && previous is null)) throw new InvalidDataException("Delta frame mode");
        var options=new DecoderOptions{Configuration=WebpConfiguration,MaxFrames=1};
        // Check dimensions before allocating the decoded image.
        var info=SharpImage.Identify(options,payload.AsSpan(1));
        if(info.Width!=size.Width || info.Height!=size.Height)throw new InvalidDataException("Delta frame dimensions");
        using var image=SharpImage.Load<Bgra32>(options,payload.AsSpan(1));
        if(image.Frames.Count!=1)throw new InvalidDataException("Delta frame count");
        var pixels=new byte[checked(size.Width*size.Height*4)]; image.CopyPixelDataTo(pixels);
        if(payload[0]==1)XorPixels(pixels,previous!);
        return pixels;
    }
}
