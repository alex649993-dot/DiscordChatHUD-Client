using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Buffers;
using System.Text;
using System.Text.Json;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

internal sealed partial class DiscordGatewayClient : IChatSource
{
    private const int GatewayVersion = 10;
    private const int Intents = 1 | 2 | 512 | 1024 | 32768;
    private const int MaxMemberCacheEntries = 512;
    private const int MaxChannelCacheEntries = 256;
    private const int MaxLookupFailures = 1024;
    private const int MaxPendingLookupsPerKind = 128;
    private const int MaxDeferredMessages = 320;
    private const int MaxGatewayPayloadBytes = 16 * 1024 * 1024;
    // Pool the common receive sizes only. An exceptional large guild payload
    // must not leave a multi-megabyte buffer rooted in a shared pool.
    private static readonly ArrayPool<byte> GatewayBuffers = ArrayPool<byte>.Create(64 * 1024, 4);
    private readonly string _token;
    private readonly ulong _targetChannelId;
    private readonly ulong? _saleChannelId;
    private readonly HttpClient _http;
    private readonly MessageStore _messages = new();
    private readonly MessageStore _saleMessages = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _nicknameGate = new(4, 4);
    private readonly SemaphoreSlim _referenceGate = new(4, 4);
    private readonly SemaphoreSlim _channelNameGate = new(2, 2);
    private readonly ConcurrentDictionary<ulong, string> _guildNicknames = new();
    private readonly ConcurrentDictionary<ulong, Color> _guildColors = new();
    private readonly ConcurrentDictionary<ulong, byte> _guildBadgeProfiles = new();
    private readonly ConcurrentDictionary<ulong, string> _channelNames = new();
    private readonly ConcurrentDictionary<ulong, Lazy<Task>> _nicknameFetches = new();
    private readonly ConcurrentDictionary<ulong, Lazy<Task>> _referenceFetches = new();
    private readonly ConcurrentDictionary<ulong, Lazy<Task>> _channelNameFetches = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _fetchGate = new();
    private readonly object _cacheGate = new();
    private readonly Queue<ulong> _memberCacheOrder = new();
    private readonly Queue<ulong> _channelCacheOrder = new();
    private readonly Dictionary<(LookupKind Kind, ulong Scope, ulong Id), long> _lookupRetryAfter = new();
    private readonly Dictionary<ulong, PendingEnrichment> _deferredEnrichment = new();
    private Task? _enrichmentRefillTask;
    private bool _enrichmentRefillScheduled;
    private bool _fetchesStopping;
    private readonly object _messageRefreshGate = new();
    private readonly Dictionary<(ulong Channel, ulong Message), (MessageStore Store, bool ReactionsOnly)> _messageRefreshQueue = new();
    private Task? _messageRefreshTask;
    private readonly object _saleActivityGate = new();
    private IReadOnlyDictionary<ulong, GuildRole> _roles = new Dictionary<ulong, GuildRole>();
    private ClientWebSocket? _socket;
    private Task? _runTask;
    private Task? _stopTask;
    private Task? _disposeTask;
    private long? _sequence;
    private long _restRetryAfterTick;
    private DateTimeOffset _saleActivityUtc = DateTimeOffset.MinValue;
    private ulong? _saleActivityMessageId;
    private volatile bool _heartbeatAcknowledged = true;
    private ulong? _guildId;
    private string _channelName = string.Empty;
    private string _status = "Discord 연결 준비 중";

