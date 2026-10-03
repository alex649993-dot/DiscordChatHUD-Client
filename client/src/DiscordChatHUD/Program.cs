using DiscordChatHUD.Models;
using System.Globalization;
using System.Diagnostics;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;
using DiscordChatHUD.Windows;

namespace DiscordChatHUD;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Packaging check only: no UI, login, settings mutation, or existing-process signals.
        if (args.Length == 1 && args[0] == "--verify-relay-distribution")
        {
            try { Environment.ExitCode = RelayClientSettings.IsRelayBuild && RelayClientSettings.Enabled
                    && RelayClientSettings.Server().Scheme == "https" ? 0 : 2; }
            catch { Environment.ExitCode = 3; }
            return;
        }
        if(args.Length==5 && args[0]=="--repair-client" && int.TryParse(args[4],out var repairCooldown))
        { Environment.ExitCode=RecoveryCoordinator.RunRepair(args[1],args[2],args[3],repairCooldown);return; }
        if(args.Length==2 && args[0]=="--verify-client")
        { Environment.ExitCode=RecoveryCoordinator.Verify(args[1]);return; }
        if(args.Length==3 && args[0]=="--recover-update" && int.TryParse(args[2],out var recoveryParent))
        { AppUpdate.RunInstaller(args[1],recoveryParent,recovery:true);return; }
        if (args.Length == 3 && args[0] == "--apply-update" && int.TryParse(args[2], out var updateParent))
        { AppUpdate.RunInstaller(args[1], updateParent); return; }
        if(args.Length==4 && args[0]=="--finish-legacy-update" && int.TryParse(args[2],out var legacyParent))
        { LegacyUpdateRestart.Finish(args[1],legacyParent,args[3]); return; }
        foreach (var argument in args.Where(value => value.StartsWith("--cleanup-update=", StringComparison.Ordinal)))
            AppUpdate.CleanExitedHelper(argument["--cleanup-update=".Length..]);
        if (GameAutoLaunch.IsUpdating(AppContext.BaseDirectory)) return;
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if(processName.Equals(Path.GetFileNameWithoutExtension(GameAutoLaunch.WatcherFile),StringComparison.OrdinalIgnoreCase)
           && args.Contains("--watch-gta"))
        {
            var watcherRepair=RecoveryCoordinator.TryWatcherStartupRepair();
            if(watcherRepair is not null){Environment.ExitCode=watcherRepair.Value;return;}
        }
        ApprovedLaunch.Remember();
        // Taskbar buttons group by executable path; reopen under the pinned name.
        if (TaskbarIdentity.TryHandOffToPinned(args)) return;
        // Configure before WinForms/HTTP/ImageSharp first use ArrayPool.Shared.
        // The user's heap retained >150 one-MiB buffers across pool partitions
        // after every animated cache entry had already been evicted.
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXPARTITIONCOUNT", "1");
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_BUFFERS_SHAREDARRAYPOOL_MAXARRAYSPERPARTITION", "2");
        if (args.Contains("--verify-buffer-pool"))
        {
            VerifyBufferPool();
            return;
        }
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ko-KR");
        }
        catch { }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, eventArgs) => AppLog.Error("UI 스레드 예외", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            AppLog.Error("처리되지 않은 예외", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            AppLog.Error("비동기 작업 예외", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        var arguments = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        var name = processName;
        var configIdentity = name.Contains("Config", StringComparison.OrdinalIgnoreCase)
            || arguments.Contains(TaskbarIdentity.ConfigIdentityArgument);
        var command = arguments.Contains("--reset-auto") ? DesktopCommand.Reset
            : arguments.Contains("--watch-gta") ? DesktopCommand.Watch
            : arguments.Contains("--preview") ? DesktopCommand.Preview
            : arguments.Contains("--auto-config") ? DesktopCommand.AutoConfig
            : arguments.Contains("--auto-hud") ? DesktopCommand.AutoHud
            : arguments.Contains("--hud") ? DesktopCommand.Hud
            : arguments.Contains("--config") || configIdentity
                ? DesktopCommand.Config : DesktopCommand.Hud;
        try
        {
            using var instance = new DesktopInstance();
            if (!instance.IsOwner) { instance.Send(command); return; }
            AppLog.Info($"시작 · Preview {AppUpdate.CurrentVersion} · {Path.GetFileName(Environment.ProcessPath)}"
                + (args.Length > 0 ? " · " + string.Join(' ', args) : ""));
            if(configIdentity)
                StartupHealth.Begin(AppContext.BaseDirectory,AppUpdate.CurrentVersion,DateTimeOffset.UtcNow);
            if (command is DesktopCommand.Config or DesktopCommand.Hud)
                ClientLifetime.CloseOtherInstallations(AppContext.BaseDirectory);
            GameAutoLaunch.AdoptIntegratedWatcher();
            PreferGamePerformance();
            using var context = new DesktopContext(instance, new DesktopSession());
            context.Queue(command);
            if(configIdentity)
                StartupHealth.ScheduleHealthy(AppContext.BaseDirectory,AppUpdate.CurrentVersion);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            AppLog.Error("프로그램 시작 실패", ex);
            MessageBox.Show($"DiscordChatHUD 실행 실패\n\n{ex.Message}", "DiscordChatHUD", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private static void VerifyBufferPool()
    {
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var buffers = new byte[160][];
        for (var i = 0; i < buffers.Length; i++) buffers[i] = pool.Rent(1024 * 1024);
        foreach (var buffer in buffers) pool.Return(buffer);
        Array.Clear(buffers);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var partitions = (Array)pool.GetType().GetField("_buckets", flags)!.GetValue(pool)!;
        var retained = 0;
        foreach (var bucket in partitions)
        {
            if (bucket is null) continue;
            var shards = (Array)bucket.GetType().GetField("_partitions", flags)!.GetValue(bucket)!;
            foreach (var shard in shards)
                retained += (int)shard!.GetType().GetField("_count", flags)!.GetValue(shard)!;
        }
        if (retained > 2) throw new InvalidOperationException("Shared buffer retention limit was not applied.");
        // Test-only entry point: no settings, credentials, UI, or Discord.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "buffer-pool-check.txt"),
            $"PASS: returned 160 x 1 MiB buffers; shared partitions retain {retained} buffers (+ one thread-local buffer). No forced GC.");
    }

    private static void PreferGamePerformance()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            // Do not pin the HUD to a guessed core. Windows can move this
            // below-normal process to whichever logical processor is idle,
            // while normal-priority GTA work wins when both need CPU time.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"HUD 프로세스 우선순위 조정 실패: {ex.Message}");
        }
    }
}
