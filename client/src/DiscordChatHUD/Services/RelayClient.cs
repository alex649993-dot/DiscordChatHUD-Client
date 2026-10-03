using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;
internal sealed record RelayClientSettings(string ServerUrl)
{
    public static string PathName => Path.Combine(AppContext.BaseDirectory, "relay-client.json");
    internal static bool IsRelayBuild
    {
        get
        {
#if RELAY_CLIENT
            return true;
#else
            return false;
#endif
        }
    }
    public static bool Enabled
    {
        get
        {
#if RELAY_CLIENT
            return true; // A distributed client never falls back to bot-token mode.
#else
            return File.Exists(PathName);
#endif
        }
    }
    public static Uri Server()
    {
        // Fresh installations and recovered update-only folders use the public service.
        // A present custom configuration is still validated; never replace it silently.
        if (IsRelayBuild && !File.Exists(PathName)) return new Uri("https://relay.example.invalid/");
        var config = JsonSerializer.Deserialize<RelayClientSettings>(File.ReadAllText(PathName), RelayProtocol.Json);
        if (!Uri.TryCreate(config?.ServerUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("relay-client.json에 운영자가 제공한 HTTPS 서버 주소를 입력해주세요.");
        return uri;
    }
}

internal sealed class RelayLoginForm : Form
{
    private readonly Uri _server;
    private readonly CancellationTokenSource _stop = new();
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Button _login = new() { Text = "Discord로 로그인", Dock = DockStyle.Bottom, Height = 48 };
    private AccountSettingsSync? _settingsSync;
    internal AccountSettingsSync? TakeSettingsSync() { var sync = _settingsSync; _settingsSync = null; return sync; }
    public string SessionToken { get; private set; } = "";
    public ulong[] Channels { get; private set; } = [];
    internal RelayChannelCatalog? Catalog { get; private set; }
    protected override bool ShowWithoutActivation => Environment.GetCommandLineArgs().Any(x => x is "--auto-config" or "--auto-hud");
    public RelayLoginForm(Uri server)
    {
        _server = server;
        Text = "DiscordChatHUD · 계정 연결";
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 430); MinimumSize = new Size(460, 410);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(16, 17, 21); ForeColor = Color.FromArgb(245, 245, 247);
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false;
        Font = MakeFont(10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 7, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++) layout.RowStyles.Add(row == 4
            ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        Label TextLine(string text, float size, Color color, FontStyle style = FontStyle.Regular)
            => new() { Text = text, AutoSize = true, Dock = DockStyle.Fill, ForeColor = color,
                Font = MakeFont(size, style), Margin = new Padding(0, 0, 0, 10) };
        var muted = Color.FromArgb(160, 161, 170); var accent = Color.FromArgb(250, 45, 85);
        layout.Controls.Add(TextLine("HUD SETTINGS  /  계정 연결", 10, accent, FontStyle.Bold), 0, 0);
        layout.Controls.Add(TextLine("DiscordChatHUD", 25, ForeColor, FontStyle.Bold), 0, 1);
        layout.Controls.Add(TextLine("Discord 계정을 연결한 뒤 설정을 시작하세요.", 11, muted), 0, 2);
        layout.Controls.Add(TextLine(server.Host, 9, muted), 0, 3);
        var card = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(0, 120),
            BackColor = Color.FromArgb(28, 29, 35), Padding = new Padding(16), Margin = new Padding(0, 6, 0, 20) };
        _status.Font = MakeFont(11); _status.ForeColor = ForeColor; _status.BackColor = card.BackColor;
        _status.Text = "Discord 서버의 ‘고닉’ 역할이 필요합니다.\n아래 버튼을 눌러 로그인해주세요.";
        card.Controls.Add(_status); layout.Controls.Add(card, 0, 4);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        _login.Dock = DockStyle.Fill; _login.Height = 48; _login.Font = MakeFont(11, FontStyle.Bold);
        _login.FlatStyle = FlatStyle.Flat; _login.FlatAppearance.BorderSize = 0;
        _login.BackColor = accent; _login.ForeColor = Color.White; _login.UseVisualStyleBackColor = false;
        _login.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 70, 107); _login.Margin = new Padding(0, 0, 10, 0);
        var cancel = new Button { Text = "닫기", Dock = DockStyle.Fill, Height = 48, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(43, 44, 52), ForeColor = ForeColor, DialogResult = DialogResult.Cancel, Margin = Padding.Empty };
        cancel.FlatAppearance.BorderSize = 0;
        actions.Controls.Add(_login, 0, 0); actions.Controls.Add(cancel, 1, 0); layout.Controls.Add(actions, 0, 5);
        var note = TextLine("로그인은 이 PC에 저장되며, 내 설정은 계정에 자동 저장됩니다.", 9, muted);
        note.Margin = new Padding(0, 16, 0, 0); layout.Controls.Add(note, 0, 6);
        Controls.Add(layout); AcceptButton = _login; CancelButton = cancel;
        _login.Click += async (_, _) => await Connect();
        if (ShowWithoutActivation) WindowState = FormWindowState.Minimized;
        Shown += async (_, _) =>
        {
            _login.Enabled = false;
            try
            {
                if (await StartupUpdate.TryApply(message => { if (!IsDisposed) _status.Text = message; }, _stop.Token)) { Close(); return; }
                if (_stop.IsCancellationRequested || IsDisposed) return;
                if (RelaySessionStore.Current.Load(_server) is not null) await Connect();
                else _status.Text = "Discord 서버의 ‘고닉’ 역할이 필요합니다.\n아래 버튼을 눌러 로그인해주세요.";
            }
            finally { if (!IsDisposed) _login.Enabled = true; }
        };
        var args = Environment.GetCommandLineArgs();
        bool autoLaunch = args.Any(x => x is "--auto-config" or "--auto-hud");
        var lifecycleStop = new EventWaitHandle(false, EventResetMode.AutoReset,
            args.Contains("--hud") ? GameAutoLaunch.HudStopSignal : GameAutoLaunch.ConfigStopSignal);
        var lifecycleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        lifecycleTimer.Tick += (_, _) =>
        {
            if (lifecycleStop.WaitOne(0) || (autoLaunch && (!GameAutoLaunch.Enabled || !GameAutoLaunch.IsGameRunning())))
            { DialogResult = DialogResult.Cancel; Close(); }
        };
        Shown += (_, _) => lifecycleTimer.Start();
        FormClosed += (_, _) => { lifecycleTimer.Dispose(); lifecycleStop.Dispose(); };
        FormClosed += (_, _) => _stop.Cancel();
    }
    private readonly List<Font> _ownedFonts = [];
    private Font MakeFont(float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font("Segoe UI", size, style); _ownedFonts.Add(font); return font;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _settingsSync?.Dispose(); _settingsSync = null; }
        base.Dispose(disposing);
        if (disposing) { foreach (var font in _ownedFonts) font.Dispose(); _ownedFonts.Clear(); }
    }
    private static ulong[] ReadChannels(JsonElement root)
    {
        if (!root.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != RelayProtocol.Version
            || !root.TryGetProperty("rememberLogin", out var remember) || remember.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("운영자가 Preview192 이상 중계 서버로 업데이트해야 합니다.");
        var channels = root.GetProperty("channels").EnumerateArray().Select(x => ulong.Parse(x.GetString()!)).ToArray();
        if (channels.Length is < 1 or > 10 || channels.Any(x => x == 0) || channels.Distinct().Count() != channels.Length)
            throw new InvalidDataException();
        return channels;
    }
    private async Task FinishConnect()
    {
        _status.Text = "로그인 완료 · 내 계정 설정을 불러오는 중입니다.";
        var sync = await AccountSettingsSync.Connect(_server, SessionToken, _stop.Token, recoverLocal: true);
        if (IsDisposed || _stop.IsCancellationRequested) { sync.Dispose(); return; }
        _settingsSync = sync;
        if (!string.IsNullOrEmpty(sync.Notice))
            MessageBox.Show(this, sync.Notice, "계정 설정 복원", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK; Close();
    }
    private async Task Connect()
    {
        _login.Enabled = false;
        var saved = RelaySessionStore.Current.Load(_server);
        if (saved is null) { await Login(); return; }
        try
        {
            _status.Text = "저장된 로그인으로 연결 중입니다.";
            using var http = RelayTransport.Create(_server, TimeSpan.FromSeconds(8), 32 * 1024);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saved.Token);
            using var response = await RelaySessionRetry.GetAsync(http, message => { if (!IsDisposed) _status.Text = message; }, _stop.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                RelaySessionStore.Current.Forget(_server, saved.Token);
                _status.Text = "저장된 로그인 권한이 해제되었습니다. Discord로 다시 로그인해주세요.";
                _login.Text = "Discord로 로그인";
                return;
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("운영자가 Preview192 이상 중계 서버로 업데이트해야 합니다.");
            response.EnsureSuccessStatusCode();
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_stop.Token));
            Channels = ReadChannels(data.RootElement); Catalog = RelayChannelCatalog.Read(data.RootElement); SessionToken = saved.Token;
            await FinishConnect();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (InvalidOperationException ex) { if (!IsDisposed) _status.Text = ex.Message + "\n" + ConnectionDiagnostics.Describe(ex, "SESSION"); _ = ConnectionDiagnostics.Report(_server, RelaySessionStore.Current.Load(_server)?.Token, ex); }
        catch (HttpRequestException ex) { if (!IsDisposed) _status.Text = ConnectionDiagnostics.Describe(ex, "SESSION"); _ = ConnectionDiagnostics.Report(_server, RelaySessionStore.Current.Load(_server)?.Token, ex); }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ConnectionDiagnostics.Describe(ex, "SESSION"); _ = ConnectionDiagnostics.Report(_server, RelaySessionStore.Current.Load(_server)?.Token, ex); }
        finally
        {
            if (!IsDisposed)
            {
                _login.Enabled = true;
                _login.Text = RelaySessionStore.Current.Load(_server) is null ? "Discord로 로그인" : "자동 연결 다시 시도";
            }
        }
    }
    private async Task Login()
    {
        _login.Enabled = false;
        try
        {
            using var http = RelayTransport.Create(_server, TimeSpan.FromSeconds(30), 32 * 1024);
            var verifier = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var challenge = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
            using var started = await http.PostAsJsonAsync("auth/start", new { challenge }, _stop.Token);
            started.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await started.Content.ReadAsStringAsync(_stop.Token));
            var id = payload.RootElement.GetProperty("id").GetString()!;
            Process.Start(new ProcessStartInfo(new Uri(_server, "login/" + Uri.EscapeDataString(id)).AbsoluteUri) { UseShellExecute = true });
            _status.Text = "브라우저에서 Discord 로그인을 완료해주세요.";
            var until = DateTimeOffset.UtcNow.AddMinutes(5);
            while (!_stop.IsCancellationRequested && DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(750, _stop.Token);
                using var polled = await http.PostAsJsonAsync("auth/poll", new { id, verifier }, _stop.Token);
                if (polled.StatusCode == HttpStatusCode.Accepted) continue;
                if (polled.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("‘고닉’ 역할이 없거나 접근이 거부되었습니다.");
                polled.EnsureSuccessStatusCode();
                using var data = JsonDocument.Parse(await polled.Content.ReadAsStringAsync(_stop.Token));
                Channels = ReadChannels(data.RootElement); Catalog = RelayChannelCatalog.Read(data.RootElement);
                SessionToken = data.RootElement.GetProperty("token").GetString()!;
                if (SessionToken.Length != 64 || !SessionToken.All(Uri.IsHexDigit)) throw new InvalidDataException();
                if (!RelaySessionStore.Current.Save(_server, SessionToken, Channels))
                    MessageBox.Show(this, "로그인은 성공했지만 이 PC에 저장하지 못했습니다. 다음 실행에는 다시 로그인해야 합니다. 로컬 저장 폴더 권한을 확인해주세요.", "로그인 저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                await FinishConnect(); return;
            }
            _status.Text = "로그인 시간이 만료되었습니다. 다시 시도해주세요.";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (InvalidOperationException ex) { if (!IsDisposed) _status.Text = ex.Message + "\n" + ConnectionDiagnostics.Describe(ex, "LOGIN"); _ = ConnectionDiagnostics.Report(_server, RelaySessionStore.Current.Load(_server)?.Token, ex); }
        catch (Exception ex) { if (!IsDisposed) _status.Text = ConnectionDiagnostics.Describe(ex, "LOGIN"); _ = ConnectionDiagnostics.Report(_server, RelaySessionStore.Current.Load(_server)?.Token, ex); }
        finally { if (!IsDisposed) _login.Enabled = true; }
    }
}

internal sealed class RelayClient(Uri server, string sessionToken, ulong channel, HttpMessageHandler? httpHandler = null, Action<bool>? authorizationChanged = null) : IChatSource
{
    private readonly HttpClient _http = httpHandler is null ? RelayTransport.Create(server, TimeSpan.FromSeconds(35))
        : new HttpClient(httpHandler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(35) };
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private ChatReceiptReporter? _receipts;
    private RelaySnapshot _snapshot = new(-1, "중계 연결", "중계 연결 준비 중", false, null, DateTimeOffset.MinValue, [], []);
    private string _status = "중계 연결 준비 중";
    public event Action? MessagesChanged;
    public event Action? StatusChanged;
    public string ChannelLabel => Volatile.Read(ref _snapshot).ChannelLabel;
    public string Status => _status;
    public GtaSessionPresence? SessionPresence => Volatile.Read(ref _snapshot).SessionPresence;
    public GtaSessionPresence? SecondarySessionPresence => Volatile.Read(ref _snapshot).SecondarySessionPresence;
    public bool HasSaleChannel => Volatile.Read(ref _snapshot).HasSaleChannel;
    public (ulong? MessageId, DateTimeOffset TimestampUtc) SaleActivity { get { var s = Volatile.Read(ref _snapshot); return (s.SaleMessageId, s.SaleTimestamp); } }
    public IReadOnlyList<ChatMessage> Snapshot() => Volatile.Read(ref _snapshot).Messages;
    public IReadOnlyList<ChatMessage> SaleSnapshot() => Volatile.Read(ref _snapshot).SaleMessages;
    public void Start() => _run ??= Task.Run(Run);
    private async Task Run()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);
        _receipts=new ChatReceiptReporter(server,sessionToken,channel);
        long revision = -1;
        var failures = 0;
        long freshnessRetryTick = 0;
        while (!_stop.IsCancellationRequested)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(revision < 0 && failures == 0 ? 8 : 35));
                var trace = Guid.NewGuid().ToString("N");
                var requestStarted = Stopwatch.GetTimestamp();
                using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/snapshot/{channel}?after={revision}");
                request.Headers.TryAddWithoutValidation("X-HUD-Chat-Trace", trace);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
                var headersMs = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    Volatile.Write(ref _snapshot, new(-1, "접근 종료", "", false, null, DateTimeOffset.MinValue, [], []));
                    authorizationChanged?.Invoke(false);
                    // Preserve the session during temporary server replacement/auth failures.
                    _status = "중계 서버 인증 재확인 중 · 계속되면 로그인과 채널 권한을 확인해주세요.";
                    MessagesChanged?.Invoke(); StatusChanged?.Invoke();
                    revision = -1;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * Math.Pow(2, Math.Min(failures++, 4)))), _stop.Token);
                    continue;
                }
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (response.Headers.RetryAfter?.Date is { } retryAt) retryAfter = retryAt - DateTimeOffset.UtcNow;
                response.EnsureSuccessStatusCode();
                authorizationChanged?.Invoke(true);
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    failures = 0;
                    var recoveredStatus = Volatile.Read(ref _snapshot).Status;
                    if(_status!=recoveredStatus){_status=recoveredStatus;StatusChanged?.Invoke();}
                    continue;
                }
                using var body = new MemoryStream();
                await using var stream = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
                // Header/long-poll wait is separate from body idle and freshness budgets.
                var idle = TimeSpan.FromSeconds(Math.Min(16, 4 * Math.Pow(2, Math.Min(failures, 2))));
                // One freshness retry per minute. A persistently slow link must still be
                // allowed to finish; never repeatedly discard progress on the same path.
                var freshness = freshnessRetryTick == 0 || Stopwatch.GetElapsedTime(freshnessRetryTick).TotalMinutes >= 1
                    ? TimeSpan.FromSeconds(6) : (TimeSpan?)null;
                await SnapshotBodyReader.Read(stream, body, idle, requestTimeout.Token,
                    fields => ChatTiming.Write("snapshot-body", $"trace={trace} channel={channel} {fields}"), freshness: freshness);
                var decodeStarted = Stopwatch.GetTimestamp();
                var next = JsonSerializer.Deserialize<RelaySnapshot>(body.GetBuffer().AsSpan(0, (int)body.Length), RelayProtocol.Json)!;
                ChatTiming.Write("snapshot-receive", FormattableString.Invariant($"trace={trace} channel={channel} revision={next.Revision} headersMs={headersMs:F1} decodeMs={Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds:F1} requestMs={Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds:F1} "));
                _receipts.Enabled=response.Headers.Contains("X-HUD-Chat-Receipts");
                ChatTiming.Receive(next.Messages, "client-receive", next.Revision, revision < 0);
                var previous = Volatile.Read(ref _snapshot);
                var messagesChanged = !MessageStateEquivalent(previous, next);
                var statusChanged = !StatusStateEquivalent(previous, next);
                Volatile.Write(ref _snapshot, next); revision = next.Revision; _status = next.Status; failures = 0;
                if (messagesChanged) MessagesChanged?.Invoke();
                if (statusChanged) StatusChanged?.Invoke();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Do not enter another 20-second long poll after connectivity returns.
                // A negative cursor requests the current snapshot immediately on existing servers.
                if (ex is SnapshotBodySlowException) freshnessRetryTick = Stopwatch.GetTimestamp();
                if (ex is not (SnapshotBodyStalledException or SnapshotBodySlowException)) revision = -1;
                var pause = retryAfter is { } advised ? Math.Clamp(advised.TotalMilliseconds, 500, 120_000)
                    : Math.Min(3000, 500 * Math.Pow(2, Math.Min(failures, 3)));
                failures++;
                var disconnected=Volatile.Read(ref _snapshot);
                var clearedPrimary=disconnected.SessionPresence is {CurrentPlayers: not null} primarySession
                    ?primarySession with {CurrentPlayers=null}:disconnected.SessionPresence;
                var clearedSecondary=disconnected.SecondarySessionPresence is {CurrentPlayers: not null} secondarySession
                    ?secondarySession with {CurrentPlayers=null}:disconnected.SecondarySessionPresence;
                if(!ReferenceEquals(clearedPrimary,disconnected.SessionPresence)
                   ||!ReferenceEquals(clearedSecondary,disconnected.SecondarySessionPresence))
                    Volatile.Write(ref _snapshot,disconnected with
                    {SessionPresence=clearedPrimary,SecondarySessionPresence=clearedSecondary});
                ChatTiming.Write("snapshot-retry", $"channel={channel} waitMs={pause} error={ex.GetType().Name}");
                var reason = ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}"
                    : ex is OperationCanceledException ? "응답 지연" : "연결 오류";
                _status = $"중계 서버 재연결 중 · {reason} · {pause / 1000:0.#}초 후 재시도"; StatusChanged?.Invoke();
                try { await Task.Delay(TimeSpan.FromMilliseconds(pause), _stop.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private static bool StatusStateEquivalent(RelaySnapshot left, RelaySnapshot right)
        => string.Equals(left.ChannelLabel, right.ChannelLabel, StringComparison.Ordinal)
           && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
           && PresenceEquivalent(left.SessionPresence, right.SessionPresence)
           && PresenceEquivalent(left.SecondarySessionPresence, right.SecondarySessionPresence);

    private static bool PresenceEquivalent(GtaSessionPresence? left, GtaSessionPresence? right)
        => ReferenceEquals(left, right)
           || left is not null && right is not null
           && left.HostId == right.HostId
           && left.CurrentPlayers == right.CurrentPlayers
           && left.PlayingGta == right.PlayingGta
           && string.Equals(left.HostName, right.HostName, StringComparison.Ordinal);

    private static bool MessageStateEquivalent(RelaySnapshot left, RelaySnapshot right)
        => left.HasSaleChannel == right.HasSaleChannel
           && left.SaleMessageId == right.SaleMessageId
           && left.SaleTimestamp == right.SaleTimestamp
           && MessagesEquivalent(left.Messages, right.Messages)
           && MessagesEquivalent(left.SaleMessages, right.SaleMessages);

    private static bool MessagesEquivalent(IReadOnlyList<ChatMessage> left, IReadOnlyList<ChatMessage> right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!MessageEquivalent(left[i], right[i])) return false;
        return true;
    }

    private static bool MessageEquivalent(ChatMessage left, ChatMessage right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left.Id == right.Id && left.ChannelId == right.ChannelId && left.GuildId == right.GuildId
               && left.AuthorId == right.AuthorId && left.AuthorColor.ToArgb() == right.AuthorColor.ToArgb()
               && left.RoleBadgeResolved == right.RoleBadgeResolved && left.Timestamp == right.Timestamp
               && left.IsBot == right.IsBot && left.IsMuteNotice == right.IsMuteNotice
               && string.Equals(left.AuthorName, right.AuthorName, StringComparison.Ordinal)
               && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
               && BadgesEquivalent(left.AuthorBadges, right.AuthorBadges)
               && ReplyEquivalent(left.Reply, right.Reply)
               && ForwardEquivalent(left.Forward, right.Forward)
               && Equals(left.GameInvite, right.GameInvite)
               && MediaEquivalent(left.Media, right.Media)
               && ReactionsEquivalent(left.Reactions, right.Reactions);
    }

    private static bool BadgesEquivalent(IReadOnlyList<AuthorBadge> left, IReadOnlyList<AuthorBadge> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static bool ReplyEquivalent(ReplyPreview? left, ReplyPreview? right)
        => ReferenceEquals(left, right)
           || left is not null && right is not null
           && left.AuthorId == right.AuthorId && left.AuthorColor.ToArgb() == right.AuthorColor.ToArgb()
           && left.SourceChannelId == right.SourceChannelId && left.SourceMessageId == right.SourceMessageId
           && string.Equals(left.AuthorName, right.AuthorName, StringComparison.Ordinal)
           && string.Equals(left.Content, right.Content, StringComparison.Ordinal);

    private static bool ForwardEquivalent(ForwardPreview? left, ForwardPreview? right)
        => ReferenceEquals(left, right)
           || left is not null && right is not null
           && left.AuthorId == right.AuthorId && left.AuthorColor.ToArgb() == right.AuthorColor.ToArgb()
           && left.SourceChannelId == right.SourceChannelId && left.SourceMessageId == right.SourceMessageId
           && string.Equals(left.AuthorName, right.AuthorName, StringComparison.Ordinal)
           && string.Equals(left.Content, right.Content, StringComparison.Ordinal)
           && MediaEquivalent(left.AllMedia, right.AllMedia)
           && ReactionsEquivalent(left.Reactions, right.Reactions);

    private static bool MediaEquivalent(IReadOnlyList<MediaItem> left, IReadOnlyList<MediaItem> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i]; var b = right[i];
            if (a.Width != b.Width || a.Height != b.Height || a.IsVideo != b.IsVideo
                || a.IsAnimated != b.IsAnimated || a.IsSticker != b.IsSticker
                || !string.Equals(a.Url, b.Url, StringComparison.Ordinal)
                || !string.Equals(a.ProxyUrl, b.ProxyUrl, StringComparison.Ordinal)
                || !string.Equals(a.FileName, b.FileName, StringComparison.Ordinal)
                || !string.Equals(a.ContentType, b.ContentType, StringComparison.Ordinal)
                || !StringsEquivalent(a.CandidateUrls, b.CandidateUrls)) return false;
        }
        return true;
    }

    private static bool ReactionsEquivalent(IReadOnlyList<ReactionItem> left, IReadOnlyList<ReactionItem> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i]; var b = right[i];
            if (a.EmojiId != b.EmojiId || a.Count != b.Count || a.Animated != b.Animated
                || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool StringsEquivalent(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        return true;
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_run is not null) await _run;
        if(_receipts is not null) await _receipts.DisposeAsync();
        // Closing the HUD preserves the remembered login. Revocation is explicit.
        _http.Dispose(); _stop.Dispose();
    }
}