    public DiscordGatewayClient(string token, ulong targetChannelId, ulong? saleChannelId = null, HttpMessageHandler? httpHandler = null)
    {
        _token = token.Trim();
        _targetChannelId = targetChannelId;
        _saleChannelId = saleChannelId is > 0 ? saleChannelId : null;
        _lifetimeToken = _stop.Token;
        _http = httpHandler is null ? new HttpClient() : new HttpClient(httpHandler);
        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordChatHUD-CSharp/1.0 (+https://discord.com)");
    }

    public event Action? MessagesChanged;
    public event Action? StatusChanged;

    public string ChannelLabel
    {
        get
        {
            var plainName = TwemojiAsset.RemoveEmoji(_channelName).Trim();
            return string.IsNullOrWhiteSpace(plainName) ? $"채널 {_targetChannelId}" : $"#{plainName}";
        }
    }
    public string Status => _status;
    public IReadOnlyList<ChatMessage> Snapshot() => _messages.Snapshot();
    public (ulong? MessageId, DateTimeOffset TimestampUtc) SaleActivity
    {
        get
        {
            lock (_saleActivityGate)
                return (_saleActivityMessageId, _saleActivityUtc);
        }
    }
    public bool HasSaleChannel
        => _saleChannelId is not null
           || _channelName.Contains("판매", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<ChatMessage> SaleSnapshot()
    {
        if (_saleChannelId == _targetChannelId || (_saleChannelId is null && HasSaleChannel))
            return _messages.Snapshot();
        return _saleChannelId is null ? [] : _saleMessages.Snapshot();
    }

    public void Start()
    {
        lock (_fetchGate)
        {
            if (_runTask is not null || _fetchesStopping || _lifetimeToken.IsCancellationRequested) return;
            if(_leader is not null)return;
            _runTask = Task.Run(() => RunSharedAsync(_lifetimeToken));
        }
    }

    public Task StopAsync()
    {
        lock (_fetchGate)
            return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _fetchesStopping = true;
        _deferredEnrichment.Clear();
        _stop.Cancel();
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "HUD closing", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch { }
        }
        try { socket?.Abort(); } catch { }
        if (_runTask is not null)
        {
            try { await _runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetStatus("Discord 연결 중");
                await LoadChannelMetadataAsync(cancellationToken).ConfigureAwait(false);
                await ConnectAndReceiveAsync(cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Error("Discord 토큰 인증 실패", ex);
                SetStatus("Discord 토큰 인증 실패");
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error("Discord 연결이 끊어짐", ex);
                SetStatus($"Discord 재연결 대기 ({Math.Ceiling(delay.TotalSeconds):0}초)");
                try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); } catch { break; }
                delay = TimeSpan.FromSeconds(Math.Min(15, Math.Max(2, delay.TotalSeconds * 1.7)));
            }
        }
    }

    private void SetStatus(string status)
    {
        if (string.Equals(_status, status, StringComparison.Ordinal)) return;
        var connected=status.StartsWith("Discord 연결됨",StringComparison.Ordinal);
        if(!connected && SessionPresence is {} currentSession)
            Volatile.Write(ref _sessionPresence,currentSession with {CurrentPlayers=null,UpdatedAtUtc=DateTimeOffset.UtcNow});
        if(!connected && SecondarySessionPresence is {} currentSecondary)
            Volatile.Write(ref _secondarySessionPresence,currentSecondary with {CurrentPlayers=null,UpdatedAtUtc=DateTimeOffset.UtcNow});
        if(!connected){Interlocked.Increment(ref _presenceGeneration);Interlocked.Increment(ref _secondaryPresenceGeneration);}
        foreach(var follower in _followers)follower.SetStatus(status);
        _status = status;
        StatusChanged?.Invoke();
    }

    private static ulong GetChannelId(JsonElement data)
        => data.TryGetProperty("channel_id", out var channel) && TryReadUlong(channel, out var id) ? id : 0;

    private static bool TryGetId(JsonElement data, out ulong id)
    {
        id = 0;
        return data.TryGetProperty("id", out var value) && TryReadUlong(value, out id);
    }

    private static bool TryGetMessageId(JsonElement data, out ulong id)
    {
        id = 0;
        return data.TryGetProperty("message_id", out var value) && TryReadUlong(value, out id);
    }

    private static bool TryReadUlong(JsonElement value, out ulong result)
    {
        if (value.ValueKind == JsonValueKind.String) return ulong.TryParse(value.GetString(), out result);
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetUInt64(out result);
        result = 0;
        return false;
    }

    public ValueTask DisposeAsync()
    {
        lock (_fetchGate)
        {
            _fetchesStopping = true;
            _deferredEnrichment.Clear();
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        // Fetch callbacks release their semaphores in finally. Drain them
        // before disposing the gates and HTTP client they still own.
        var pending = _nicknameFetches.Values.Concat(_referenceFetches.Values).Concat(_channelNameFetches.Values)
            .Where(work => work.IsValueCreated).Select(work => work.Value)
            .Concat(_enrichmentRefillTask is { } refill ? new[] { refill } : [])
            .Concat(_memberRetryTask is { } memberRetry ? new[] { memberRetry } : [])
            .Concat(_messageRefreshTask is { } refresh ? new[] { refresh } : []).ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _http.Dispose();
        _sendGate.Dispose();
        _nicknameGate.Dispose();
        _referenceGate.Dispose();
        _channelNameGate.Dispose();
        _stop.Dispose();
    }
}
