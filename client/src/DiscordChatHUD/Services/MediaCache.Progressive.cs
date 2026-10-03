using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

internal sealed partial class MediaCache
{
    private sealed class ProgressiveDownload(MediaCache owner, string key, long started) : IDisposable
    {
        private CompressedBitmapFrames.PackStreamReader? _reader;
        private CompressedBitmapFrames? _frames;
        internal CacheEntry? Entry { get; private set; }
        private bool _abandoned;
        internal async Task FeedAsync(Stream input, bool final, CancellationToken token)
        {
            if (_abandoned) return;
            var end = input.Length;
            bool notify = false;
            await owner._decodeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _reader ??= CompressedBitmapFrames.PackStreamReader.TryHeader(input);
                if (_reader is null)
                {
                    if (final) throw new InvalidDataException("Incomplete frame pack header");
                    return;
                }
                _frames ??= CompressedBitmapFrames.BeginProgressive(_reader);
                while (_reader.ReadNext(input) is { } payload)
                {
                    token.ThrowIfCancellationRequested();
                    lock (owner._gate)
                    {
                        if (owner._disposed || (Entry is not null && (!owner._cache.TryGetValue(key, out var current) || !ReferenceEquals(current, Entry))))
                        { _abandoned = true; return; }
                        if (Entry is not null) notify |= Entry.BeforeStreamingAppend(AnimationClock.NowMilliseconds);
                        _frames.AppendProgressive(payload);
                        if (Entry is null && _frames.AvailableFrames >= 2)
                        {
                            owner.Add(key, new DecodedMedia(_frames, _reader.Delays, _reader.Representative) { CompressionEvaluated=true });
                            if (!owner._cache.TryGetValue(key, out var published) || !ReferenceEquals(published.Frames, _frames))
                            { _abandoned=true; return; }
                            Entry = published;
                            notify = true;
                            AppLog.Info($"GIF 점진 재생 준비: {AnimationClock.NowMilliseconds-started}ms · {_frames.AvailableFrames}/{_frames.Count}프레임");
                        }
                    }
                }
                if (final)
                {
                    _reader.ValidateEnd(input);
                    lock (owner._gate)
                    {
                        if (owner._disposed || Entry is null || !owner._cache.TryGetValue(key,out var current) || !ReferenceEquals(current,Entry))
                        { _abandoned=true; return; }
                        notify |= Entry.BeforeStreamingAppend(AnimationClock.NowMilliseconds);
                        _frames.CompleteProgressive();
                    }
                }
            }
            finally
            {
                input.Position = end;
                owner._decodeGate.Release();
                if (notify && !owner._disposed)
                {
                    try { owner.MediaReady?.Invoke(); }
                    catch(Exception ex) { AppLog.Warn($"미디어 알림 실패: {ex.GetType().Name}"); }
                }
            }
        }
        public void Dispose()
        {
            // Published frames belong to the cache. A failed download keeps its
            // last valid frame visible until an alternate source replaces it.
            if (Entry is null) _frames?.Dispose();
        }
    }
}
