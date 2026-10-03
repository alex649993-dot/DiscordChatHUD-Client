using System.Diagnostics;

namespace DiscordChatHUD.Services;

internal static class LegacyUpdateRestart
{
    internal static bool NeedsRestart(int version) => version > 0 && version < 254;

    internal static bool IsUpdateFolder(string folder)
    {
        var full=Path.GetFullPath(folder).TrimEnd('\\','/');
        const string prefix="DiscordChatHUD-Update-";
        var name=Path.GetFileName(full);
        return string.Equals(Path.GetDirectoryName(full),Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(prefix,StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..],"N",out _)
            && Directory.Exists(full) && (File.GetAttributes(full)&FileAttributes.ReparsePoint)==0;
    }

    internal static bool TryStart(string[] args)
    {
        var cleanup=args.FirstOrDefault(a=>a.StartsWith("--cleanup-update=",StringComparison.Ordinal));
        if(cleanup is null)return false;
        string oldFolder=cleanup["--cleanup-update=".Length..];
        if(!IsUpdateFolder(oldFolder))return false;
        string previousHelper=Path.Combine(oldFolder,"HUD-Update.exe");
        if(!File.Exists(previousHelper))return false;
        var info=FileVersionInfo.GetVersionInfo(previousHelper);
        if(!ClientLifetime.IsKnownProduct(info.ProductName) || !NeedsRestart(info.FileBuildPart))return false;

        string destination=AppContext.BaseDirectory;
        string helperFolder=Path.Combine(Path.GetTempPath(),"DiscordChatHUD-Update-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperFolder);
        string helper=Path.Combine(helperFolder,"HUD-Update.exe");
        File.Copy(Environment.ProcessPath!,helper);
        string readyName=AppInstance.ScopedName("LegacyRestartReady",helperFolder);
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,readyName);
        using var guard=new EventWaitHandle(false,EventResetMode.ManualReset,AppInstance.ScopedName("UpdateInProgress",destination));
        guard.Set();
        try
        {
            var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=helperFolder};
            foreach(var arg in new[]{"--finish-legacy-update",destination,Environment.ProcessId.ToString(),oldFolder})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new IOException("전환 도우미를 시작하지 못했습니다.");
            if(!ready.WaitOne(10000))throw new IOException("전환 도우미가 응답하지 않습니다. 클라이언트를 다시 실행해주세요.");
            Logging.AppLog.Info($"구버전 업데이트 후 전체 재시작 도우미 인계 · 이전 v{info.FileBuildPart}");
            return true; // Exit this startup before login/HUD. Helper now owns the guard.
        }
        catch { guard.Reset(); throw; }
    }

    internal static void Finish(string destination,int parent,string oldFolder)
    {
        using var guard=new EventWaitHandle(false,EventResetMode.ManualReset,AppInstance.ScopedName("UpdateInProgress",destination));
        guard.Set();
        try
        {
            using(var ready=EventWaitHandle.OpenExisting(AppInstance.ScopedName("LegacyRestartReady",AppContext.BaseDirectory)))ready.Set();
            try{using var caller=Process.GetProcessById(parent);if(!caller.WaitForExit(30000))throw new IOException("업데이트 후 시작 프로세스가 종료되지 않았습니다.");}catch(ArgumentException){}
            // Wait until the old installer has finished spawning its restart processes.
            using var current=Process.GetCurrentProcess();
            foreach(var candidate in Process.GetProcessesByName("HUD-Update"))using(candidate)
            {
                if(candidate.Id==current.Id || candidate.SessionId!=current.SessionId)continue;
                string? path;try{path=candidate.MainModule?.FileName;}catch(InvalidOperationException){continue;}
                if(path is not null && ClientLifetime.SameDirectory(Path.GetDirectoryName(path)!,oldFolder) && !candidate.WaitForExit(30000))
                    throw new IOException("기존 업데이트 도우미가 아직 실행 중입니다.");
            }
            // Legacy installer finally blocks may reset this shared event as they exit.
            guard.Set();
            GameAutoLaunch.StopWatcherForUpdate(destination);
            ClientLifetime.CloseOtherInstallations(destination,includeDestination:true);
            ClientLifetime.WaitUntilAllClosed(destination);
            ClientLifetime.AssertAllClosed();
            if(IsUpdateFolder(oldFolder))AppUpdate.Clean(oldFolder);
            guard.Reset();
            var config=new ProcessStartInfo(Path.Combine(destination,"DiscordChatHUD_Config.exe")){UseShellExecute=false,WorkingDirectory=destination};
            // This helper is v255, so this cleanup argument cannot trigger another legacy restart.
            config.ArgumentList.Add("--cleanup-update="+AppContext.BaseDirectory);
            Process.Start(config);
            Process.Start(new ProcessStartInfo(Path.Combine(destination,"DiscordChatHUD.exe"),"--hud"){UseShellExecute=false,WorkingDirectory=destination});
            Logging.AppLog.Info("구버전 업데이트 후 전체 종료 확인 및 새 클라이언트 재실행 완료");
        }
        catch(Exception ex){MessageBox.Show("설치는 끝났지만 전체 재시작을 완료하지 못했습니다.\n"+ex.Message,"DiscordChatHUD 업데이트",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{guard.Reset();}
    }
}
