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

internal sealed partial class MediaCache : IDisposable
{
    private const long MaxDownloadBytes = 64L * 1024 * 1024;
    private const long MaxEmojiDownloadBytes = 8L * 1024 * 1024;
    // Bound cached storage without repeatedly downloading visible media.
    // Compressible animations now count their payloads plus two hot bitmaps,
    // instead of holding every decoded frame as a native bitmap.
    private const long MaxCacheBytes = 96L * 1024 * 1024;
    private const int MaxCacheItems = 96;
    private const int MaxNegativeEntries = 256;
    private const int MaxPendingLoads = 64;
    private const int MaxCachedDimension = 960;
    // Animated media is never drawn beyond 320px at the 100% HUD setting, so the
    // client (profile 0) decodes to that limit. The relay exports frame packs
    // with its own profiles: 1 = HQ 800 px, 2 = compact 320 px, 3 = standard 640 px.
    private const int ExportProfileClient = 0, ExportProfileHighQuality = 1, ExportProfileCompact = 2, ExportProfileStandard = 3;
    private static readonly AsyncLocal<int> ExportProfile = new();
    private static int MaxAnimatedDimension => ExportProfile.Value switch
    {
        ExportProfileHighQuality => 800,
        ExportProfileStandard => 640,
        _ => 320
    };
    private static int MaxAnimatedFrames => ExportProfile.Value == ExportProfileHighQuality ? 240 : 180;
    private const int MinimumSourceFrameDelayMilliseconds = 10;
    // Bound stored pixels to 30 MB (client and compact), 60 MB (standard export)
    // or 120 MB (opt-in HQ export), including
    // incompressible media. Lossless packing is used only when even smaller.
    private static long MaxAnimatedFramePixels => (ExportProfile.Value switch
    {
        ExportProfileHighQuality => 120_000_000L,
        ExportProfileStandard => 60_000_000L,
        _ => 30_000_000L
    }) / 4;
    private const long MaxDecodedPixels = 80_000_000;
    private sealed class MediaBudgetExceededException : Exception
    {
        public MediaBudgetExceededException() : base("Source media exceeds the 192 MB decode budget.") { }
    }
    private sealed class RelayMediaFailureException(string code, HttpStatusCode statusCode, string? detail = null)
        : HttpRequestException($"Relay media failure: {code}", null, statusCode)
    {
        internal string Code { get; } = code;
        // Relay237+ X-HUD-Media-Detail, e.g. "not_animated". Null on older relays.
        internal string? Detail { get; } = detail;
    }
    private const string RelayNotAnimatedDetail = "not_animated";
    private const int MaxRelayStaticSources = 2048;
    // Sources Relay has confirmed to be still images. A WebP probe for them would
    // only cost a Relay round trip before the same direct still decode.
    private readonly object _relayStaticGate = new();
    private readonly HashSet<string> _relayStaticSources = new(StringComparer.OrdinalIgnoreCase);
    // Large cold packs in real relay logs need 3-5 s before headers. Keep a
    // finite fallback while giving server conversion/compression time to finish.
    private const int RelayAnimationHeaderTimeoutMilliseconds = 7000;
    // Do not wait for a slow Relay header before even beginning the direct
    // animation transfer. Start only the encoded download in the background;
    // decoding still stays serialized and begins only after Relay has failed.
    // A short delay preserves the fast Relay/cache path without sacrificing the
    // several seconds otherwise lost before a Tenor/Discord fallback starts.
    private const int RelayDirectPrefetchDelayMilliseconds = 600;
    // The direct original may only take over while Relay is still silent. Once
    // Relay starts delivering the pack, the transfer is left alone: a Relay 242
    // WebP pack is far smaller than the original, so preempting it lost the most
    // on exactly the slow connections the preemption was meant to help.
    private const int RelayDirectWinDelayMilliseconds = 900;
    private const long MaxConcurrentAnimationFallbackBytes = 2L * 1024 * 1024;
    private const long MaxConcurrentAnimationFallbackPixels = 640L * 640;
    private readonly HttpClient _http;
    private readonly Uri? _relayServer;
    private readonly string _relayToken;
    private readonly MediaFrameDiskCache? _frameDiskCache;
    // 전송은 I/O 대기라 슬롯을 넉넉히 준다. 슬롯이 2개뿐이면 한 메시지에 붙은
    // 사진 두 장이 아바타/이모지와 슬롯을 다투면서 뒤에 줄을 서고, 그 대기가
    // 그대로 "늦게 뜬다"로 보인다. 전송 슬롯은 디코드 대기 전에 반납된다.
    private readonly SemaphoreSlim _mediaLoadGate = new(6, 6);
    // 디코드는 CPU 를 쓰므로 게임과 함께 돌아가는 오버레이답게 조금만 연다.
    // 큰 이미지/애니메이션은 2개까지, 1MP 이하의 작은 정지 이미지는 거의
    // 즉시 끝나므로 별도 슬롯 3개를 줘서 큰 것 뒤에 막히지 않게 한다.
    private readonly SemaphoreSlim _decodeGate = new(2, 2);
    private readonly SemaphoreSlim _smallStillDecodeGate = new(3, 3);
    // Relay failures can otherwise dump several large GIFs into the local decoder
    // at once. That makes first display slower and creates multi-percent CPU bursts.
    // Serialize only animation-probe/direct fallbacks; normal still images retain
    // their existing parallel decode path.
    private readonly SemaphoreSlim _animationFallbackDecodeGate = new(1, 1);
    private static readonly SixLabors.ImageSharp.Configuration DecoderConfiguration = CreateDecoderConfiguration();

