namespace DiscordChatHUD.Services;

// Relay237: lets the worker report a single-frame image separately from a real
// decoder error. Only the relay worker exports frame packs.
internal sealed class NotAnimatedMediaException() : Exception("Not an animation");

internal sealed partial class MediaCache
{
    /// <summary>Where one relay export spent its time, and the shape of what it produced.</summary>
    /// <param name="LoadMs">Inside ImageSharp's decoder — not ours to parallelize.</param>
    /// <param name="FramesMs">Our scale/score/compress loop over the decoded frames.</param>
    internal readonly record struct FramePackExport(long LoadMs, long FramesMs, long PackMs, int Frames, int Width, int Height, int SourceFrames = 0);

    /// <summary>Relay worker: decode an animation and write a DHSFRM frame pack.</summary>
    internal static FramePackExport ExportFramePack(Stream source, Stream output, bool highQuality = false, bool compact = false, LiveFrameExport? live = null)
    {
        var previous = ExportProfile.Value;
        var previousLive = LiveExport.Value;
        LiveExport.Value = live;
        ExportProfile.Value = highQuality ? ExportProfileHighQuality : compact ? ExportProfileCompact : ExportProfileStandard;
        try
        {
            SourceLoadMilliseconds = 0;
            SourceFrameCount = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var decoded = DecodeMedia(source);
            var loadMs = SourceLoadMilliseconds;
            var framesMs = Math.Max(0, watch.ElapsedMilliseconds - loadMs);
            if (decoded.Frames.Count < 2) throw new NotAnimatedMediaException();
            watch.Restart();
            Size size;
            if (decoded.Frames is CompressedBitmapFrames packed)
            {
                size = packed.FrameSize;
                packed.WritePack(output, decoded.FrameDelays, decoded.RepresentativeFrameIndex, highQuality);
            }
            else
            {
                size = decoded.Frames[0].Size;
                using var frames = CompressedBitmapFrames.CreateForStorage(decoded.Frames);
                frames.WritePack(output, decoded.FrameDelays, decoded.RepresentativeFrameIndex, highQuality);
            }
            return new(loadMs, framesMs, watch.ElapsedMilliseconds, decoded.Frames.Count, size.Width, size.Height, SourceFrameCount);
        }
        finally { ExportProfile.Value = previous; LiveExport.Value = previousLive; }
    }

    /// <summary>Relay: rewrite a cached LZ4 frame pack as the smaller lossless WebP pack.</summary>
    internal static void TranscodeFramePackToWebp(Stream lz4Pack, Stream output)
        => CompressedBitmapFrames.TranscodePackToWebp(lz4Pack, output, RelayDecodeParallelism);

    private static DecodedMedia ImportFramePack(Stream source, bool takeOwnership = false)
    {
        var packed = CompressedBitmapFrames.ReadPack(source, out var delays, out var representative, takeOwnership);
        return new DecodedMedia(packed, delays, representative) { CompressionEvaluated = true };
    }
}
