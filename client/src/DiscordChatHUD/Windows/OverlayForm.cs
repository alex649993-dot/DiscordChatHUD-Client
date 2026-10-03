using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

internal sealed partial class OverlayForm : Form
{
    private const int MinimumAnimationFrameIntervalMilliseconds = 33;
    private readonly HudConfig _config;
    private readonly IChatSource _discord;
    private readonly MediaCache _media;
    private readonly HudRenderer _renderer;
    private readonly GameWindowTracker _game = new();
    private readonly ResourceDiagnostics _diagnostics = new();
    // 20 Hz is sufficient for hotkeys and drag state while avoiding needless
    // UI-thread wake-ups when the overlay is otherwise idle.
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    // Runs only while an animated image is actually visible. Keeping animation
    // timing separate preserves 30fps GIFs without forcing all idle polling to 30Hz.
    private readonly AnimationDeadlineTimer _animationTimer;
    private readonly System.Windows.Forms.Timer _renderBatchTimer = new() { Interval = 16 };
    private readonly EventWaitHandle _gameExitSignal = new(false, EventResetMode.AutoReset, GameAutoLaunch.HudStopSignal);
    private readonly RegisteredWaitHandle _gameExitWait;
    private readonly EventWaitHandle _positionEditSignal = new(false, EventResetMode.AutoReset, AppInstance.PositionEditEventName);
    private readonly RegisteredWaitHandle _positionEditWait;
    private volatile bool _positionEditRequested;
    private HudPositionEditor? _positionEditor;
    private Rectangle _positionBeforeEdit;
    private bool _positionWasVisible;
    private readonly ChannelHotkeyRegistration _channelHotkeyRegistration = new();
    private ParsedHotkey _channelPickerHotkey = new(false,false,true,Keys.T);
    private bool _channelPickerWasDown, _channelSwitchBusy;
    private ChannelPickerForm? _channelPicker;
    private IntPtr _pickerGameWindow;
    private bool _automaticStop;
    private readonly ShutdownSignal _shutdownSignal;
    private readonly RegisteredWaitHandle _shutdownWait;
    private readonly NotifyIcon _tray;
    private readonly bool _preview;
    private int _renderVersion;
    private bool _fullRenderRequired = true;
    private bool _renderLoopActive;
    private Task<RenderedHud>? _renderWorker;
    private Func<RenderedHud>? _fullSceneRender;
    private Func<RenderedHud>? _animatedSceneRender;
    private Size _renderSceneSize;
    private bool _closing;
    private bool _actuallyVisible;
    private IntPtr _lastForegroundForZOrder;
    private DateTimeOffset _lastZOrderRefresh = DateTimeOffset.MinValue;
    private bool _hudManuallyHidden;
    private bool _inputEnabled;
    private bool _geometryDirty;
    private bool _sizeMoveActive;
    private bool _captureGuardActive;
    private bool _vinewoodTimerPopupVisible;
    private bool _captureGuardWasVisible;
    // Capture temporarily conceals the existing surface, not the scene/cache.
    private bool PreserveCapturedScene => _captureGuardActive && _captureGuardWasVisible;
    private bool _captureSelectionSeen;
    private bool _captureSelectionSurfaceActive;
    private bool _captureAwaitingSelectionInput;
    private bool _captureSelectionMouseDown;
    private bool _captureChordWasDown;
    private bool _printScreenWasDown;
    private bool _captureEscapeWasDown;
    private bool _captureEnterWasDown;
    private DateTimeOffset _captureGuardReleaseAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _captureGuardSafetyDeadline = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCaptureSurfaceProbe = DateTimeOffset.MinValue;
    private uint _captureForegroundProcessId;
    private bool _captureForegroundIsHost;
    private long _captureProcessRefreshTick;
    private IntPtr _captureKeyboardHook;
    private NativeMethods.LowLevelKeyboardProc? _captureKeyboardHookProc;
    private bool _leftWindowsKeyDown;
    private bool _rightWindowsKeyDown;
    private bool _leftShiftKeyDown;
    private bool _rightShiftKeyDown;
    private bool _genericShiftKeyDown;
    private int _maxScrollPixels;
    private int _mainTintBottom;
    private int _mainTintTop;
    private int _scrollPixels;
    private int _liveMessageExtraHeight;
    private DateTimeOffset _manualScrollPinnedUntil = DateTimeOffset.MinValue;
    private ulong _manualScrollBaselineMessageId;
    private ChatMessage? _manualScrollLiveMessage;
    private IReadOnlyList<ChatMessage>? _manualScrollFrozenMessages;
    private DateTimeOffset _gameMissingSince = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVisibilityPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastConfigPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _lastClockMinute = DateTimeOffset.MinValue;
    private DateTimeOffset _lastTimedHudProbe = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSaleStatusRefresh = DateTimeOffset.MinValue;
    private string? _lastTimedHudSignature;
    private SaleSequenceStatus? _cachedSaleStatus;
    private bool _saleStatusDirty = true;
    private readonly SaleHudVisibility _saleVisibility;
    private int _mediaNotificationPending;
    // -1: no active animation, 0: inspect after a new render, >0: next due tick.
    private long _nextAnimatedMediaRenderTick = -1;
    private long _lastAnimatedMediaRenderRequestTick = -1;
    private readonly BusinessProgressClock _businessProgressClock = new();
    private DateTimeOffset _businessProgressLastSave = DateTimeOffset.MinValue;
    private bool _configTrackingWasActive;
    private DateTime _lastConfigWrite = DateTime.MinValue;
    private ParsedHotkey _hudToggleHotkey = new(false, true, false, Keys.T);
    private ParsedHotkey _exitHotkey = new(false, false, false, Keys.None);
    private ParsedHotkey _saleStatusHotkey = new(false, false, false, Keys.F6);
    private ParsedHotkey _vinewoodTimerPopupHotkey = new(false, true, false, Keys.V);
    private ParsedHotkey _readBusinessScreenHotkey = new(false, false, false, Keys.F7);
    private bool _readBusinessScreenWasDown;
    private readonly BusinessAlertPolicy _businessAlerts = new();
    private DateTimeOffset _businessAlertsLastCheck = DateTimeOffset.MinValue;
    // HUD 를 숨긴 뒤 바탕화면이 다시 그려지기를 기다리는 시간.
    private const int OverlayHideForCaptureMilliseconds = 90;
    private bool _hudToggleWasDown;
    private bool _exitWasDown;
    private bool _saleStatusWasDown;
    private bool _vinewoodTimerPopupWasDown;
    private bool _pageUpWasDown;
    private bool _pageDownWasDown;
    private bool _endWasDown;
    private long _pageUpNextRepeat;
    private long _pageDownNextRepeat;
    private IntPtr _surfaceDc;
    private IntPtr _surfaceBitmap;
    private IntPtr _surfaceOldBitmap;
    private IntPtr _surfaceBits;
    private Size _surfaceSize;

