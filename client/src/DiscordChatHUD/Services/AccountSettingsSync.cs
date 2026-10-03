using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal sealed class AccountSettingsSync : IDisposable
{
    internal sealed class ServerRollbackException : InvalidOperationException
    {
        public ServerRollbackException() : base("서버에 이전 설정이 남아 있습니다. 이 PC 설정으로 내 계정만 복구할 수 있습니다.") { }
    }
    internal bool RecoverLocalSettings { get; set; }
    internal sealed record Lease(int Pid, long Started, string Instance);
    internal sealed class Metadata
    {
        public string Origin { get; set; } = "";
        public string UserId { get; set; } = "";
        public long Version { get; set; }
        public string Hash { get; set; } = "";
        // Preview 296: hash of the local settings last uploaded or restored. The
        // server re-encodes with its own settings model, so Hash (the server copy)
        // never matched a newer client's local encoding and every client re-sent
        // identical settings every 10 s (Relay log: ~51 saves/min for 9 users).
        public string LocalHash { get; set; } = "";
        public bool Conflict { get; set; }
        public bool ServerResetApplied { get; set; }
        public List<Lease> Leases { get; set; } = [];
    }
    private static string _status = "계정 설정 연결 전";
    internal static string Status => Volatile.Read(ref _status);
    internal static event Action? StatusChanged;
    private static void SetStatus(string status)
    {
        if (Interlocked.Exchange(ref _status, status) == status) return;
        try { StatusChanged?.Invoke(); } catch { }
    }
    private readonly string _directory, _origin, _metadataPath;
    private readonly HttpClient _http;
    private readonly Func<HudConfig> _read;
    private readonly Action<HudConfig> _restore;
    private readonly Action<string> _backup;
    private readonly Func<IReadOnlyList<int>> _otherProcesses;
    private readonly Lease _lease;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private string _user = "";
    private bool _joined, _disposed;
    internal string Notice { get; private set; } = "";
    internal AccountSettingsSync(Uri server, string token, string directory, Func<HudConfig> read,
        Action<HudConfig> restore, Action<string> backup, HttpMessageHandler? handler = null,
        Func<IReadOnlyList<int>>? otherProcesses = null)
    {
        _directory = directory; _origin = server.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        _metadataPath = Path.Combine(directory, "account-profile-sync.json");
        _read = () => ConnectionDiagnostics.At("LOCAL-READ", read); _restore = c => ConnectionDiagnostics.At("LOCAL-RESTORE", () => { restore(c); return true; }); _backup = reason => ConnectionDiagnostics.At("LOCAL-BACKUP", () => { backup(reason); return true; }); _otherProcesses = otherProcesses ?? (() => []);
        _http = handler is null ? RelayTransport.Create(server, TimeSpan.FromSeconds(8), ProfileCodec.MaxBytes * 2)
            : new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = ProfileCodec.MaxBytes * 2 };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var process = Process.GetCurrentProcess();
        _lease = new(process.Id, process.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"));
    }
    internal static async Task<AccountSettingsSync> Connect(Uri server, string token, CancellationToken ct, bool recoverLocal = false)
    {
        var sync = new AccountSettingsSync(server, token, AppPaths.DataDirectory, ConfigStore.Load,
            config => { ConfigStore.BackupAccountSettings("before-restore"); ConfigStore.Save(config, replaceBusinessState: true); },
            ConfigStore.BackupAccountSettings, otherProcesses: OtherInstalledProcesses);
        sync.RecoverLocalSettings = recoverLocal;
        try { await Task.Run(() => sync.Initialize(ct), ct); return sync; }
        catch { sync.Dispose(); throw; }
    }
    private static IReadOnlyList<int> OtherInstalledProcesses()
    {
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("DiscordChatHUD").Concat(Process.GetProcessesByName("DiscordChatHUD_Config")))
        using (process)
        {
            if (process.Id == Environment.ProcessId || GameAutoLaunch.IsUtility(process.Id)) continue;
            try
            {
                if (string.Equals(Path.GetDirectoryName(process.MainModule?.FileName), AppPaths.BaseDirectory, StringComparison.OrdinalIgnoreCase)) result.Add(process.Id);
            }
            catch { }
        }
        return result;
    }
    private static bool Alive(Lease lease)
    {
        try { using var process = Process.GetProcessById(lease.Pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.Started; }
        catch { return false; }
    }
    private T WithGate<T>(Func<T> action, CancellationToken ct)
    {
        using var mutex = new Mutex(false, AppInstance.ScopedName("AccountProfile", _directory));
        var acquired = false;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!acquired)
            {
                ct.ThrowIfCancellationRequested();
                try { acquired = mutex.WaitOne(100); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired && DateTime.UtcNow >= until) throw new IOException("다른 창에서 설정을 저장 중입니다. 잠시 후 다시 시도해주세요.");
            }
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private Metadata? ReadMetadata()
    {
        if (!File.Exists(_metadataPath)) return null;
        if (new FileInfo(_metadataPath).Length > 65536) throw new InvalidDataException("계정 설정 연결 기록을 확인하지 못했습니다.");
        var meta = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(_metadataPath), ProfileCodec.Json) ?? throw new InvalidDataException();
        meta.LocalHash ??= "";
        if (!ulong.TryParse(meta.UserId, out var id) || id == 0 || meta.Version < 0 || meta.Hash.Length != 64 || meta.LocalHash.Length is not (0 or 64)
            || meta.Leases is null || meta.Leases.Count > 32)
            throw new InvalidDataException("계정 설정 연결 기록이 손상되었습니다. 기존 설정은 유지됩니다.");
        meta.Leases.RemoveAll(lease => !Alive(lease)); return meta;
    }
    private void WriteMetadata(Metadata meta) => ConnectionDiagnostics.At("LOCAL-META-WRITE", () => { WriteMetadataCore(meta); return true; });
    private void WriteMetadataCore(Metadata meta)
    {
        Directory.CreateDirectory(_directory);
        var temp = _metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(meta, ProfileCodec.Json)); File.Move(temp, _metadataPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void EnsureResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("계정 설정 복원을 위해 운영자가 중계 서버도 Preview210 이상으로 교체해야 합니다.");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("계정 설정 접근 권한을 확인하지 못했습니다. 로그인과 고닉 역할을 확인해주세요.");
        response.EnsureSuccessStatusCode();
    }
    private AccountProfile ReadProfile(CancellationToken ct)
    {
        using var response = _http.GetAsync("v1/profile", ct).GetAwaiter().GetResult();
        EnsureResponse(response); return Parse(response, ct);
    }
    private AccountProfile Parse(HttpResponseMessage response, CancellationToken ct)
    {
        var profile = JsonSerializer.Deserialize<AccountProfile>(response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult(), ProfileCodec.Json) ?? throw new InvalidDataException();
        if (!ulong.TryParse(profile.UserId, out var user) || user == 0 || profile.Version < 0
            || (profile.Version == 0) != (profile.Settings is null) || (_user.Length > 0 && profile.UserId != _user)) throw new InvalidDataException("계정 설정 응답을 확인하지 못했습니다.");
        if (profile.Settings is { } data) _ = ProfileCodec.Decode(data);
        return profile;
    }
    private AccountProfile? Upload(long version, JsonElement settings, CancellationToken ct, long? recoveryVersion = null) => ConnectionDiagnostics.At("PROFILE-SAVE", () => UploadCore(version, settings, ct, recoveryVersion));
    private AccountProfile? UploadCore(long version, JsonElement settings, CancellationToken ct, long? recoveryVersion = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "v1/profile") { Content = JsonContent.Create(new ProfileUpdate(version, settings), options: ProfileCodec.Json) };
        if (recoveryVersion is { } floor) request.Headers.Add("X-HUD-Recovery-Version", floor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = _http.SendAsync(request, ct).GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        EnsureResponse(response); return Parse(response, ct);
    }
    internal void Initialize(CancellationToken ct) => WithGate(() =>
    {
        var remote = ConnectionDiagnostics.At("PROFILE-READ", () => ReadProfile(ct)); _user = remote.UserId;
        var meta = ConnectionDiagnostics.At("LOCAL-META", ReadMetadata); var same = meta?.UserId == _user && meta.Origin == _origin;
        if (_otherProcesses().Any(pid => meta?.Leases.Any(lease => lease.Pid == pid) != true))
            throw new InvalidOperationException("이 폴더의 이전 버전 HUD/설정 창을 먼저 닫고 다시 실행해주세요.");
        if (same && remote.Version < meta!.Version)
        {
            if (!RecoverLocalSettings) throw new ServerRollbackException();
            if (meta.Leases.Count > 0) throw new InvalidOperationException("복구 전 이 폴더의 다른 HUD/설정 창을 닫아주세요.");
            var recovered = ProfileCodec.Encode(_read());
            _backup("before-server-recovery");
            // Keep the newer local settings; retain the older server snapshot for recovery.
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "account-server-before-recovery-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(remote, ProfileCodec.Json));
            var saved = Upload(remote.Version, recovered, ct, meta.Version)
                ?? throw new InvalidOperationException("다른 PC에서 설정이 변경되었습니다. 다시 연결해주세요.");
            WriteMetadata(new Metadata { Origin = _origin, UserId = _user, Version = saved.Version,
                Hash = ProfileCodec.Hash(saved.Settings!.Value), LocalHash = ProfileCodec.Hash(recovered),
                ServerResetApplied = meta.ServerResetApplied, Leases = [_lease] });
            _joined = true; SetStatus("이 PC의 최신 설정 유지 · 계정 복구 완료"); return true;
        }
        if (meta is { Leases.Count: > 0 })
        {
            if (!same) throw new InvalidOperationException("다른 계정의 HUD/설정 창이 실행 중입니다. 먼저 닫고 다시 로그인해주세요.");
            if (meta.Conflict) throw new InvalidOperationException("다른 PC에서 설정이 변경되었습니다. 이 폴더의 HUD와 설정 창을 모두 닫고 다시 실행해주세요.");
            meta.Leases.Add(_lease); WriteMetadata(meta); _joined = true;
            SetStatus("계정 설정 자동 저장 연결됨"); return true;
        }
        var local = ProfileCodec.Encode(_read()); var localHash = ProfileCodec.Hash(local);
        var dirty = same && meta!.Hash != localHash && meta.LocalHash != localHash;
        // Nothing changed on either side since this PC last synced: the server copy
        // may still differ in encoding (older/newer settings model), not in content.
        var unchangedSinceSync = same && !dirty && meta!.Version == remote.Version;
        if (remote.Settings is null)
        {
            // A new account must never inherit the previous account's local settings.
            if (meta is not null && !same) { _restore(ConfigStore.CreateDistributionDefault()); local = ProfileCodec.Encode(_read()); localHash = ProfileCodec.Hash(local); }
            remote = Upload(0, local, ct) ?? throw new InvalidOperationException("다른 PC에서 설정을 먼저 저장했습니다. 다시 연결해주세요.");
        }
        else if (same && dirty && !meta!.Conflict && meta.Version == remote.Version)
        {
            remote = Upload(remote.Version, local, ct) ?? throw new InvalidOperationException("다른 PC에서 설정이 변경되었습니다. 다시 연결해주세요.");
        }
        else if (!unchangedSinceSync && ProfileCodec.Hash(remote.Settings.Value) != localHash)
        {
            if (dirty)
            {
                _backup("conflict");
                Notice = "다른 PC에서 저장한 최신 설정을 불러왔습니다. 이 PC의 미전송 변경 내용은 Data 폴더에 백업했습니다.";
            }
            var restored = ProfileCodec.DecodeForRestore(remote.Settings.Value, _read());
            localHash = ProfileCodec.Hash(ProfileCodec.Encode(restored));
            if (same && ProfileCodec.PreserveNewerBusinessState(restored, _read()))
                Notice = "이 PC의 더 최신인 사업장 상태를 유지했습니다. 서버에 다시 저장합니다.";
            _restore(restored);
        }
        var next = new Metadata { Origin = _origin, UserId = _user, Version = remote.Version,
            Hash = ProfileCodec.Hash(remote.Settings!.Value), LocalHash = localHash,
            ServerResetApplied = same && meta!.ServerResetApplied, Leases = [_lease] };
        WriteMetadata(next); _joined = true; SetStatus("내 계정 설정 복원 완료 · 자동 저장 중"); return true;
    }, ct);
    internal void SyncOnce(CancellationToken ct) => WithGate(() =>
    {
        var meta = ReadMetadata() ?? throw new InvalidDataException();
        if (meta.UserId != _user || meta.Origin != _origin) throw new InvalidOperationException("계정이 변경되어 자동 저장을 중단했습니다.");
        if (meta.Conflict) { SetStatus("다른 PC 설정 변경 · HUD와 설정 창을 닫고 다시 실행"); return false; }
        var local = ProfileCodec.Encode(_read()); var hash = ProfileCodec.Hash(local);
        if (hash == meta.Hash || hash == meta.LocalHash) { SetStatus("내 계정 설정 · 서버 저장 완료"); return true; }
        var saved = Upload(meta.Version, local, ct);
        if (saved is null)
        {
            // Covers both a true conflict and a successful PUT whose HTTP response was lost.
            var latest = ReadProfile(ct);
            if (latest.Settings is { } settings && ProfileCodec.Hash(settings) == hash) saved = latest;
            else
            {
                _backup("conflict"); meta.Conflict = true; WriteMetadata(meta);
                SetStatus("다른 PC 설정 변경 · HUD와 설정 창을 닫고 다시 실행"); return false;
            }
        }
        meta.Version = saved.Version; meta.Hash = ProfileCodec.Hash(saved.Settings!.Value); meta.LocalHash = hash; WriteMetadata(meta);
        SetStatus("내 계정 설정 · 서버 저장 완료"); return true;
    }, ct);
    internal void Start()
    {
        if (!_joined) throw new InvalidOperationException();
        _worker ??= Task.Run(async () =>
        {
            var pause = 10;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(pause), _stop.Token);
                    SyncOnce(_stop.Token); pause = 10;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (InvalidOperationException) { SetStatus("계정 설정 연결 확인 필요 · 다시 로그인해주세요"); return; }
                catch { SetStatus("계정 설정 저장 대기 · 연결 복구 후 재시도"); pause = Math.Min(30, pause * 2); }
            }
        });
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _stop.Cancel(); try { _worker?.GetAwaiter().GetResult(); } catch { }
        if (_joined)
        {
            using var final = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { SyncOnce(final.Token); } catch { SetStatus("미전송 설정은 다음 실행 때 다시 저장합니다"); }
            try
            {
                WithGate(() => { var meta = ReadMetadata(); if (meta is not null) { meta.Leases.RemoveAll(lease => lease.Instance == _lease.Instance); WriteMetadata(meta); } return true; }, CancellationToken.None);
            }
            catch { }
        }
        _http.Dispose(); _stop.Dispose();
    }
}
