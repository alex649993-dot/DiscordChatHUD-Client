namespace DiscordChatHUD.Services;

internal sealed partial class MediaCache
{
    // Queries used before drawing must not select/decompress a frame or mark an
    // animation visible. In particular, a clipped author header can be queried
    // while its message body remains inside the viewport.
    internal Size? TryGetRenderFrameSize(string url)
    {
        lock (_gate)
            return !_disposed && _cache.TryGetValue(url, out var entry) ? entry.FrameSize : null;
    }

    internal bool TryGetStaticRenderSourceId(string url, out long sourceId)
    {
        sourceId = 0;
        lock (_gate)
        {
            if (_disposed || !_cache.TryGetValue(url, out var entry) || entry.IsAnimated) return false;
            TouchLocked(url, entry);
            entry.LastAccessRenderPassId = _renderPassId;
            entry.LastAccessTick = AnimationClock.NowMilliseconds;
            sourceId = entry.FrameSourceId;
            return true;
        }
    }
}
