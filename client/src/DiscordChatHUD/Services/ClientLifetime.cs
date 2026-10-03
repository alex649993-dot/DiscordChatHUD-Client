using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiscordChatHUD.Services;

internal static class ClientLifetime
{
    private const string TransferMutex = @"Local\DiscordChatHUD.CSharp.InstallTransfer.v1";

    internal static bool IsKnownProduct(string? product)
        => product is not null
           && (product.StartsWith("DiscordChatHUD C# Beta ", StringComparison.Ordinal)
               || product.StartsWith("DiscordChatHUD C# Preview ", StringComparison.Ordinal));

    internal static string? TryGetClientPath(Process process, bool failClosed)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return process.MainModule?.FileName; }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 299)
            {
                try { if (process.HasExited) return null; } catch (InvalidOperationException) { return null; }
                Logging.AppLog.Warn($"클라이언트 경로 조회 Windows 299 재시도 · PID {process.Id} · {attempt + 1}/3");
                if (attempt < 2) { Thread.Sleep(100); continue; }
                if (!failClosed) return null;
                throw new IOException("클라이언트 종료 상태를 확인하는 중 Windows 오류 299가 반복되었습니다. 잠시 후 다시 시도해주세요.", ex);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (!failClosed) return null;
                throw new IOException("클라이언트 종료 상태를 확인할 권한이 없어 업데이트를 중단합니다.", ex);
            }
        }
        return null;
    }

    internal static bool IsClient(string? path, string? product, int session, int currentSession)
        => session == currentSession && path is not null
           && (Path.GetFileName(path).Equals("DiscordChatHUD.exe", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(path).Equals("DiscordChatHUD_Config.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("DiscordChatHUD_Watcher.exe", StringComparison.OrdinalIgnoreCase))
           && IsKnownProduct(product);

    internal static bool SameDirectory(string first, string second)
        => string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // Graceful transfer between install directories. No force kill: let profiles flush first.
    internal static void CloseOtherInstallations(string destination, bool includeDestination = false, Func<Process[]>? processProvider = null)
    {
        using var gate = new Mutex(false, TransferMutex);
        bool owned;
        try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("다른 클라이언트가 종료 처리 중입니다. 잠시 후 다시 실행해주세요.");
        try
        {
            using var current = Process.GetCurrentProcess();
            var targets = new List<Process>();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var process in processProvider?.Invoke() ?? Process.GetProcesses())
                {
                    bool keep = false;
                    try
                    {
                        if (process.Id == current.Id) continue;
                        // Avoid opening modules for unrelated applications.
                        if (process.ProcessName is not ("DiscordChatHUD" or "DiscordChatHUD_Config" or "DiscordChatHUD_Watcher")) continue;
                        if (process.SessionId != current.SessionId) continue;
                        string? path = TryGetClientPath(process, includeDestination);
                        if (!IsClient(path, path is null ? null : FileVersionInfo.GetVersionInfo(path).ProductName, process.SessionId, current.SessionId)) continue;
                        string directory = Path.GetDirectoryName(path!)!;
                        if (!includeDestination && (SameDirectory(directory, destination) || SameDirectory(directory, AppContext.BaseDirectory))) continue;
                        directories.Add(directory); targets.Add(process); keep = true;
                    }
                    catch (InvalidOperationException) { }
                    finally { if (!keep) process.Dispose(); }
                }
                var graceful = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var directory in directories)
                {
                    GameAutoLaunch.StopWatcherForUpdate(directory);
                    bool configSignalled = TrySignal(AppInstance.ScopedName("AutoConfigStop", directory));
                    bool hudSignalled = TrySignal(AppInstance.ScopedName("AutoHudStop", directory));
                    if (configSignalled || hudSignalled) graceful.Add(directory);
                }
                // Also covers an old login dialog that has not installed its stop event yet.
                foreach (var process in targets)
                {
                    if (process.HasExited) continue;
                    // A normal stop event flushes pending settings. Do not race it with WM_CLOSE.
                    if (graceful.Contains(Path.GetDirectoryName(process.MainModule!.FileName)!)) continue;
                    int id = process.Id;
                    EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == id) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
                }
                var deadline = Stopwatch.StartNew();
                foreach (var process in targets)
                    if (!process.HasExited && !process.WaitForExit(5000))
                    {
                        // A login/secondary window may have no stop event even when its directory does.
                        int id = process.Id;
                        EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var pid); if (pid == id) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
                        if (!process.WaitForExit((int)Math.Max(0, 30_000 - deadline.ElapsedMilliseconds)))
                        throw new IOException("이전 폴더의 클라이언트가 설정 저장 또는 종료 처리 중입니다. 종료가 끝난 뒤 다시 실행해주세요.");
                    }
                GameAutoLaunch.MoveRegistration(directories, destination);
                if (targets.Count > 0) Logging.AppLog.Info($"이전 설치 클라이언트 정상 종료 확인 · {targets.Count}개");
            }
            finally { foreach (var process in targets) process.Dispose(); }
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static void AssertAllClosed(Func<Process[]>? provider = null)
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in provider?.Invoke() ?? Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == current.Id) continue;
                    if (process.ProcessName is not ("DiscordChatHUD" or "DiscordChatHUD_Config" or "DiscordChatHUD_Watcher")) continue;
                    if (process.HasExited || process.SessionId != current.SessionId) continue;
                    string? path = TryGetClientPath(process, failClosed: true);
                    if (path is null)
                    {
                        try { if (process.HasExited) continue; } catch (InvalidOperationException) { continue; }
                        throw new IOException("클라이언트 실행 경로를 확인하지 못했습니다.");
                    }
                    if (IsClient(path, FileVersionInfo.GetVersionInfo(path).ProductName, process.SessionId, current.SessionId))
                        throw new IOException($"클라이언트 PID {process.Id}가 아직 실행 중입니다. 파일 교체를 중단합니다.");
                }
                catch (InvalidOperationException) { /* Process exited during inspection. */ }
                catch (System.ComponentModel.Win32Exception ex) { throw new IOException($"클라이언트 PID {process.Id} 종료 확인에 실패했습니다 (Windows 오류 {ex.NativeErrorCode}). 파일을 교체하지 않습니다.", ex); }
            }
        }
    }

    internal static void WaitUntilAllClosed(string destination)
    {
        // Catch processes that were starting while the first snapshot was taken.
        for (int pass = 0; pass < 3; pass++)
        {
            Thread.Sleep(350);
            CloseOtherInstallations(destination, includeDestination: true);
            AssertAllClosed();
        }
        Logging.AppLog.Info("업데이트 사전 검사 완료 · 설정창/HUD/자동실행 감시 잔류 0개");
    }

    private static bool TrySignal(string name)
    {
        try { using var signal = EventWaitHandle.OpenExisting(name); signal.Set(); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
