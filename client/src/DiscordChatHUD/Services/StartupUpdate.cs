using System.Text.Json;
using DiscordChatHUD.Logging;
namespace DiscordChatHUD.Services;
internal static class StartupUpdate
{
    internal sealed record Attempt(int Version, DateTimeOffset Time);
    internal static bool ShouldInstall(int current, int offered, Attempt? last, DateTimeOffset now)
        => offered > current && !(last is not null && last.Version == offered && now - last.Time < TimeSpan.FromHours(1));
    internal static async Task<bool> TryApply(Action<string> status, CancellationToken ct)
    {
        string? download = null;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            // Cross-process exclusion does not rely on async thread-affine mutex ownership.
            using var gate = new FileStream(Path.Combine(AppPaths.DataDirectory,"startup-update.lock"), FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            status("복구 정책 확인 중…");
            using(var recoveryStop=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                recoveryStop.CancelAfter(3000);
                if(await RecoveryCoordinator.TryStartServerRepair(recoveryStop.Token))
                {
                    status("자동 복구를 준비한 뒤 다시 실행합니다.");
                    return true;
                }
            }
            status("새 버전 확인 중…");
            using var checkStop=CancellationTokenSource.CreateLinkedTokenSource(ct);checkStop.CancelAfter(2000);
            var update=await AppUpdate.Check(checkStop.Token);
            var marker=Path.Combine(AppPaths.DataDirectory,"startup-update-attempt.json");
            Attempt? last=null;
            try { if(File.Exists(marker)) last=JsonSerializer.Deserialize<Attempt>(File.ReadAllText(marker)); } catch { }
            if(!ShouldInstall(AppUpdate.CurrentVersion,update.Manifest.Version,last,DateTimeOffset.UtcNow)) return false;
            // A broken release/network must not create an endless launch/update loop.
            File.WriteAllText(marker,JsonSerializer.Serialize(new Attempt(update.Manifest.Version,DateTimeOffset.UtcNow)));
            status("새 버전 자동 다운로드 중…");
            download=await AppUpdate.Download(update.Manifest,update.Envelope,new Progress<int>(p=>status($"새 버전 자동 다운로드 중 · {p}%")),ct);
            ct.ThrowIfCancellationRequested();
            status("업데이트 적용 후 다시 실행합니다. 설정과 로그인은 유지됩니다.");
            AppUpdate.StartInstaller(download); download=null; return true;
        }
        catch(OperationCanceledException) { return false; }
        catch(Exception ex)
        {
            try
            {
                var state=RecoveryStateStore.Load(AppContext.BaseDirectory);
                state.ConsecutiveUpdateFailures++;
                state.LastFailureCode=ex is InvalidDataException ? "UPDATE-DATA" : ex is IOException ? "UPDATE-FILE" : null;
                RecoveryStateStore.Save(AppContext.BaseDirectory,state);
            }
            catch { }
            AppLog.Warn("시작 업데이트 확인/적용 보류: "+ex.GetType().Name);return false;
        }
        finally { if(download is not null) AppUpdate.Clean(download); }
    }
}