    public OverlayForm(
        HudConfig config,
        IChatSource discord,
        MediaCache media,
        HudRenderer renderer,
        bool preview)
    {
        _animationTimer = new AnimationDeadlineTimer(this) { Interval = 33 };
        _config = config;
        _discord = discord;
        _media = media;
        // Preview 299: GIF playback is a user setting again. Stale false values
        // from the old still-frame toggle are reset once by HudConfig.Normalize.
        _media.AnimationsEnabled = _config.AnimateGif;
        _renderer = renderer;
        _preview = preview;
        _saleVisibility = new(config.ShowSaleStatus);
        _vinewoodTimerPopupVisible = config.ShowVinewoodTimerPopup;
        _gameExitWait = ThreadPool.RegisterWaitForSingleObject(_gameExitSignal, (_, _) => SafeBeginInvoke(() => { _automaticStop = true; Close(); }), null, Timeout.Infinite, false);
        _positionEditWait = ThreadPool.RegisterWaitForSingleObject(_positionEditSignal, (_, _) => { _positionEditRequested=true; SafeBeginInvoke(() => { if(_timer.Enabled) { _positionEditRequested=false; BeginPositionEdit(); } }); }, null, Timeout.Infinite, false);
        _shutdownSignal = ShutdownSignal.CreateListener();
        _shutdownWait = ThreadPool.RegisterWaitForSingleObject(
            _shutdownSignal.WaitHandle,
            (_, _) => SafeBeginInvoke(() => { _automaticStop = true; Close(); }),
            null,
            Timeout.Infinite,
            false);

        Text = "DiscordChatHUD C#";
        Name = "DiscordChatHUD_CSharp_Overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new Size(HudLayoutPreset.MinimumWidth, HudLayoutPreset.MinimumHeight);
        MaximumSize = new Size(HudLayoutPreset.MaximumWidth, HudLayoutPreset.MaximumHeight);
        BackColor = Color.Black;
        Bounds = CalculateInitialBounds();

        ParseHotkeys();
        _discord.MessagesChanged += OnSourceChanged;
        _discord.StatusChanged += OnSourceChanged;
        _media.MediaReady += OnMediaReady;
        _timer.Tick += TimerOnTick;
        _animationTimer.Tick += AnimationTimerOnTick;
        _renderBatchTimer.Tick += (_, _) =>
        {
            _renderBatchTimer.Stop();
            if (!_closing && _actuallyVisible && !_renderLoopActive) StartRenderWorker();
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("HUD 숨기기 / 표시", null, (_, _) => ToggleHudVisibility());
        trayMenu.Items.Add("판매 HUD 표시 / 숨김", null, (_, _) => ToggleSaleStatusPresentation());
        trayMenu.Items.Add("직원 배정 HUD 표시 / 숨김", null, (_, _) => ToggleVinewoodTimerPopup());
        trayMenu.Items.Add("모두 종료 (이번 실행)", null, async (_, _) => { if (DesktopContext.Current is { } desktop) await desktop.ExitAllAsync(); else Close(); });
        trayMenu.Items.Add("설정 열기", null, (_, _) => OpenConfigurator());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("HUD.exe 끝내기", null, (_, _) => Close());
        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DiscordChatHUD C# Public Source 334",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenConfigurator();
    }

    protected override bool ShowWithoutActivation => true;

    internal static long NormalizeTaskbarStyle(long style) =>
        (style & ~NativeMethods.WsExAppWindow) | NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // FormBorderStyle.None removes this style. Keep the resize frame so
            // Windows honours every HT* resize result returned below.
            parameters.Style |= NativeMethods.WsThickFrame;
            parameters.ExStyle = unchecked((int)NormalizeTaskbarStyle(
                parameters.ExStyle | NativeMethods.WsExLayered | NativeMethods.WsExTransparent));
            return parameters;
        }
    }

    private void EnsureHiddenFromTaskbar()
    {
        if (!IsHandleCreated) return;
        var current = NativeMethods.GetWindowLongPtrW(Handle, NativeMethods.GwlExStyle).ToInt64();
        var desired = NormalizeTaskbarStyle(current);
        if (desired == current) return;
        NativeMethods.SetWindowLongPtrW(Handle, NativeMethods.GwlExStyle, new IntPtr(desired));
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopMost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureHiddenFromTaskbar();
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopMost,
            Left,
            Top,
            Width,
            Height,
            NativeMethods.SwpNoActivate);
        _actuallyVisible = true;
        if (!_preview)
        {
            HideOverlay();
            _game.Refresh(force: true);
            RepositionForGameMonitor();
        }
        _timer.Start();
        if(_positionEditRequested) { _positionEditRequested=false; BeginPositionEdit(); }
        _discord.Start();
        UpdateCaptureProtectionRegistration();
        RequestRender();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == ChannelHotkeyRegistration.Id)
        {
            if (!_closing && !_channelSwitchBusy && _channelPicker is null && _positionEditor is null
                && !_captureGuardActive && _game.IsGameAlive && _game.IsForeground())
            {
                _channelPickerWasDown = true;
                UpdateGameVisibility();
                OpenChannelPicker();
            }
            return;
        }
        switch (message.Msg)
        {
            case NativeMethods.WmNcHitTest:
                if (_inputEnabled)
                {
                    message.Result = (IntPtr)HitTest(message.LParam);
                    return;
                }
                message.Result = (IntPtr)NativeMethods.HtTransparent;
                return;
            case NativeMethods.WmEnterSizeMove:
                _sizeMoveActive = true;
                break;
            case NativeMethods.WmMoving:
            case NativeMethods.WmSizing:
                _geometryDirty = true;
                break;
            case NativeMethods.WmExitSizeMove:
                _sizeMoveActive = false;
                _geometryDirty = true;
                SaveGeometry();
                RequestRender();
                break;
            case NativeMethods.WmQueryEndSession:
                message.Result = (IntPtr)1;
                return;
            case NativeMethods.WmEndSession:
                if (message.WParam != IntPtr.Zero) BeginInvoke(Close);
                break;
        }
        base.WndProc(ref message);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _channelHotkeyRegistration.Dispose();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_sizeMoveActive || _positionEditor is not null)
        {
            _geometryDirty = true;
            RequestRender();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_automaticStop && !_preview && e.CloseReason == CloseReason.UserClosing) GameAutoLaunch.PauseForSession();
        EndPositionEdit(false);
        CloseChannelPicker(null, false);
        _closing = true;
        _channelHotkeyRegistration.Dispose();
        _nightclubReadStop.Cancel();
        _businessOcrWarmup.Dispose();
        _timer.Stop();
        _animationTimer.Stop();
        _timer.Dispose();
        _animationTimer.Dispose();
        _renderBatchTimer.Stop();
        _renderBatchTimer.Dispose();
        _fullSceneRender = null;
        _animatedSceneRender = null;
        if (_geometryDirty) SaveGeometry();
        if (!IsConfigBusinessTrackerActive()) SaveBusinessProgress();
        _discord.MessagesChanged -= OnSourceChanged;
        _discord.StatusChanged -= OnSourceChanged;
        _media.MediaReady -= OnMediaReady;
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        RemoveCaptureKeyboardHook();
        _gameExitWait.Unregister(null);
        _gameExitSignal.Dispose();
        _positionEditWait.Unregister(null);
        _positionEditSignal.Dispose();
        _shutdownWait.Unregister(null);
        _shutdownSignal.Dispose();
        DestroyLayeredSurface();
        _game.Dispose();
        base.OnFormClosing(e);
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        if (_closing) return;
        _diagnostics.Poll(_media, _actuallyVisible || PreserveCapturedScene, _renderLoopActive);
        if (_positionEditor is not null && NativeMethods.IsKeyDown(Keys.Escape)) EndPositionEdit(false);
        PollCaptureProtection();
        PollControlKeys();
        var shouldEnableInput = _channelPicker is null && _positionEditor is null && _game.IsGameAlive && _game.IsForeground() && NativeMethods.IsKeyDown(Keys.ControlKey) && NativeMethods.IsCursorVisible();
        if (shouldEnableInput != _inputEnabled) SetInputEnabled(shouldEnableInput);

        var now = DateTimeOffset.UtcNow;
        if (_scrollPixels > 0
            && _manualScrollPinnedUntil != DateTimeOffset.MinValue
            && now >= _manualScrollPinnedUntil)
        {
            SetScroll(0);
        }
        if (now - _lastVisibilityPoll >= TimeSpan.FromMilliseconds(250))
        {
            _lastVisibilityPoll = now;
            UpdateGameVisibility();
            _businessOcrWarmup.TryStart(!_preview && !_nightclubReadBusy && _game.IsGameAlive);
        }
        if (now - _lastConfigPoll >= TimeSpan.FromMilliseconds(250))
        {
            _lastConfigPoll = now;
            RefreshHotkeysIfChanged();
        }
        RefreshTimedHud(now);
        // Fallback for the separate animation timer. This costs only one
        // cache scan on the existing 20Hz HUD tick and keeps GIF/WebP frames
        // moving after foreground/hide transitions where WinForms can defer a
        // secondary timer notification.
        if (_actuallyVisible && !_renderLoopActive && !_animationTimer.Enabled && _media.IsAnimationFrameDue(1))
            RequestAnimationRender();
    }

    private void AnimationTimerOnTick(object? sender, EventArgs e)
    {
        if (_closing || !_actuallyVisible)
        {
            StopAnimationTimer();
            return;
        }

        var nowTick = AnimationClock.NowMilliseconds;
        if (_nextAnimatedMediaRenderTick > nowTick)
        {
            _animationTimer.Interval = (int)Math.Clamp(
                _nextAnimatedMediaRenderTick - nowTick,
                1L,
                10_000L);
            return;
        }
        // Stop until the completed render confirms that an animated frame is
        // still on screen. This also handles long-delay GIF frames without
        // repeatedly scanning the cache between frame changes.
        _animationTimer.Stop();
        _nextAnimatedMediaRenderTick = -1;
        RequestAnimationRender();
    }

    private void RequestAnimationRender()
    {
        // A running render already samples the current absolute animation time.
        // Do not invalidate it with another timer tick: slow frames must still
        // reach UpdateLayeredWindow, otherwise the display can starve forever.
        var now = AnimationClock.NowMilliseconds;
        if (!RenderPresentationPolicy.CanRequestAnimation(_closing, _actuallyVisible,
                _renderLoopActive, now, _lastAnimatedMediaRenderRequestTick,
                MinimumAnimationFrameIntervalMilliseconds))
            return;
        _lastAnimatedMediaRenderRequestTick = now;
        RequestRender(animationOnly: true);
    }

    private void RefreshAnimationTimer()
    {
        if (_closing || !_actuallyVisible)
        {
            StopAnimationTimer();
            return;
        }

        // Frame selection follows the source's absolute time axis. Schedule the
        // next paint for the later of the real source boundary and our 30 fps
        // presentation ceiling, so slow frames do not burn CPU while fast GIFs
        // still catch up without stretching their playback duration.
        var sourceFrameDelay = _media.GetAnimationRefreshDelayMilliseconds();
        if (sourceFrameDelay < 0)
        {
            StopAnimationTimer();
            return;
        }

        var nowTick = AnimationClock.NowMilliseconds;
        var frameCapDelay = _lastAnimatedMediaRenderRequestTick < 0
            ? 0L
            : Math.Max(
                0L,
                _lastAnimatedMediaRenderRequestTick
                + MinimumAnimationFrameIntervalMilliseconds
                - nowTick);
        // The 30fps ceiling is measured from the previous render request, not
        // from render completion. This avoids adding render time + 33ms and
        // slowing large GIFs while still preventing more than ~30 updates/s.
        var delay = Math.Max(
            1,
            (int)Math.Min(Math.Max(frameCapDelay, sourceFrameDelay), int.MaxValue));
        _nextAnimatedMediaRenderTick = nowTick + delay;
        _animationTimer.Interval = Math.Clamp(delay, 1, 10_000);
        if (!_animationTimer.Enabled) _animationTimer.Start();
    }

    private void StopAnimationTimer()
    {
        if (_animationTimer.Enabled) _animationTimer.Stop();
        _nextAnimatedMediaRenderTick = -1;
        _lastAnimatedMediaRenderRequestTick = -1;
    }

    private void UpdateGameVisibility()
    {
        if (_channelPicker is not null || _positionEditor is not null) { _game.Refresh(); AdvanceBusinessProgress(!_preview && _game.IsGameAlive); return; }
        if (_preview)
        {
            if (!_hudManuallyHidden) ShowOverlay();
            AdvanceBusinessProgress(false);
            return;
        }

        var found = _game.Refresh();
        var gameRunning = _game.IsGameAlive;
        var gameForeground = found && _game.IsForeground();
        var shouldShow = HudVisibilityPolicy.AllowsDisplay(_config.AlwaysVisible, _preview, gameRunning, gameForeground)
            && !_hudManuallyHidden;
        if (shouldShow)
        {
            if (!_actuallyVisible) ShowOverlay();
            else
            {
                // A desktop app can cover a still-visible layered window without
                // taking GTA's foreground. Refresh the topmost order sparingly.
                var foreground = NativeMethods.GetForegroundWindow();
                var now = DateTimeOffset.UtcNow;
                if (foreground != _lastForegroundForZOrder
                    || now - _lastZOrderRefresh >= TimeSpan.FromSeconds(2))
                {
                    _lastForegroundForZOrder = foreground;
                    _lastZOrderRefresh = now;
                    var zStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    NativeMethods.SetWindowPos(Handle, NativeMethods.HwndTopMost, 0, 0, 0, 0,
                        NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
                    var zMs=System.Diagnostics.Stopwatch.GetElapsedTime(zStarted).TotalMilliseconds;
                    if(zMs>=50) ChatTiming.Write("zorder-slow",FormattableString.Invariant($"elapsedMs={zMs:F1}"));
                }
            }
        }
        else HideOverlay();
        // GTA Online businesses keep producing while the game is running even
        // when its window is minimized or another application is foreground.
        AdvanceBusinessProgress(gameRunning);

        if (!_config.BusinessAutoOnline && HudVisibilityPolicy.ShouldCloseAfterGameExit(_config.AlwaysVisible, _preview, _game.SeenOnce, gameRunning))
        {
            if (_gameMissingSince == DateTimeOffset.MinValue) _gameMissingSince = DateTimeOffset.UtcNow;
            if (DateTimeOffset.UtcNow - _gameMissingSince >= TimeSpan.FromSeconds(1)) Close();
        }
        else
        {
            _gameMissingSince = DateTimeOffset.MinValue;
        }
    }

    private void ShowOverlay(bool reuseSurface = false)
    {
        if (_captureGuardActive) return;
        if (_actuallyVisible) return;
        RepositionForGameMonitor();
        NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate);
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopMost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        _actuallyVisible = true;
        if (reuseSurface && !_fullRenderRequired && _surfaceBitmap != IntPtr.Zero
            && _surfaceSize == new Size(Width, Height))
            RefreshAnimationTimer();
        else
            RequestRender();
    }

    private void HideOverlay()
    {
        if (!_actuallyVisible) return;
        NativeMethods.ShowWindow(Handle, NativeMethods.SwHide);
        _actuallyVisible = false;
        StopAnimationTimer();
        if (_geometryDirty) SaveGeometry();
    }

    internal void CloseForHost() { _automaticStop = true; Close(); }
    internal void FlushForConfig()
    {
        if (!SaveBusinessProgress()) throw new IOException("사업장 상태 저장을 완료하지 못했습니다.");
        _configTrackingWasActive = true;
    }
    private void OpenConfigurator()
    {
        try
        {
            SaveBusinessProgress();
            DesktopContext.Current?.Queue(DesktopCommand.Config);
        }
        catch (Exception ex)
        {
            AppLog.Error("설정기 실행 실패", ex);
        }
    }

    private static bool IsConfigBusinessTrackerActive()
    {
        if (DesktopContext.Current is { } desktop) return desktop.ConfigOwnsClock;
        try
        {
            using var mutex = Mutex.OpenExisting(AppInstance.ConfigBusinessTrackerMutexName);
            try
            {
                if (!mutex.WaitOne(0)) return true;
                mutex.ReleaseMutex();
                return false;
            }
            catch (AbandonedMutexException)
            {
                try { mutex.ReleaseMutex(); } catch { }
                return false;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private Icon LoadIcon()
    {
        try { return new Icon(AppPaths.IconPath); }
        catch { return (Icon)SystemIcons.Application.Clone(); }
    }

    private bool SafeBeginInvoke(Action action)
    {
        if (_closing || IsDisposed || !IsHandleCreated) return false;
        try
        {
            BeginInvoke(action);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum SaleStatusPresentation
    {
        Expanded,
        Hidden
    }
}




