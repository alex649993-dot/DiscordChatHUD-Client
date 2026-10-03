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

// MediaCache — 다운로드: 후보 순회, 새 주소 도착 시 재시작, 원본 미리 받기, 첫 미리보기.
internal sealed partial class MediaCache
{

    private sealed class DownloadedImage(string url, Stream data, long queuedAt, long startedAt, long completedAt, bool diskHit = false) : IDisposable
    {
        private Stream? _data = data;
        public string Url { get; } = url;
        public Stream Data => _data ?? throw new ObjectDisposedException(nameof(DownloadedImage));
        public long QueuedAt { get; } = queuedAt;
        public long StartedAt { get; } = startedAt;
        public long CompletedAt { get; } = completedAt;
        public bool DiskHit { get; } = diskHit;
        public CacheEntry? StreamedEntry { get; init; }
        public Stream TakeData()
        {
            var owned = _data ?? throw new ObjectDisposedException(nameof(DownloadedImage));
            _data = null;
            return owned;
        }
        public void Dispose() { _data?.Dispose(); _data = null; }
    }

    /// <summary>Set once a transfer has actually started arriving.</summary>
    private sealed class TransferProgress { internal volatile bool Started; }

    private async Task<DownloadedImage> DownloadImageAsync(string url, bool lowByteLimit, CancellationToken token,
        int timeoutMilliseconds = 20000, TransferProgress? progress = null, string? streamingKey = null)
    {
        var queuedAt = AnimationClock.NowMilliseconds;
        await _mediaLoadGate.WaitAsync(token).ConfigureAwait(false);
        Stream? data = null;
        try
        {
            var startedAt = AnimationClock.NowMilliseconds;
            string? diskKey = FrameDiskKey(url);
            if (diskKey is not null && _frameDiskCache is not null)
            {
                data = CreateDownloadStorage(null);
                if (_frameDiskCache.Read(diskKey, data))
                {
                    token.ThrowIfCancellationRequested();
                    if (progress is not null) progress.Started = true;
                    var hit = new DownloadedImage(url, data, queuedAt, startedAt, AnimationClock.NowMilliseconds, true);
                    data = null;
                    AppLog.Info($"서버 프레임 PC 캐시 재사용 · {hit.Data.Length}bytes / {hit.CompletedAt - startedAt}ms");
                    return hit;
                }
                data.Dispose(); data = null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsRelayMediaProxy(url))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _relayToken);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (IsRelayMediaProxy(url) && response.StatusCode == HttpStatusCode.NotFound
                && response.Headers.TryGetValues("X-HUD-Media-Failure", out var failures))
            {
                var code = failures.FirstOrDefault() ?? "unknown_failure";
                if (code.Length > 64 || code.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_'))) code = "invalid_failure_code";
                string? detail = null;
                if (response.Headers.TryGetValues("X-HUD-Media-Detail", out var details))
                {
                    detail = details.FirstOrDefault();
                    if (detail is not null && (detail.Length is 0 or > 64 || detail.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))) detail = null;
                }
                if (detail == RelayNotAnimatedDetail) AppLog.Info("중계 확인: 정지 이미지");
                else AppLog.Warn(detail is null ? $"중계 프레임 생성 실패: {code}" : $"중계 프레임 생성 실패: {code} · {detail}");
                throw new RelayMediaFailureException(code, response.StatusCode, detail);
            }
            response.EnsureSuccessStatusCode();
            var headersAt = AnimationClock.NowMilliseconds;
            // A relay body used to get its own 1200 ms total deadline, which killed
            // transfers that were slow but still arriving and sent them to the larger
            // original download. A stopped transfer is caught by the per-read timer below.
            var liveConversion = IsRelayMediaProxy(url) && response.Headers.Contains("X-HUD-Media-Streaming");
            timeout.CancelAfter(TimeSpan.FromMilliseconds(liveConversion ? 35_000 : 20_000));
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("Thumbnail endpoint returned video; body download skipped.");
            var limit = lowByteLimit ? MaxEmojiDownloadBytes : MaxDownloadBytes;
            if (response.Content.Headers.ContentLength is { } length && length > limit)
                throw new InvalidDataException("media download limit exceeded");
            data = CreateDownloadStorage(response.Content.Headers.ContentLength);
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            bool relayFrameResponse = IsRelayMediaProxy(url);
            var buffer = ArrayPool<byte>.Shared.Rent(relayFrameResponse ? 256 * 1024 : 64 * 1024);
            var relayBurstBytes = 0;
            using var progressive = relayFrameResponse && streamingKey is not null ? new ProgressiveDownload(this, streamingKey, startedAt) : null;
            try
            {
                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    if (timeoutMilliseconds < 20000) readTimeout.CancelAfter(liveConversion ? 5000 : 1500);
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (progress is not null) progress.Started = true;
                    if (data.Length + read > limit) throw new InvalidDataException("media download limit exceeded");
                    data.Write(buffer, 0, read);
                    if (progressive is not null) await progressive.FeedAsync(data, false, timeout.Token).ConfigureAwait(false);
                    if (relayFrameResponse)
                    {
                        relayBurstBytes += read;
                        if (relayBurstBytes >= 256 * 1024)
                        {
                            relayBurstBytes = 0;
                            // A fast relay can otherwise deliver a multi-megabyte
                            // pack quickly enough that copy/file I/O alone becomes
                            // the visible first-load CPU spike. Pace only relay
                            // frame packs; ordinary images keep their old latency.
                            await Task.Delay(3, timeout.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (progressive is not null) await progressive.FeedAsync(data, true, timeout.Token).ConfigureAwait(false);
            data.Flush();
            data.Position = 0;
            if (IsRelayMediaProxy(url))
            {
                string serverTiming = response.Headers.TryGetValues("Server-Timing", out var timings)
                    ? string.Join(",", timings).Replace('\r', ' ').Replace('\n', ' ') : "unavailable";
                if (serverTiming.Length > 120) serverTiming = serverTiming[..120];
                AppLog.Info($"서버 프레임 전송 · 응답대기 {headersAt - startedAt}ms / 본문 {AnimationClock.NowMilliseconds - headersAt}ms / {data.Length}bytes / {serverTiming}");
            }
            var result = new DownloadedImage(url, data, queuedAt, startedAt, AnimationClock.NowMilliseconds) { StreamedEntry = progressive?.Entry };
            data = null;
            return result;
        }
        finally { data?.Dispose(); _mediaLoadGate.Release(); }
    }

    private sealed class CandidatesSupersededException() : Exception("Renewed media URLs arrived");

    private bool HasSupersedingCandidates(string cacheKey, IReadOnlyList<string> loading)
    {
        lock (_gate)
            return _inflightCandidates.TryGetValue(cacheKey, out var latest) && HasNewCandidate(latest, loading);
    }

    private async IAsyncEnumerable<DownloadedImage> DownloadCandidatesAsync(string[] urls, bool lowByteLimit, bool animated,
        Func<bool>? superseded = null, string? streamingKey = null)
    {
        if (animated || lowByteLimit)
        {
            using var directPrefetchStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            using var directPrefetchWake = new CancellationTokenSource();
            var relayProgress = new TransferProgress();
            string? directPrefetchUrl = null;
            Task<DownloadedImage?>? directPrefetch = null;
            Task<DownloadedImage?>? directPrefetchWin = null;
            if (animated && urls.Any(IsRelayMediaProxy))
            {
                directPrefetchUrl = SelectDirectAnimationPrefetchUrl(urls);
                if (directPrefetchUrl is not null)
                {
                    directPrefetch = PrefetchDirectAnimationAsync(
                        directPrefetchUrl,
                        lowByteLimit,
                        directPrefetchWake.Token,
                        directPrefetchStop.Token, () => relayProgress.Started);
                    directPrefetchWin = WaitForDirectAnimationWinAsync(directPrefetch, directPrefetchStop.Token);
                }
            }
            var fastProxy = IsDiscordImageProxy(urls[0]) && urls.Any(url => !IsDiscordImageProxy(url));
            var attempts = urls.Select((url, i) => (Url: url, Timeout: IsRelayMediaProxy(url)
                ? RelayAnimationHeaderTimeoutMilliseconds : fastProxy && i == 0 ? 500 : urls.Length > 1 ? 1500 : 20000));
            if (urls.Length > 1) attempts = attempts.Concat(urls.Where(url => !IsRelayMediaProxy(url)).Select(url => (Url: url, Timeout: 20000)));
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            var bypassRemainingRelay = false;
            try
            {
                foreach (var attempt in attempts)
                {
                    // Renewed URLs make the remaining old ones (usually expired 404s
                    // and a Relay wait) pointless; LoadAsync restarts with the new set.
                    if (superseded?.Invoke() == true)
                    {
                        AppLog.Info("미디어 새 주소로 재시작 · 남은 이전 후보 생략");
                        yield break;
                    }
                    if (rejected.Contains(attempt.Url)) continue;
                    if (bypassRemainingRelay && IsRelayMediaProxy(attempt.Url)) continue;
                    DownloadedImage? result = null;
                    try
                    {
                        if (IsRelayMediaProxy(attempt.Url)
                            && directPrefetch is not null
                            && directPrefetchWin is not null
                            && directPrefetchUrl is not null)
                        {
                            using var relayAttemptStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            relayProgress.Started = false;
                            var relayDownload = DownloadImageAsync(
                                attempt.Url,
                                lowByteLimit,
                                relayAttemptStop.Token,
                                attempt.Timeout,
                                relayProgress, streamingKey);
                            var winner = await Task.WhenAny((Task)relayDownload, directPrefetchWin).ConfigureAwait(false);
                            if (ReferenceEquals(winner, directPrefetchWin) && !relayProgress.Started)
                            {
                                var prefetched = await directPrefetchWin.ConfigureAwait(false);
                                if (prefetched is not null && CanPreemptRelay(prefetched.Data))
                                {
                                    var prefetchedUrl = directPrefetchUrl;
                                    relayAttemptStop.Cancel();
                                    try { (await relayDownload.ConfigureAwait(false)).Dispose(); }
                                    catch (OperationCanceledException) { }
                                    catch { }
                                    result = prefetched;
                                    rejected.Add(prefetchedUrl);
                                    directPrefetch = null;
                                    directPrefetchWin = null;
                                    directPrefetchUrl = null;
                                    AppLog.Info($"직접 애니메이션 선점: {SafeHost(prefetchedUrl)} · Relay 대기 중단");
                                }
                                else
                                {
                                    result = await relayDownload.ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                if (ReferenceEquals(winner, directPrefetchWin))
                                    AppLog.Info($"중계 전송 진행 중 · 원본 선점 보류: {SafeHost(attempt.Url)}");
                                result = await relayDownload.ConfigureAwait(false);
                            }
                        }
                        else if (directPrefetch is not null
                            && directPrefetchUrl is not null
                            && attempt.Url.Equals(directPrefetchUrl, StringComparison.Ordinal))
                        {
                            // Relay has now failed or been bypassed. Wake the hedge
                            // immediately if its 600 ms grace period has not elapsed.
                            if (!directPrefetchWake.IsCancellationRequested) directPrefetchWake.Cancel();
                            result = await directPrefetch.ConfigureAwait(false);
                            directPrefetch = null;
                            directPrefetchUrl = null;
                            if (result is not null)
                                AppLog.Info($"직접 애니메이션 예비 다운로드 재사용: {SafeHost(attempt.Url)}");
                        }
                        result ??= await DownloadImageAsync(attempt.Url, lowByteLimit, _stop.Token, attempt.Timeout, streamingKey: streamingKey).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (OperationCanceledException) when (IsRelayMediaProxy(attempt.Url))
                    {
                        rejected.Add(attempt.Url);
                        bypassRemainingRelay = true;
                        AppLog.Warn("중계 응답 지연 · 남은 Relay 후보 생략");
                    }
                    catch (RelayMediaFailureException ex) when (ex.Detail == RelayNotAnimatedDetail)
                    {
                        // Relay237 checked the file itself: every rendition is a still
                        // image, so neither another Relay candidate nor a later probe helps.
                        rejected.Add(attempt.Url);
                        // 295 treated any still answer as final. A resized Discord proxy
                        // rendition can be a still frame of an animated original, so only
                        // the original file's answer decides for every rendition.
                        if (RelaySourceOf(attempt.Url) is { } stillSource && IsOriginalRelaySource(stillSource))
                        {
                            bypassRemainingRelay = true;
                            RememberRelayStill(attempt.Url);
                            AppLog.Info("중계 확인: 정지 이미지 · 남은 Relay 후보 생략");
                        }
                        else
                        {
                            AppLog.Info("중계 확인: 이 크기 주소는 정지 이미지 · 다른 Relay 후보 계속");
                        }
                    }
                    catch (RelayMediaFailureException ex)
                    {
                        rejected.Add(attempt.Url);
                        RejectExpiredRelayRenditions(ex.Code, attempt.Url, urls, rejected);
                        if (ex.Code.Equals("worker_busy", StringComparison.Ordinal)
                            || ex.Code.Equals("worker_format_rejected", StringComparison.Ordinal)
                            || ex.Code.Equals("worker_decode_failed", StringComparison.Ordinal))
                        {
                            bypassRemainingRelay = true;
                            AppLog.Warn($"중계 실패 · 남은 Relay 후보 생략: {ex.Code}");
                        }
                        AppLog.Warn($"미디어 후보 제외: HTTP {(int)(ex.StatusCode ?? HttpStatusCode.NotFound)}");
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    { rejected.Add(attempt.Url); AppLog.Warn($"미디어 후보 제외: HTTP {(int)ex.StatusCode.Value}"); }
                    catch (Exception ex) { AppLog.Warn($"미디어 다운로드 후보 실패: {ex.GetType().Name}"); }
                    if (result is not null)
                    {
                        // Stop the hedge before the caller starts decoding the relay pack.
                        if (IsRelayMediaProxy(result.Url)) directPrefetchStop.Cancel();
                        yield return result;
                    }
                }
            }
            finally
            {
                directPrefetchStop.Cancel();
                if (!directPrefetchWake.IsCancellationRequested) directPrefetchWake.Cancel();
                if (directPrefetch is not null)
                {
                    try { (await directPrefetch.ConfigureAwait(false))?.Dispose(); }
                    catch { }
                }
            }
            yield break;
        }
        using var race = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var pending = new List<Task<DownloadedImage>>();
        var next = 0;
        // A small global limit also bounds sockets, memory and competing transfers.
        // Keep a healthy but cold thumbnail conversion alive instead of repeatedly
        // cancelling it at 500/1500ms and restarting the same conversion.
        const int parallel = 3;
        try
        {
            pending.Add(DownloadImageAsync(urls[next++], lowByteLimit, race.Token));
            while (pending.Count < parallel && next < urls.Length)
            {
                var primary = Task.WhenAny(pending);
                if (await Task.WhenAny(primary, Task.Delay(75, race.Token)).ConfigureAwait(false) != primary)
                    pending.Add(DownloadImageAsync(urls[next++], lowByteLimit, race.Token));
                else break;
            }
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                DownloadedImage? result = null;
                try { result = await completed.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception ex) { AppLog.Warn($"미디어 다운로드 후보 실패: {ex.GetType().Name}"); }
                if (result is not null) yield return result;
                // Resumed only after an invalid/unsupported decoded candidate.
                while (pending.Count < parallel && next < urls.Length)
                    pending.Add(DownloadImageAsync(urls[next++], lowByteLimit, race.Token));
            }
        }
        finally
        {
            race.Cancel();
            foreach (var task in pending)
            {
                try { (await task.ConfigureAwait(false)).Dispose(); }
                catch { }
            }
        }
    }

    private async Task<DownloadedImage?> PrefetchDirectAnimationAsync(
        string url,
        bool lowByteLimit,
        CancellationToken wakeToken,
        CancellationToken stopToken, Func<bool> relayStarted)
    {
        try
        {
            try
            {
                await Task.Delay(RelayDirectPrefetchDelayMilliseconds, wakeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wakeToken.IsCancellationRequested && !stopToken.IsCancellationRequested)
            {
                // A Relay failure reached the direct candidate before the grace
                // period elapsed. Begin the fallback transfer immediately.
            }
            stopToken.ThrowIfCancellationRequested();
            if (relayStarted() && !wakeToken.IsCancellationRequested) return null;
            AppLog.Info($"직접 애니메이션 예비 다운로드 시작: {SafeHost(url)}");
            return await DownloadImageAsync(url, lowByteLimit, stopToken, 20000).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"직접 애니메이션 예비 다운로드 실패: {SafeHost(url)} · {ex.GetType().Name}");
            return null;
        }
    }

    private static async Task<DownloadedImage?> WaitForDirectAnimationWinAsync(
        Task<DownloadedImage?> prefetch,
        CancellationToken token)
    {
        await Task.Delay(RelayDirectWinDelayMilliseconds, token).ConfigureAwait(false);
        return await prefetch.ConfigureAwait(false);
    }

    internal string? SelectDirectAnimationPrefetchUrl(IReadOnlyList<string> urls)
        => urls
            .Where(url => !IsRelayMediaProxy(url)
                          && !IsExplicitStaticRendition(url)
                          && !IsRawVideoCandidate(url)
                          && !IsExternalDiscordImageProxy(url))
            .OrderBy(DirectAnimationPrefetchPriority)
            .FirstOrDefault();

    private static int DirectAnimationPrefetchPriority(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return 9;
        if (uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)) return 0;
        if (uri.Host.Equals("media1.tenor.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".tenor.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("tenor.com", StringComparison.OrdinalIgnoreCase)) return 1;
        if (!IsDiscordImageProxy(url)) return 2;
        if (uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase)) return 3;
        return 9;
    }

    private static bool IsExternalDiscordImageProxy(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Host.StartsWith("images-ext-", StringComparison.OrdinalIgnoreCase)
           && uri.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase);
    internal static string? FirstDisplayPreviewUrl(IReadOnlyList<string> candidates)
    {
        // Prefer an explicitly static rendition even if an APNG/GIF source with
        // a static-looking extension appears earlier in the candidate list.
        var explicitPreview = candidates.FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !IsRawVideoCandidate(value)
            && uri.Query.Split('&', '?').Any(p => p.Equals("animated=false", StringComparison.OrdinalIgnoreCase)
                || p.Equals("format=png", StringComparison.OrdinalIgnoreCase)
                || p.Equals("format=jpeg", StringComparison.OrdinalIgnoreCase)));
        explicitPreview ??= candidates.FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && !IsRawVideoCandidate(value)
            && (uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)));
        if (explicitPreview is not null) return explicitPreview;
        // Only Discord's existing image service; never send signed URLs or
        // credentials to an unrelated thumbnail provider. Keep attachment signatures.
        foreach (var value in candidates)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !string.IsNullOrEmpty(uri.UserInfo) || IsRawVideoCandidate(value)) continue;
            if (uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net")) continue;
            if (!uri.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal)
                && !uri.AbsolutePath.StartsWith("/external/", StringComparison.Ordinal)) continue;
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !new[] { "width", "height", "format", "quality", "animated" }
                    .Contains(p.Split('=')[0], StringComparer.OrdinalIgnoreCase));
            var builder = new UriBuilder(uri) { Host = "media.discordapp.net", Port = -1,
                Query = string.Join("&", query.Concat(new[] { "width=320", "height=320", "format=png", "animated=false" })), Fragment = "" };
            return builder.Uri.AbsoluteUri;
        }
        return null;
    }
    private async Task LoadStillPreviewAsync(string cacheKey, IReadOnlyList<string> candidates, CancellationToken token, bool isSticker)
    {
        // Use only an explicitly supplied static rendition; never decode a second GIF.
        var preview = FirstDisplayPreviewUrl(candidates);
        if (preview is null) return;
        try
        {
            await Task.Delay(isSticker ? 20 : 35, token).ConfigureAwait(false);
            lock (_gate) if (_disposed || _cache.ContainsKey(cacheKey)) return;
            using var download = await DownloadImageAsync(preview, true, token, 1500).ConfigureAwait(false);
            if (!IsSmallStillImage(download.Data)) return;
            await _smallStillDecodeGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var decoded = DecodeMedia(download.Data);
                if (decoded.Frames.Count != 1) return;
                var bitmap = CloneScaled(decoded.Frames[0], 320);
                lock (_gate)
                {
                    if (token.IsCancellationRequested || _disposed || _cache.ContainsKey(cacheKey)) { bitmap.Dispose(); return; }
                    Add(cacheKey, new DecodedMedia([bitmap], [0], 0));
                }
                AppLog.Info($"미디어 첫 미리보기 표시 준비: {AnimationClock.NowMilliseconds - download.QueuedAt}ms · GIF 로딩 계속");
                if (!_disposed) MediaReady?.Invoke();
            }
            finally { _smallStillDecodeGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Warn("미디어 미리보기 생략: " + ex.GetType().Name); }
    }
}
