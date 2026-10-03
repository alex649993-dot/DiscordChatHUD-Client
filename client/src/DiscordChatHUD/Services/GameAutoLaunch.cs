using System.Diagnostics;
using Microsoft.Win32;
namespace DiscordChatHUD.Services;
internal static class GameAutoLaunch
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "DiscordChatHUD.Beta.GameAutoLaunch";
    internal static string ConfigSignal => AppInstance.ScopedName("AutoConfig", AppContext.BaseDirectory);
    internal static string HudStopSignal => AppInstance.ScopedName("AutoHudStop", AppContext.BaseDirectory);
    internal static string ConfigStopSignal => AppInstance.ScopedName("AutoConfigStop", AppContext.BaseDirectory);
    static string WatchStopSignal => AppInstance.ScopedName("AutoWatchStop", AppContext.BaseDirectory);
    internal static void Signal(string name) { try { using var e=EventWaitHandle.OpenExisting(name); e.Set(); } catch(WaitHandleCannotBeOpenedException) { } }
    static string PauseSignal => AppInstance.ScopedName("AutoManualPause", AppContext.BaseDirectory);
    internal static bool SessionPaused { get { try { using var e=EventWaitHandle.OpenExisting(PauseSignal); return e.WaitOne(0); } catch(WaitHandleCannotBeOpenedException) { return false; } } }
    internal static void PauseForSession() => Signal(PauseSignal);
    internal static void StopHud() { PauseForSession(); Signal(HudStopSignal); }
    internal static void EnableAndRestart()
    {
        SetEnabled(true, false);
        StartWatcher("--reset-auto " + Environment.ProcessId);
    }
    static string WatchMutex => AppInstance.ScopedName("GameWatcher", AppContext.BaseDirectory);
    internal const string WatcherFile = "DiscordChatHUD_Watcher.exe";
    internal static bool MatchesRegistration(string? registered,string directory) =>
        new[]{WatcherFile,"DiscordChatHUD_Config.exe","DiscordChatHUD.exe"}.Any(file=>string.Equals(registered,
            "\""+Path.Combine(directory,file)+"\" --watch-gta",StringComparison.OrdinalIgnoreCase));
    static string Command => "\"" + Path.Combine(AppContext.BaseDirectory, "DiscordChatHUD.exe") + "\" --watch-gta";
    internal static bool Enabled { get { using var key=Registry.CurrentUser.OpenSubKey(RunKey); return MatchesRegistration(key?.GetValue(ValueName) as string, AppContext.BaseDirectory); } }
    internal static void SetEnabled(bool enabled, bool start = true)
    {
        if(enabled && DesktopContext.Current is null) WatcherExecutable.Ensure();
        using var key=Registry.CurrentUser.CreateSubKey(RunKey);
        if(enabled) key.SetValue(ValueName,Command); else if(Enabled) key.DeleteValue(ValueName,false);
        if(enabled && start) EnsureWatcher();
        if(!enabled) { StopHud(); if (DesktopContext.Current is { } desktop) desktop.RefreshWatch(); else Signal(WatchStopSignal); }
    }
    private static string UtilityMarker(int pid,long ticks) => AppInstance.ScopedName("Utility."+pid+"."+ticks,AppContext.BaseDirectory);
    internal static EventWaitHandle MarkUtility()
    {
        using var p=Process.GetCurrentProcess();
        return new EventWaitHandle(false,EventResetMode.ManualReset,UtilityMarker(p.Id,p.StartTime.ToUniversalTime().Ticks));
    }
    internal static bool IsUtility(int pid)
    {
        try { using var p=Process.GetProcessById(pid); using var marker=EventWaitHandle.OpenExisting(UtilityMarker(pid,p.StartTime.ToUniversalTime().Ticks)); return true; }
        catch { return false; }
    }
    internal static bool Busy(string name)
    {
        using var mutex=new Mutex(false,name); bool held;
        try { held=mutex.WaitOne(0); } catch(AbandonedMutexException) { held=true; }
        if(held) mutex.ReleaseMutex(); return !held;
    }
    internal static bool HudRunning()=>DesktopContext.Current?.HudRunning ?? Busy(AppInstance.HudMutexName);
    internal static bool IsGameRunning()
    {
        foreach(var name in new[]{"gta5_Enhanced","GTA5Enhanced","GTA5"})
        {
            var list=Process.GetProcessesByName(name);
            try { if(list.Any(p=>!p.HasExited)) return true; }
            finally { foreach(var p in list)p.Dispose(); }
        }
        return false;
    }
    internal static bool IsUpdating(string directory)
    {
        try { using var e=EventWaitHandle.OpenExisting(AppInstance.ScopedName("UpdateInProgress",directory)); return e.WaitOne(0); }
        catch(WaitHandleCannotBeOpenedException){return false;}
    }
    internal static void MoveRegistration(IEnumerable<string> oldDirectories, string destination)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        var registered = key?.GetValue(ValueName) as string;
        if (oldDirectories.Any(directory => MatchesRegistration(registered,directory)))
            key!.SetValue(ValueName, "\"" + Path.Combine(destination, "DiscordChatHUD_Config.exe") + "\" --watch-gta");
    }
    internal static void StopWatcherForUpdate(string directory)
    {
        Signal(AppInstance.ScopedName("AutoManualPause",directory));
        Signal(AppInstance.ScopedName("AutoWatchStop",directory));
    }
    internal static void AdoptIntegratedWatcher()
    {
        // Preserve the user's enabled/disabled choice; only migrate an existing registration.
        if (Enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, Command);
        }
        using var current = Process.GetCurrentProcess();
        var watcherPath = Path.Combine(AppContext.BaseDirectory, WatcherFile);
        foreach (var old in Process.GetProcessesByName("DiscordChatHUD_Watcher"))
        using (old)
        {
            if (old.Id == current.Id || old.SessionId != current.SessionId || old.HasExited) continue;
            if (!string.Equals(old.MainModule?.FileName, watcherPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (FileVersionInfo.GetVersionInfo(watcherPath).FileBuildPart >= 264) continue;
            if (!EventWaitHandle.TryOpenExisting(WatchStopSignal, out var stop)) continue;
            using (stop)
            {
                stop.Set();
                if (!old.WaitForExit(5000)) throw new IOException("이전 자동 감시가 종료 중입니다. 잠시 후 다시 실행해주세요.");
                stop.Reset();
            }
        }
    }
    internal static void EnsureWatcher()
    {
        if (DesktopContext.Current is { } desktop) { desktop.RefreshWatch(); return; }
        if(!Enabled||IsUpdating(AppContext.BaseDirectory))return;
        WatcherExecutable.Ensure();
        using(var key=Registry.CurrentUser.CreateSubKey(RunKey))key.SetValue(ValueName,Command);
        if(Busy(WatchMutex))return;
        Start(WatcherFile,"--watch-gta");
    }
    internal static void StartWatcher(string arguments) { if (DesktopContext.Current is { } desktop) { desktop.Queue(arguments.StartsWith("--reset-auto") ? DesktopCommand.Reset : DesktopCommand.Watch); return; } WatcherExecutable.Ensure(); Start(WatcherFile,arguments); }
    static void Start(string file,string args)
    {
        using var p=Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,file),args){UseShellExecute=false,WorkingDirectory=AppContext.BaseDirectory,CreateNoWindow=true});
    }
}

internal sealed class GameTransition
{
    bool running;
    internal int Observe(bool next)
    {
        int edge=next==running ? 0 : next ? 1 : -1;
        running=next; return edge;
    }
}

internal enum AutoHudAction { None, Start, Stop }
internal sealed class AutoHudPolicy
{
    bool running;
    long lastStart=long.MinValue;
    internal AutoHudAction Observe(bool game, bool hud, bool paused, long now)
    {
        bool stopped=running && !game;
        if(game && !running) lastStart=long.MinValue;
        running=game;
        if(!game) return stopped && hud ? AutoHudAction.Stop : AutoHudAction.None;
        if(hud || paused) return AutoHudAction.None;
        if(lastStart!=long.MinValue && now-lastStart<5000) return AutoHudAction.None;
        lastStart=now; return AutoHudAction.Start;
    }
}
