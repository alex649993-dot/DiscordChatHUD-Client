using System.Buffers;
using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace DiscordChatHUD.Services;

// MediaCache — 한 미디어를 받아 해독하고 캐시에 넣는 전체 흐름(LoadAsync).
internal sealed partial class MediaCache
{
    private async Task LoadAsync(string cacheKey, IReadOnlyList<string> candidateUrls, bool lowByteLimit,
        bool expectAnimation = false, bool relayAnimationProbe = false, bool isSticker = false)
    {
        var loadStarted = AnimationClock.NowMilliseconds;
        var useAnimationRelay = expectAnimation || relayAnimationProbe;
        using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        // A relay animation probe is still an animation candidate from the user's
        // point of view. Show a cheap static rendition while Relay decides/decodes.
        var previewTask = useAnimationRelay ? LoadStillPreviewAsync(cacheKey, candidateUrls, previewStop.Token, isSticker) : Task.CompletedTask;
        var knownAnimatedMedia = expectAnimation;
        DecodedMedia? stillFallback = null;
        try
        {
            Exception? lastError = null;
            var usable = candidateUrls.Where(url => !IsRawVideoCandidate(url)).ToArray();
            if (usable.Length == 0) throw new InvalidDataException("No image thumbnail candidate; raw video download skipped.");
            var fastProxy = IsDiscordImageProxy(usable[0]) && usable.Skip(1).Any(url => !IsDiscordImageProxy(url));
            // Embed Url may itself be the first proxy, so MediaItem.Url is not
            // a reliable original. Use the supplied candidate hosts instead.
            // Keep the first resized/animated candidate and its cache key; after
            // its short attempt, escape the same proxy before trying more renditions.
            var ordered = fastProxy
                ? usable.Take(1).Concat(usable.Skip(1).Where(url => !IsDiscordImageProxy(url)))
                    .Concat(usable.Skip(1).Where(IsDiscordImageProxy)).ToArray()
                : usable;
            if (useAnimationRelay)
            {
                var relayProxies = BuildRelayAnimationUrls(ordered, isSticker);
                if (relayProxies.Count > 0) ordered = relayProxies.Concat(ordered).Distinct(StringComparer.Ordinal).ToArray();
            }
            // Still images and video thumbnails may race up to three sources. Animated
            // candidates remain ordered so a static rendition cannot win over a GIF.
            await foreach (var download in DownloadCandidatesAsync(ordered, lowByteLimit, useAnimationRelay,
                               () => HasSupersedingCandidates(cacheKey, candidateUrls), cacheKey))
            {
                using var ownedDownload = download;
                var url = download.Url;
                try
                {
                    var queuedAt = download.QueuedAt;
                    var downloadStarted = download.StartedAt;
                    var downloadedAt = download.CompletedAt;
                    var memory = download.Data;
                    bool adoptedStreaming;
                    string? streamingPath = null;
                    lock (_gate)
                    {
                        adoptedStreaming = download.StreamedEntry is { } streamed
                            && _cache.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, streamed)
                            && current.Frames is CompressedBitmapFrames { TransferComplete: true };
                        if (adoptedStreaming) streamingPath = ((CompressedBitmapFrames)download.StreamedEntry!.Frames).ReusablePackPath;
                    }
                    if (adoptedStreaming)
                    {
                        previewStop.Cancel();
                        if (streamingPath is not null && _frameDiskCache is not null && FrameDiskKey(url) is { } streamKey
                            && (!streamKey.StartsWith("hq2\n", StringComparison.Ordinal) || CompressedBitmapFrames.IsHighQualityPack(memory)))
                        {
                            // Preserve immediate persistent reuse for tiny LZ4 packs.
                            // Larger/WebP packs keep the low-priority disk writer.
                            if (memory.Length <= 64 * 1024 && !CompressedBitmapFrames.IsWebpPack(memory))
                                _frameDiskCache.Write(streamKey, memory);
                            else _ = _frameDiskCache.WriteFileThrottledAsync(streamKey, streamingPath, _stop.Token);
                        }
                        AppLog.Info($"GIF 점진 수신 완료: {AnimationClock.NowMilliseconds-loadStarted}ms · 재생 위치 유지");
                        return;
                    }
                    var smallStillImage = IsSmallStillImage(memory);
                    var localAnimationFallback = useAnimationRelay && !IsRelayMediaProxy(url) && !smallStillImage;
                    var lightweightAnimationFallback = localAnimationFallback && IsLightweightAnimationFallback(memory);
                    var decodeGate = localAnimationFallback
                        ? lightweightAnimationFallback ? _decodeGate : _animationFallbackDecodeGate
                        : smallStillImage ? _smallStillDecodeGate : _decodeGate;
                    if (lightweightAnimationFallback)
                        AppLog.Info($"직접 애니메이션 빠른 해독 경로: {SafeHost(url)} · {memory.Length}bytes");
                    await decodeGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                    var decodeStarted = AnimationClock.NowMilliseconds;
                    string? deferredFrameKey = null;
                    string? deferredFramePath = null;
                    try
                    {
                    _stop.Token.ThrowIfCancellationRequested();
                    // Retain this fact even when the primary decoder throws.
                    // A later PNG response is a fallback, not animation success.
                    bool framePack = CompressedBitmapFrames.IsPack(memory);
                    // Persist decoded WebP frames as LZ4, not the wire format, so
                    // the next load can adopt the pack without repeating conversion.
                    bool webpPack = framePack && CompressedBitmapFrames.IsWebpPack(memory);
                    if (IsRelayMediaProxy(url) && !framePack) throw new InvalidDataException("Server frame format unavailable");
                    knownAnimatedMedia |= framePack || HasMultipleWebpAnimationFrames(memory)
                                          || ReadGifFrameDelays(memory).Count > 1;
                    if (framePack && !webpPack && !download.DiskHit && _frameDiskCache is not null && FrameDiskKey(url) is { } frameKey
                        && (!frameKey.StartsWith("hq2\n", StringComparison.Ordinal) || CompressedBitmapFrames.IsHighQualityPack(memory)))
                    {
                        if (memory is FileStream packFile && !webpPack)
                        {
                            deferredFrameKey = frameKey;
                            deferredFramePath = packFile.Name;
                        }
                        else
                        {
                            // Only tiny packs use memory storage; their cache write
                            // is already too small to create the high-quality GIF peak.
                            _frameDiskCache.Write(frameKey, memory);
                        }
                    }

                    bool adoptedFramePack = false;
                    DecodedMedia decoded;
                    if (framePack && memory is FileStream && !webpPack)
                    {
                        var ownedPack = download.TakeData();
                        try
                        {
                            decoded = ImportFramePack(ownedPack, takeOwnership: true);
                            adoptedFramePack = true;
                        }
                        catch { ownedPack.Dispose(); throw; }
                    }
                    else
                    {
                        decoded = DecodeMediaWithPreview(memory, first =>
                        {
                            // The decoder owns the borrowed frame; the cache owns this copy.
                            Add(cacheKey, new DecodedMedia([new Bitmap(first)], [0], 0));
                            if (!_disposed)
                            {
                                try { MediaReady?.Invoke(); }
                                catch (Exception ex) { AppLog.Warn($"미디어 알림 실패: {ex.GetType().Name}"); }
                            }
                        });
                    }
                    if (webpPack && decoded.Frames is CompressedBitmapFrames { ReusablePackPath: { } reusablePath }
                        && _frameDiskCache is not null && FrameDiskKey(url) is { } reusableKey
                        && (!reusableKey.StartsWith("hq2\n", StringComparison.Ordinal) || CompressedBitmapFrames.IsHighQualityPack(memory)))
                    {
                        // Also upgrade old WebP disk entries on their next use.
                        deferredFrameKey = reusableKey;
                        deferredFramePath = reusablePath;
                    }
                    // Decoders have detached their pixels. Release the encoded
                    // download before compression adds its own frame payloads.
                    if (!adoptedFramePack) memory.SetLength(0);
                    if (relayAnimationProbe && !expectAnimation && framePack && decoded.Frames.Count == 1)
                        knownAnimatedMedia = false;
                    if (knownAnimatedMedia && decoded.Frames.Count == 1)
                    {
                        if (stillFallback is null) stillFallback = decoded;
                        else decoded.Dispose();
                        continue;
                    }
                    if (decoded.Frames.Count > 1)
                    {
                        var duration = decoded.FrameDelays.Aggregate(
                            0L,
                            (total, delay) => total + delay);
                        AppLog.Info(
                            $"애니메이션 미디어 로드: {SafeHost(url)} · "
                            + $"{decoded.Frames.Count}프레임 / {duration}ms");
                    }
                    previewStop.Cancel();
                    lock (_gate) Add(cacheKey, decoded);
                    }
                    finally { decodeGate.Release(); }
                    if (deferredFrameKey is not null && deferredFramePath is not null && _frameDiskCache is not null)
                        _ = _frameDiskCache.WriteFileThrottledAsync(deferredFrameKey, deferredFramePath, _stop.Token);
                    AppLog.Info($"미디어 로딩 시간: {SafeHost(url)} · 전체 {AnimationClock.NowMilliseconds - loadStarted}ms / 다운로드대기 {downloadStarted - queuedAt}ms / 전송 {downloadedAt - downloadStarted}ms / 해독대기 {decodeStarted - downloadedAt}ms / 해독 {AnimationClock.NowMilliseconds - decodeStarted}ms");
                    if (IsRelayMediaProxy(url)) AppLog.Info("서버 무손실 프레임 적용 완료 · 클라이언트 GIF 합성 생략");
                    if (!_disposed)
                    {
                        // A UI subscriber failure must not discard a successful
                        // decode and restart the whole candidate/download loop.
                        try { MediaReady?.Invoke(); }
                        catch (Exception ex) { AppLog.Warn($"미디어 알림 실패: {ex.GetType().Name}"); }
                    }
                    return;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (download.DiskHit && FrameDiskKey(url) is { } badKey) _frameDiskCache?.Remove(badKey);
                    // Keep the decoder error rather than hiding it behind a
                    // successful PNG fallback. Never log the signed query.
                    AppLog.Warn($"미디어 후보 실패: {SafeHost(url)} · {ex.Message.Split('\n')[0]}");
                }
}
            // Prefer the renewed URLs over settling for a still fallback.
            if (HasSupersedingCandidates(cacheKey, candidateUrls)) throw new CandidatesSupersededException();
            if (stillFallback is not null)
            {
                var fallback = stillFallback;
                stillFallback = null;
                Add(cacheKey, fallback);
                AppLog.Warn($"애니메이션 정지 대체본만 표시됨: {SafeHost(cacheKey)} · 원본 디코더 오류 로그 확인");
                if (!_disposed)
                {
                    try { MediaReady?.Invoke(); }
                    catch (Exception ex) { AppLog.Warn($"미디어 알림 실패: {ex.GetType().Name}"); }
                }
                return;
            }
            throw new InvalidDataException(
                $"모든 미디어 후보({candidateUrls.Count}개) 로드 실패",
                lastError);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Normal shutdown: no retry/negative-cache entry or error log.
        }
        catch (CandidatesSupersededException)
        {
            // Not a failure: the finally block below restarts with the renewed URLs.
        }
        catch (Exception ex)
        {
            RememberNegative(
                cacheKey,
                lowByteLimit ? TimeSpan.FromSeconds(12) : TimeSpan.FromMinutes(1), candidateUrls);
            AppLog.Warn($"미디어 불러오기 실패: {SafeHost(cacheKey)} · {ex.GetBaseException().Message}");
        }
        finally
        {
            previewStop.Cancel();
            await previewTask.ConfigureAwait(false);
            stillFallback?.Dispose();

            lock (_gate)
            {
                _inflight.Remove(cacheKey);
                if (_inflightCandidates.Remove(cacheKey, out var latest) && !_disposed
                    && !_cache.ContainsKey(cacheKey) && HasNewCandidate(latest, candidateUrls))
                {
                    // Embeds may arrive while the initial direct URL is still failing.
                    _negative.Remove(cacheKey);
                    _inflight.Add(cacheKey);
                    _inflightCandidates[cacheKey] = latest;
                    _ = Task.Run(() => LoadAsync(cacheKey, latest, lowByteLimit, expectAnimation, relayAnimationProbe, isSticker));
                }
                if (_disposed && _inflight.Count == 0)
                {
                    _stop.Dispose();
                    _mediaLoadGate.Dispose();
                    _decodeGate.Dispose();
                    _smallStillDecodeGate.Dispose();
                }
            }
        }
    }

    private static Stream CreateDownloadStorage(long? length)
    {
        if (length is > 0 and <= 256 * 1024) return new MemoryStream((int)length.Value);
        var path = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-download-" + Guid.NewGuid().ToString("N") + ".tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
    }

    private static bool IsSmallStillImage(Stream stream)
    {
        if (CompressedBitmapFrames.IsPack(stream)) return false;
        try
        {
            stream.Position = 0;
            var info = ImageSharpImage.Identify(new DecoderOptions { Configuration = DecoderConfiguration, MaxFrames = 2 }, stream);
            // Routing only: larger images keep their existing quality and acceptance rules.
            return info is not null && info.FrameMetadataCollection.Count <= 1 && (long)info.Width * info.Height <= 1_000_000;
        }
        catch { return false; }
        finally { stream.Position = 0; }
    }

    internal static bool CanPreemptRelay(Stream stream)
    {
        // Identify metadata only. Bound both encoded size and cumulative pixel work;
        // compressed byte size alone badly underestimates long GIF decode cost.
        if (stream.Length is <= 0 or > 512 * 1024) return false;
        try
        {
            stream.Position = 0;
            var info = ImageSharpImage.Identify(new DecoderOptions
                { Configuration = DecoderConfiguration, MaxFrames = 33 }, stream);
            return info is not null && info.FrameMetadataCollection.Count is > 0 and <= 32
                && (long)info.Width * info.Height * info.FrameMetadataCollection.Count <= 4_000_000;
        }
        catch { return false; }
        finally { stream.Position = 0; }
    }

    internal static bool IsLightweightAnimationFallback(Stream stream)
    {
        if (stream.Length <= 0 || stream.Length > MaxConcurrentAnimationFallbackBytes
            || CompressedBitmapFrames.IsPack(stream)) return false;
        try
        {
            stream.Position = 0;
            var info = ImageSharpImage.Identify(
                new DecoderOptions { Configuration = DecoderConfiguration, MaxFrames = 2 },
                stream);
            return info is not null
                   && (long)info.Width * info.Height <= MaxConcurrentAnimationFallbackPixels;
        }
        catch { return false; }
        finally { stream.Position = 0; }
    }
}