    private static SixLabors.ImageSharp.Configuration CreateDecoderConfiguration()
    {
        var config = SixLabors.ImageSharp.Configuration.Default.Clone();
        config.MaxDegreeOfParallelism = 1;
        config.MemoryAllocator = new TransientImageAllocator();
        return config;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = [];
    private readonly HashSet<string> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private sealed record FailedLoad(DateTimeOffset Until, IReadOnlyList<string> Candidates);
    private readonly Dictionary<string, FailedLoad> _negative = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _inflightCandidates = new(StringComparer.OrdinalIgnoreCase);
    private long _cacheBytes;
    private long _renderPassId;
    private int _animatedAccessCount;
    public int AnimatedAccessCount { get { lock (_gate) return _animatedAccessCount; } }
    private bool _animationsEnabled = true;
    private volatile bool _disposed;

    public MediaCache() : this(null, null, "") { }

    internal MediaCache(HttpMessageHandler? suppliedHandler) : this(suppliedHandler, null, "") { }

    internal MediaCache(Uri relayServer, string relayToken) : this(null, relayServer, relayToken) { }

    internal MediaCache(HttpMessageHandler? suppliedHandler, Uri? relayServer, string relayToken, string? frameCacheDirectory = null)
    {
        _relayServer = relayServer;
        _relayToken = relayToken;
        if (relayServer is not null)
        {
            AnimationFrameCodec.WarmUpDecoder();
            CompressedBitmapFrames.WarmUpDecodePath();
        }
        if (relayServer is not null && relayToken.Length > 0 && (suppliedHandler is null || frameCacheDirectory is not null))
            _frameDiskCache = new MediaFrameDiskCache(frameCacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordChatHUD", "MediaFrames", "v1"),
                relayServer.GetLeftPart(UriPartial.Authority) + "\n" + relayToken);
        var handler = suppliedHandler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 6,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordChatHUD-CSharp/1.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "image/gif,image/apng,image/webp,image/png,image/jpeg;q=0.9,*/*;q=0.2");
    }

    public event Action? MediaReady;

    internal (long CacheBytes, int Items, int Pending, int Animated) GetDiagnostics()
    {
        lock (_gate) return (_cacheBytes, _cache.Count, _inflight.Count, _cache.Values.Count(entry => entry.IsAnimated));
    }

    internal void TrimIdle(bool visible)
    {
        var now = AnimationClock.NowMilliseconds;
        lock (_gate)
        {
            if (_disposed) return;
            var node = _lru.First;
            while (node is not null)
            {
                var next = node.Next;
                var entry = _cache[node.Value];
                // A visible long-delay frame must survive even without recent paints.
                var onScreen = visible && _renderPassId > 0 && entry.LastAccessRenderPassId == _renderPassId;
                var retention = entry.IsAnimated ? 5_000 : 120_000;
                if (!onScreen && now - entry.LastAccessTick >= retention)
                {
                    if (entry.Frames is CompressedBitmapFrames && now - entry.LastAccessTick < 15 * 60_000)
                    {
                        var before = entry.Bytes;
                        entry.Park();
                        _cacheBytes += entry.Bytes - before;
                        node = next;
                        continue;
                    }
                    _cache.Remove(node.Value);
                    _lru.Remove(node);
                    _cacheBytes -= entry.Bytes;
                    entry.Dispose();
                }
                node = next;
            }
            TrimDiskLocked();
        }
    }

