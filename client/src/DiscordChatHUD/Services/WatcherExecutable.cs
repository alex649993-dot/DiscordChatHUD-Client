using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DiscordChatHUD.Services;

internal static class WatcherExecutable
{
    // Embedded so pre-256 installers keep accepting the original signed ZIP file list.
    internal static void Ensure()
    {
        if (string.Equals(Path.GetFileName(Environment.ProcessPath),GameAutoLaunch.WatcherFile,StringComparison.OrdinalIgnoreCase)) return;
        using var packed=typeof(WatcherExecutable).Assembly.GetManifestResourceStream("DiscordChatHUD.Watcher.exe.gz")
            ?? throw new IOException("자동 감지 실행파일이 포함되지 않은 빌드입니다.");
        using var gate=new Mutex(false,AppInstance.ScopedName("WatcherExtract",AppContext.BaseDirectory));
        bool held;try{held=gate.WaitOne(30_000);}catch(AbandonedMutexException){held=true;}
        if(!held)throw new IOException("자동 감지 파일 준비가 진행 중입니다.");
        var target=Path.Combine(AppContext.BaseDirectory,GameAutoLaunch.WatcherFile);
        var pending=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            // Compare the compressed resource digest to a sidecar before doing any decompression.
            string digest=Convert.ToHexString(SHA256.HashData(packed));
            var stamp=target+".sha256";
            if(File.Exists(target) && File.Exists(stamp) && File.ReadAllText(stamp)==digest
                && FileVersionInfo.GetVersionInfo(target).FileVersion==typeof(WatcherExecutable).Assembly.GetName().Version?.ToString()) return;
            packed.Position=0;
            using(var gzip=new GZipStream(packed,CompressionMode.Decompress))
            using(var output=File.Create(pending))gzip.CopyTo(output);
            using var current=Process.GetCurrentProcess();
            foreach(var process in Process.GetProcessesByName("DiscordChatHUD_Watcher"))
            using(process)
            {
                if(process.SessionId!=current.SessionId)continue;
                if(!string.Equals(process.MainModule?.FileName,target,StringComparison.OrdinalIgnoreCase))continue;
                GameAutoLaunch.StopWatcherForUpdate(AppContext.BaseDirectory);
                if(!process.WaitForExit(30_000))throw new IOException("자동 감지 프로세스가 아직 종료 중입니다.");
            }
            File.Move(pending,target,true);
            File.WriteAllText(stamp,digest);
        }
        finally { if(File.Exists(pending))File.Delete(pending);gate.ReleaseMutex(); }
    }
}
