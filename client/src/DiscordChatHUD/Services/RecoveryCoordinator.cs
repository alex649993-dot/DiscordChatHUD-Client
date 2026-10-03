using System.Diagnostics;

namespace DiscordChatHUD.Services;

internal static class RecoveryCoordinator
{
    internal static async Task<bool> TryStartServerRepair(CancellationToken ct)
    {
        var policy = await RecoveryPolicyClient.Check(AppUpdate.CurrentVersion, ct);
        if (policy.Action != "self-heal") return false;
        var state = RecoveryStateStore.Load(AppContext.BaseDirectory);
        var now = DateTimeOffset.UtcNow;
        var quick=InstallIntegrity.Quick(AppContext.BaseDirectory,AppUpdate.CurrentVersion);
        var needsFullCheck=!quick.Healthy
                           || state.ConsecutiveStartupFailures>=2
                           || state.ConsecutiveUpdateFailures>=2
                           || state.LastIntegrityCheckUtc is null
                           || now-state.LastIntegrityCheckUtc.Value>=TimeSpan.FromHours(24);
        if(!needsFullCheck) return false;
        var reason=quick.Code;
        if(quick.Healthy)
        {
            try
            {
                using var verifyStop=CancellationTokenSource.CreateLinkedTokenSource(ct);
                verifyStop.CancelAfter(TimeSpan.FromSeconds(20));
                var recovery=await AppUpdate.CheckRecovery(AppUpdate.CurrentVersion,verifyStop.Token);
                var full=InstallIntegrity.Full(AppContext.BaseDirectory,recovery.Manifest);
                state.LastIntegrityCheckUtc=now;
                RecoveryStateStore.Save(AppContext.BaseDirectory,state);
                if(full.Healthy) return false;
                reason=full.Code;
            }
            catch (RecoveryManifestMissingException) when (state.ConsecutiveStartupFailures>=2 || state.ConsecutiveUpdateFailures>=2)
            { reason="RECOVERY-MANIFEST-MISSING"; }
            catch
            {
                // A recovery-server outage must never block an otherwise healthy startup.
                return false;
            }
        }
        if (!RecoveryStateStore.CanRepair(state, now))
        {
            state.LastFailureCode = "REPAIR-COOLDOWN";
            RecoveryStateStore.Save(AppContext.BaseDirectory, state);
            return false;
        }
        WatcherExecutable.Ensure();
        var watcher = Path.Combine(AppContext.BaseDirectory, GameAutoLaunch.WatcherFile);
        var start = new ProcessStartInfo(watcher) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true };
        start.ArgumentList.Add("--repair-client");
        start.ArgumentList.Add(AppContext.BaseDirectory);
        start.ArgumentList.Add(reason=="OK" ? policy.ReasonCode : reason);
        start.ArgumentList.Add(policy.PolicyId);
        start.ArgumentList.Add(policy.CooldownMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Process.Start(start) ?? throw new IOException("복구 도우미를 실행하지 못했습니다.");
        return true;
    }

    internal static int? TryWatcherStartupRepair()
    {
        ClientRecoveryPolicy policy;
        try
        {
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            policy=RecoveryPolicyClient.Check(AppUpdate.CurrentVersion,stop.Token).GetAwaiter().GetResult();
        }
        catch { return null; }
        if(policy.Action!="self-heal") return null;
        var quick=InstallIntegrity.Quick(AppContext.BaseDirectory,AppUpdate.CurrentVersion);
        if(!quick.Healthy)
            return RunRepair(AppContext.BaseDirectory,quick.Code,policy.PolicyId,policy.CooldownMinutes);
        var state=RecoveryStateStore.Load(AppContext.BaseDirectory);
        if(state.ConsecutiveStartupFailures>=2 || state.ConsecutiveUpdateFailures>=2)
        {
            try
            {
                using var verifyStop=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var update=AppUpdate.CheckRecovery(AppUpdate.CurrentVersion,verifyStop.Token).GetAwaiter().GetResult();
                var full=InstallIntegrity.Full(AppContext.BaseDirectory,update.Manifest);
                if(!full.Healthy)
                    return RunRepair(AppContext.BaseDirectory,full.Code,policy.PolicyId,policy.CooldownMinutes);
                state.LastIntegrityCheckUtc=DateTimeOffset.UtcNow;
                RecoveryStateStore.Save(AppContext.BaseDirectory,state);
            }
            catch (RecoveryManifestMissingException)
            { return RunRepair(AppContext.BaseDirectory,"RECOVERY-MANIFEST-MISSING",policy.PolicyId,policy.CooldownMinutes); }
            catch { }
        }
        return null;
    }