    private void TrimDiskLocked(string? protectedUrl = null)
    {
        var diskBytes = _cache.Values.Sum(entry => entry.Frames is CompressedBitmapFrames p ? p.DiskBytes : 0L);
        var node = _lru.First;
        while (diskBytes > 128L * 1024 * 1024 && node is not null)
        {
            var next = node.Next;
            var entry = _cache[node.Value];
            if (node.Value != protectedUrl && entry.Frames is CompressedBitmapFrames p
                && (entry.LastAccessRenderPassId != _renderPassId || diskBytes > 256L * 1024 * 1024))
            {
                diskBytes -= p.DiskBytes;
                _cacheBytes -= entry.Bytes;
                _cache.Remove(node.Value);
                _lru.Remove(node);
                entry.Dispose();
            }
            node = next;
        }
    }

    public void BeginRenderPass()
    {
        lock (_gate)
        {
            _renderPassId = _renderPassId == long.MaxValue ? 1 : _renderPassId + 1;
            _animatedAccessCount = 0;
        }
    }

    public bool AnimationsEnabled
    {
        get
        {
            lock (_gate) return _animationsEnabled;
        }
        set
        {
            lock (_gate)
            {
                if (_animationsEnabled == value) return;
                _animationsEnabled = value;
                if (!value)
                {
                    foreach (var entry in _cache.Values) entry.ResetAnimatedAccess();
                }
            }
        }
    }

    public int GetAnimationRefreshDelayMilliseconds()
    {
        var nowTick = AnimationClock.NowMilliseconds;
        lock (_gate)
        {
            var nextDelay = int.MaxValue;
            foreach (var entry in _cache.Values)
            {
                if (!entry.WasAnimatedInRenderPass(_renderPassId)) continue;
                nextDelay = Math.Min(nextDelay, entry.MillisecondsUntilNextFrame(nowTick));
            }

            return nextDelay == int.MaxValue ? -1 : Math.Max(1, nextDelay);
        }
    }

    // The dedicated animation timer is normally more precise, but a layered
    // window can occasionally lose that timer while its visibility changes.
    // The regular HUD tick uses this as a lightweight safety net so a decoded
    // animation can never remain on its first frame indefinitely.
    public bool IsAnimationFrameDue(int withinMilliseconds)
    {
        var delay = GetAnimationRefreshDelayMilliseconds();
        return delay >= 0 && delay <= Math.Max(1, withinMilliseconds);
    }

