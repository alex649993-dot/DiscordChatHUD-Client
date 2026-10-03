using DiscordChatHUD.Windows;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

internal enum DesktopCommand { Config, Hud, Preview, AutoConfig, AutoHud, Watch, Reset }

// Named requests are created before taking ownership, so a second launch can
// queue activation even while the first process is still displaying login.
internal sealed class DesktopInstance : IDisposable
{
    readonly Mutex owner;
    readonly Dictionary<DesktopCommand, EventWaitHandle> requests = new();
    internal bool IsOwner { get; }
    internal DesktopInstance(string? scope = null)
    {
        scope ??= AppContext.BaseDirectory;
        foreach (var command in Enum.GetValues<DesktopCommand>())
            requests[command] = new(false, EventResetMode.AutoReset, AppInstance.ScopedName("Desktop." + command, scope));
        owner = new(false, AppInstance.ScopedName("Desktop.Owner", scope));
        try { IsOwner = owner.WaitOne(0); } catch (AbandonedMutexException) { IsOwner = true; }
    }
    internal void Send(DesktopCommand command) => requests[command].Set();
    internal DesktopCommand? Take()
    {
        foreach (var pair in requests) if (pair.Value.WaitOne(0)) return pair.Key;
        return null;
    }
    public void Dispose()
    {
        if (IsOwner) owner.ReleaseMutex();
        owner.Dispose();
        foreach (var request in requests.Values) request.Dispose();
    }
}

internal interface IDesktopSession : IDisposable
{
    bool Login();
    bool RequiresLogin => false;
    Form CreateConfig();
    Form? CreateHud(bool preview);
    Task ReleaseHudAsync(Form hud);
    void PrepareConfig();
    Task FlushConfigAsync(Form config);
}

internal sealed class DesktopContext : ApplicationContext
{
    internal static DesktopContext? Current { get; private set; }
    readonly DesktopInstance instance;
    readonly IDesktopSession session;
    readonly Func<bool> watchEnabled;
    readonly Func<bool> gameRunning;
    readonly bool production;
    readonly Control dispatch = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    readonly GameTransition transition = new();
    readonly AutoHudPolicy policy = new();
    readonly EventWaitHandle? pause, stopWatch;
    NotifyIcon? tray;
    Form? config, hud;
    Mutex? configLease, hudLease;
    Task hudCleanup = Task.CompletedTask;
    bool busy, exiting, watching, configRequested, startingHud, automaticRequest;
    long nextGamePoll;
    internal bool HudRunning => hud is not null || !hudCleanup.IsCompleted;
    internal bool ConfigRunning => config is not null;
    internal bool ConfigOwnsClock => config is ConfigForm form && form.OwnsBusinessTracker;
    internal bool Exiting => exiting;