    internal static int RunRepair(string destination, string reasonCode, string policyId, int cooldownMinutes)
    {
        destination = Path.GetFullPath(destination);
        if (!Directory.Exists(destination)) return 2;
        for (var part = destination; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                return 3;

        using var gate = new Mutex(false, AppInstance.ScopedName("RecoveryInProgress", destination));
        bool held;
        try { held = gate.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held || GameAutoLaunch.IsUpdating(destination)) return 4;
        try
        {
            var state = RecoveryStateStore.Load(destination);
            var now = DateTimeOffset.UtcNow;
            var policy = new ClientRecoveryPolicy(1, policyId, "stable", 1, 100000, "self-heal",
                reasonCode, null, null, Math.Clamp(cooldownMinutes,5,1440));
            if (!RecoveryStateStore.CanRepair(state, now)) return 5;
            RecoveryStateStore.BeginRepair(state, policy, now);
            state.LastFailureCode = "REPAIR-START";
            RecoveryStateStore.Save(destination, state);

            string? download = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var installed = InstalledVersion(destination);
                var expectedVersion=installed>0 ? installed : AppUpdate.CurrentVersion;
                var update = AppUpdate.CheckRecovery(expectedVersion,timeout.Token,allowLatest:true).GetAwaiter().GetResult();
                RecoveryReleaseFallback.ValidateLatest(update.Manifest,expectedVersion);

                var integrity = InstallIntegrity.Full(destination, update.Manifest);
                if (update.Manifest.Version == expectedVersion && integrity.Healthy)
                {
                    GameAutoLaunch.StopWatcherForUpdate(destination);
                    ClientLifetime.CloseOtherInstallations(destination,includeDestination:true);
                    ClientLifetime.WaitUntilAllClosed(destination);
                    state.LastFailureCode = null;
                    state.LastIntegrityCheckUtc=DateTimeOffset.UtcNow;
                    state.PendingVerificationVersion = expectedVersion;
                    RecoveryStateStore.Save(destination, state);
                    StartConfig(destination, null);
                    return 0;
                }

                var recovered = RecoveryReleaseFallback.Download(expectedVersion, update,
                    () => AppUpdate.Check(timeout.Token),
                    (manifest,envelope) => AppUpdate.Download(manifest,envelope,new Progress<int>(),timeout.Token))
                    .GetAwaiter().GetResult();
                update = recovered.Update;
                download = recovered.Folder;
                ApplyDownloadedRepair(destination,download,update.Manifest);
                state.PendingVerificationVersion = update.Manifest.Version;
                state.LastIntegrityCheckUtc=DateTimeOffset.UtcNow;
                state.LastFailureCode = null;
                RecoveryStateStore.Save(destination, state);
                StartConfig(destination, download);
                download = null;
                return 0;
            }
            catch (Exception ex)
            {
                state.ConsecutiveUpdateFailures++;
                state.LastFailureCode = ex switch
                {
                    HttpRequestException => "REPAIR-FAIL-HTTP",
                    InvalidDataException => "REPAIR-FAIL-DATA",
                    _ => "REPAIR-FAIL-FILE"
                };
                RecoveryStateStore.Save(destination, state);
                Logging.AppLog.Error("자동 복구 실패", ex);
                return 10;
            }
            finally { if (download is not null) AppUpdate.Clean(download); }
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static int Verify(string destination)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var installed=InstalledVersion(destination);
            var expected=installed>0 ? installed : AppUpdate.CurrentVersion;
            var update = AppUpdate.CheckRecovery(expected,timeout.Token).GetAwaiter().GetResult();
            return InstallIntegrity.Full(Path.GetFullPath(destination), update.Manifest).Healthy ? 0 : 1;
        }
        catch { return 2; }
    }

    internal static void ApplyDownloadedRepair(string destination,string folder,UpdateManifest manifest)
    {
        destination=Path.GetFullPath(destination);
        var installed=InstalledVersion(destination);
        var expectedVersion=installed>0 ? installed : AppUpdate.CurrentVersion;
        RecoveryReleaseFallback.ValidateLatest(manifest,expectedVersion);
        AppUpdate.ValidatePackage(folder,manifest);
        GameAutoLaunch.StopWatcherForUpdate(destination);
        ClientLifetime.CloseOtherInstallations(destination,includeDestination:true);
        ClientLifetime.WaitUntilAllClosed(destination);
        AppUpdate.BackupUserSettings(destination);
        ClientLifetime.AssertAllClosed();
        AppUpdate.Apply(destination,folder);
    }

    private static int InstalledVersion(string destination)
    {
        var config = Path.Combine(destination, "DiscordChatHUD_Config.exe");
        if (!File.Exists(config)) return 0;
        try { return Math.Max(0, FileVersionInfo.GetVersionInfo(config).FileBuildPart); }
        catch { return 0; }
    }

    private static void StartConfig(string destination, string? cleanupFolder)
    {
        var config = Path.Combine(destination, "DiscordChatHUD_Config.exe");
        if (!File.Exists(config)) throw new FileNotFoundException("복구 후 설정 실행파일이 없습니다.", config);
        var start = new ProcessStartInfo(config) { UseShellExecute = false, WorkingDirectory = destination };
        if (!string.IsNullOrEmpty(cleanupFolder)) start.ArgumentList.Add("--cleanup-update=" + cleanupFolder);
        _ = Process.Start(start) ?? throw new IOException("복구 후 설정 프로그램을 실행하지 못했습니다.");
    }
}