    // Reuse rendered pixels without acquiring/decompressing a source bitmap.
    // Selection and acknowledgement stay under the cache gate so eviction cannot
    // make an old renderer tile stand in for a newly loaded source.
    internal bool TryUseAnimationFrame(MediaItem? media, string? emojiUrl, long sourceId, Func<int, bool> useFrame)
    {
        var key = media is null ? emojiUrl : FirstUsableUrl(media.CandidateUrls.Count > 0 ? media.CandidateUrls : new[] { media.BestUrl });
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_gate)
        {
            if (_disposed || !_animationsEnabled || !_cache.TryGetValue(key, out var entry)
                || entry.FrameSourceId != sourceId || !entry.IsAnimated) return false;
            var now = AnimationClock.NowMilliseconds;
            entry.SelectCurrentFrame(now, true, out var index, out var serial);
            if (!useFrame(index)) return false;
            entry.AcknowledgeFrame(serial);
            TouchLocked(key, entry);
            entry.LastAccessRenderPassId = _renderPassId;
            entry.LastAccessTick = now;
            entry.MarkAnimatedAccess(_renderPassId);
            _animatedAccessCount++;
            return true;
        }
    }
    /// <summary>
    /// Preview 306: the pixel size of what is already decoded, without taking a lease
    /// or starting a download. Discord delivers a link embed (Tenor and the like) after
    /// the message itself, so until it lands the layout has no dimensions and falls back
    /// to a fixed box; the decoded frame keeps the aspect ratio right in the meantime.
    /// </summary>
    public Size? TryGetDecodedSize(MediaItem media)
    {
        if (_disposed) return null;
        var cacheKey = FirstUsableUrl(media.CandidateUrls.Count > 0 ? media.CandidateUrls : new[] { media.BestUrl });
        if (cacheKey is null) return null;
        lock (_gate) return _cache.TryGetValue(cacheKey, out var entry) ? entry.FrameSize : null;
    }

    public BitmapLease? TryGetClone(string? url, bool lowByteLimit = false)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url)) return null;
        if (TryGetCachedClone(url, out var cached)) return cached;
        return QueueLoad(BuildDiscordImageCandidates(url), lowByteLimit);
    }

    public BitmapLease? TryGetClone(MediaItem media, bool lowByteLimit = false)
    {
        var candidates = media.CandidateUrls.Count > 0
            ? media.CandidateUrls
            : new[] { media.BestUrl };
        // Keep the cache key and fast resized first candidate. If that proxy stalls,
        // try the original before more transformations on the same slow host.
        if (!media.IsVideo && !media.IsAnimated && candidates.Count > 1
            && candidates.Contains(media.Url, StringComparer.OrdinalIgnoreCase))
            candidates = new[] { candidates[0], media.Url }.Concat(candidates.Skip(1))
                .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_disposed) return null;
        var cacheKey = FirstUsableUrl(candidates);
        if (cacheKey is null) return null;
        ObserveCandidates(cacheKey, candidates);
        if (TryGetCachedClone(cacheKey, out var cached)) return cached;
        return QueueLoad(candidates, lowByteLimit, media.IsAnimated,
            ShouldProbeRelayAnimation(media) && !IsKnownRelayStill(candidates), media.IsSticker);
    }

    private static bool ShouldProbeRelayAnimation(MediaItem media)
    {
        if (media.IsAnimated || media.IsVideo) return false;
        if (media.ContentType.Contains("webp", StringComparison.OrdinalIgnoreCase)
            || media.FileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return true;
        return media.CandidateUrls.Any(candidate =>
            Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(uri.AbsolutePath).Equals(".webp", StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetCachedClone(string cacheKey, out BitmapLease? bitmap)
    {
        bitmap = null;
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var entry))
            {
                TouchLocked(cacheKey, entry);
                entry.LastAccessRenderPassId = _renderPassId;
                entry.LastAccessTick = AnimationClock.NowMilliseconds;
                var nowTick = AnimationClock.NowMilliseconds;
                if (_animationsEnabled)
                {
                    entry.MarkAnimatedAccess(_renderPassId);
                    if (entry.IsAnimated) _animatedAccessCount++;
                }
                try
                {
                    var frame = entry.AcquireCurrentFrame(nowTick, _animationsEnabled, out var frameIndex);
                    if (entry.Frames is CompressedBitmapFrames packed)
                    {
                        var bytes = packed.CurrentResidentBytes;
                        _cacheBytes += bytes - entry.Bytes;
                        entry.Bytes = bytes;
                    }
                    bitmap = new BitmapLease(this, entry, frame, frameIndex);
                }
                catch (Exception ex)
                {
                    // A deferred frame-pack decode can discover corruption only
                    // when that frame is first used. Remove the bad entry so the
                    // normal load path can discard a bad disk hit and retry the
                    // network candidate instead of leaving a permanently blank GIF.
                    _cache.Remove(cacheKey);
                    _lru.Remove(entry.Node);
                    _cacheBytes -= entry.Bytes;
                    entry.Dispose();
                    bitmap = null;
                    AppLog.Warn($"애니메이션 캐시 프레임 복구: {ex.GetType().Name}");
                    return false;
                }
                return true;
            }
            if (_negative.TryGetValue(cacheKey, out var until))
            {
                if (until.Until > DateTimeOffset.UtcNow) return true;
                _negative.Remove(cacheKey);
            }
        }
        return false;
    }

    private BitmapLease? QueueLoad(IReadOnlyList<string> candidateUrls, bool lowByteLimit, bool expectAnimation = false,
        bool relayAnimationProbe = false, bool isSticker = false)
    {
        var candidates = candidateUrls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return null;
        var cacheKey = candidates[0];
        ObserveCandidates(cacheKey, candidates);
        if (TryGetCachedClone(cacheKey, out var cached)) return cached;

        // GetOrAdd's value factory may run multiple times. Reserve the key
        // before starting any I/O and before the task can complete/remove it.
        lock (_gate)
        {
            if (_disposed || _cache.ContainsKey(cacheKey)) return null;
            // Retry overflow items on a later MediaReady render instead of
            // retaining an unbounded queue of waiting tasks and URL arrays.
            if (_inflight.Count >= MaxPendingLoads) return null;
            if (!_inflight.Add(cacheKey)) return null;
            _inflightCandidates[cacheKey] = candidates;
            _ = Task.Run(() => LoadAsync(cacheKey, candidates, lowByteLimit, expectAnimation, relayAnimationProbe, isSticker));
        }
        return null;
    }

    private static bool HasNewCandidate(IReadOnlyList<string> next, IReadOnlyList<string> previous)
        => next.Any(url => !previous.Contains(url, StringComparer.OrdinalIgnoreCase));

    private void ObserveCandidates(string key, IReadOnlyList<string> candidates)
    {
        lock (_gate)
        {
            if (_negative.TryGetValue(key, out var failed) && HasNewCandidate(candidates, failed.Candidates))
                _negative.Remove(key);
            if (_inflightCandidates.TryGetValue(key, out var running) && HasNewCandidate(candidates, running))
            {
                _inflightCandidates[key] = candidates.ToArray();
                // Logged to measure how long a load kept trying stale URLs after
                // Discord supplied renewed ones (expired-link case).
                AppLog.Info($"미디어 새 주소 도착: {SafeHost(key)} · 진행 중인 로드에 반영");
            }
        }
    }

    private static string? FirstUsableUrl(IReadOnlyList<string> urls)
    {
        for (var i = 0; i < urls.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(urls[i])) return urls[i];
        }
        return null;
    }

    private void Release(CacheEntry entry)
    {
        lock (_gate) entry.ReleaseLease();
    }

    private void Add(string url, DecodedMedia decoded)
    {
        // Compression runs on the bounded load worker, never under the render
        // lock. Transfer ownership only after all payloads were created.
        var packed = decoded.Frames as CompressedBitmapFrames;
        var originalBytes = packed?.EstimatedDecodedBytes
            ?? decoded.Frames.Sum(frame => (long)frame.Width * frame.Height * 4);
        if (packed is null && decoded.Frames.Count > 1)
        {
            try { packed = CompressedBitmapFrames.CreateForStorage(decoded.Frames); }
            catch (Exception ex) { AppLog.Warn($"GIF 메모리 압축 생략: {ex.Message}"); }
        }
        IReadOnlyList<Bitmap> storedFrames = packed is null ? decoded.Frames : packed;
        var storedBytes = packed?.CurrentResidentBytes ?? originalBytes;
        if (packed is not null)
        {
            AppLog.Info($"애니메이션 디스크 프레임 저장: 원본 {originalBytes} bytes, 디스크 {packed.DiskBytes} bytes (재생 비트맵 최대 2장)");
            if (!ReferenceEquals(decoded.Frames, packed)) decoded.Dispose();
        }
        lock (_gate)
        {
            if (_disposed)
            {
                if (packed is not null) packed.Dispose(); else decoded.Dispose();
                return;
            }
            if (_cache.Remove(url, out var old))
            {
                _lru.Remove(old.Node);
                _cacheBytes -= old.Bytes;
                old.Dispose();
            }
            var node = _lru.AddLast(url);
            var bytes = storedBytes;
            _cache[url] = new CacheEntry(
                storedFrames,
                decoded.FrameDelays,
                decoded.RepresentativeFrameIndex,
                bytes,
                node);
            decoded.ReleaseOwnership();
            _cacheBytes += bytes;
            TrimDiskLocked(url);
            while (_cache.Count > MaxCacheItems || _cacheBytes > MaxCacheBytes)
            {
                var oldest = _lru.First;
                if (oldest is null) break;
                // Prefer off-screen entries. Keep the original 160 MiB hard
                // ceiling when the current visible scene alone needs more
                // than 96 MiB, avoiding an eviction/re-download render loop.
                while (oldest is not null && (oldest.Value == url
                    || (_renderPassId > 0 && _cache[oldest.Value].LastAccessRenderPassId == _renderPassId)))
                    oldest = oldest.Next;
                if (oldest is null)
                {
                    if (_cache.Count <= MaxCacheItems && _cacheBytes <= 160L * 1024 * 1024) break;
                    oldest = _lru.First!;
                }
                _lru.Remove(oldest);
                if (_cache.Remove(oldest.Value, out var removed))
                {
                    _cacheBytes -= removed.Bytes;
                    removed.Dispose();
                }
            }
        }
    }

    private void RememberNegative(string cacheKey, TimeSpan duration, IReadOnlyList<string> candidates)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _negative
                         .Where(pair => pair.Value.Until <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
                _negative.Remove(expired);

            while (_negative.Count >= MaxNegativeEntries)
            {
                var oldest = _negative.MinBy(pair => pair.Value.Until).Key;
                if (!_negative.Remove(oldest)) break;
            }
            _negative[cacheKey] = new FailedLoad(now.Add(duration), candidates.ToArray());
        }
    }

    private void TouchLocked(string key, CacheEntry entry)
    {
        _lru.Remove(entry.Node);
        _lru.AddLast(entry.Node);
    }

    private static string SafeHost(string url)
    {
        try { return new Uri(url).Host; } catch { return "unknown"; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            foreach (var entry in _cache.Values) entry.Dispose();
            _cache.Clear();
            _lru.Clear();
            _negative.Clear();
            _cacheBytes = 0;
            if (_inflight.Count == 0)
            {
                _stop.Dispose();
                _mediaLoadGate.Dispose();
                _decodeGate.Dispose();
                _smallStillDecodeGate.Dispose();
            }
        }
        _http.Dispose();
    }
}