    internal DesktopContext(DesktopInstance instance, IDesktopSession session,
        Func<bool>? watchEnabled = null, Func<bool>? gameRunning = null, bool production = true)
    {
        this.instance = instance; this.session = session; this.production = production;
        this.watchEnabled = watchEnabled ?? (() => GameAutoLaunch.Enabled);
        this.gameRunning = gameRunning ?? GameAutoLaunch.IsGameRunning;
        if (production)
        {
            if (Current is not null) throw new InvalidOperationException("Desktop already exists.");
            Current = this;
            pause = new(false, EventResetMode.ManualReset, AppInstance.ScopedName("AutoManualPause", AppContext.BaseDirectory));
            stopWatch = new(false, EventResetMode.AutoReset, AppInstance.ScopedName("AutoWatchStop", AppContext.BaseDirectory));
            var menu = new ContextMenuStrip();
            menu.Items.Add("설정 열기", null, (_, _) => instance.Send(DesktopCommand.Config));
            menu.Items.Add("HUD 실행", null, (_, _) => instance.Send(DesktopCommand.Hud));
            menu.Items.Add("HUD 종료", null, async (_, _) => { GameAutoLaunch.PauseForSession(); await StopHudAsync(); CheckIdle(); });
            menu.Items.Add("모두 종료 (이번 실행)", null, async (_, _) => await ExitAllAsync());
            tray = new() { Icon = SystemIcons.Application, Text = "DiscordChatHUD", ContextMenuStrip = menu };
            tray.DoubleClick += (_, _) => instance.Send(DesktopCommand.Config);
        }
        _ = dispatch.Handle;
        watching = this.watchEnabled();
        timer.Tick += async (_, _) => await PollAsync();
        timer.Start();
    }
    internal void Queue(DesktopCommand command) => instance.Send(command);
    internal void RefreshWatch() { watching = watchEnabled(); nextGamePoll = 0; }
    internal async Task HandleAsync(DesktopCommand command)
    {
        if (exiting) return;
        if (busy) { instance.Send(command); return; }
        busy = true;
        try
        {
            if (command is DesktopCommand.Watch or DesktopCommand.Reset)
            {
                RefreshWatch();
                if (command == DesktopCommand.Reset) { pause?.Reset(); configRequested = false; }
                return;
            }
            bool automatic = command is DesktopCommand.AutoConfig or DesktopCommand.AutoHud;
            automaticRequest = automatic;
            if (automatic && (!watchEnabled() || !gameRunning() || Paused)) return;
            bool renewSession = session.RequiresLogin;
            if (!session.Login()) { if (automatic || watching) pause?.Set(); return; }
            if (exiting || (automatic && (!gameRunning() || Paused))) return;
            if (command is DesktopCommand.Config or DesktopCommand.AutoConfig)
                ShowConfig(automatic);
            await StartHudAsync(command == DesktopCommand.Preview, keepExisting: !renewSession && command != DesktopCommand.Preview, allowConfigFallback: !automatic);
        }
        catch (Exception ex) { AppLog.Error("통합 클라이언트 실행 실패", ex); if (production && !exiting) MessageBox.Show(ex.Message, "DiscordChatHUD 실행 실패"); }
        finally { busy = false; automaticRequest = false; CheckIdle(); }
    }
    bool Paused => pause?.WaitOne(0) ?? false;
    void ShowConfig(bool automatic)
    {
        if (config is not null)
        {
            if (!automatic) { config.Show(); config.WindowState = FormWindowState.Normal; config.Activate(); }
            return;
        }
        session.PrepareConfig();
        configLease = TakeLease(AppInstance.ConfigWindowMutexName);
        try { config = session.CreateConfig(); }
        catch { ReleaseLease(ref configLease); throw; }
        var form = config;
        form.FormClosed += (_, _) =>
        {
            config = null;
            ReleaseLease(ref configLease);
            CheckIdle();
        };
        if (automatic) form.WindowState = FormWindowState.Minimized;
        form.Show();
    }
    internal async Task StartHudAsync(bool preview = false, bool keepExisting = false, bool allowConfigFallback = true)
    {
        if (exiting || (production && GameAutoLaunch.IsUpdating(AppContext.BaseDirectory))) return;
        if (startingHud || (keepExisting && HudRunning)) return;
        startingHud = true;
        // Hold the lifetime open while the old window/resources are disposed.
        bool previousBusy = busy; busy = true;
        try
        {
            await StopHudAsync();
            if (exiting) return;
            hudLease = TakeLease(AppInstance.HudMutexName);
            try { hud = session.CreateHud(preview); }
            catch { ReleaseLease(ref hudLease); throw; }
            if (hud is null) { ReleaseLease(ref hudLease); if (allowConfigFallback) ShowConfig(false); return; }
            var form = hud;
            form.FormClosed += (_, _) =>
            {
                hud = null;
                ReleaseLease(ref hudLease);
                hudCleanup = session.ReleaseHudAsync(form);
                _ = AfterHudClosedAsync();
            };
            form.Show();
        }
        finally { startingHud = false; busy = previousBusy; UpdateTray(); }
    }
    async Task AfterHudClosedAsync()
    {
        try { await hudCleanup; } catch (Exception ex) { AppLog.Error("HUD 자원 정리 실패", ex); }
        CheckIdle();
    }
    internal async Task StopHudAsync()
    {
        if (hud is OverlayForm overlay) overlay.CloseForHost();
        else hud?.Close();
        await hudCleanup;
    }
    internal async Task ExitAllAsync()
    {
        if (exiting) return;
        exiting = true; timer.Stop(); watching = false;
        try
        {
            // Login uses a nested message loop; it must also close on update/exit.
            if (production)
                foreach (var login in Application.OpenForms.OfType<RelayLoginForm>().ToArray()) login.Close();
            if (config is not null) { await session.FlushConfigAsync(config); config.Close(); if (config is not null) throw new IOException("설정 저장을 완료하지 못해 종료가 취소되었습니다."); }
            await StopHudAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("통합 클라이언트 종료 실패", ex);
            exiting = false; watching = watchEnabled(); timer.Start();
            return;
        }
        ExitThread();
    }
    internal async Task PollAsync()
    {
        if (exiting) return;
        if (stopWatch?.WaitOne(0) == true) { await ExitAllAsync(); return; }
        if (busy)
        {
            if (production && automaticRequest && !gameRunning())
                foreach (var login in Application.OpenForms.OfType<RelayLoginForm>().ToArray()) login.Close();
            return;
        }
        var request = instance.Take();
        if (request is not null) { await HandleAsync(request.Value); return; }
        if (Environment.TickCount64 < nextGamePoll) return;
        nextGamePoll = Environment.TickCount64 + 1500;
        RefreshWatchState();
        if (!watching) { CheckIdle(); return; }
        if (production && GameAutoLaunch.IsUpdating(AppContext.BaseDirectory)) return;
        bool game = gameRunning();
        int edge = transition.Observe(game);
        if (edge == -1)
        {
            busy = true;
            try
            {
                if (config is not null) { await session.FlushConfigAsync(config); config.Close(); if (config is not null) throw new IOException("설정 저장을 완료하지 못해 종료가 취소되었습니다."); }
                await StopHudAsync(); configRequested = false;
            }
            finally { busy = false; }
        }
        if (edge == 1) { pause?.Reset(); configRequested = false; }
        if (game && !Paused && !configRequested)
        {
            configRequested = true;
            await HandleAsync(DesktopCommand.AutoConfig);
        }
        if (policy.Observe(game, HudRunning, Paused, Environment.TickCount64) == AutoHudAction.Start)
            await HandleAsync(DesktopCommand.AutoHud);
        CheckIdle();
    }
    void RefreshWatchState() { watching = watchEnabled(); UpdateTray(); }
    void UpdateTray() { if (tray is not null) tray.Visible = !exiting && hud is null; }
    void CheckIdle()
    {
        UpdateTray();
        if (!exiting && !busy && !watching && config is null && !HudRunning) ExitThread();
    }
    Mutex? TakeLease(string name)
    {
        if (!production) return null;
        var lease = new Mutex(false, name);
        bool owned;
        try { owned = lease.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) { lease.Dispose(); throw new IOException("기존 클라이언트가 아직 실행 중입니다."); }
        return lease;
    }
    static void ReleaseLease(ref Mutex? lease) { lease?.ReleaseMutex(); lease?.Dispose(); lease = null; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose(); dispatch.Dispose();
            if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
            pause?.Dispose(); stopWatch?.Dispose();
            ReleaseLease(ref configLease); ReleaseLease(ref hudLease);
            session.Dispose();
            if (Current == this) Current = null;
        }
        base.Dispose(disposing);
    }
}