using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

internal sealed partial class ConfigForm : Form, IMessageFilter
{
    private static readonly Color WindowBackground = Color.FromArgb(17, 20, 27);
    private static readonly Color SidebarBackground = Color.FromArgb(13, 16, 22);
    private static readonly Color PanelBackground = Color.FromArgb(25, 29, 38);
    private static readonly Color FieldBackground = Color.FromArgb(33, 38, 49);
    private static readonly Color Foreground = Color.FromArgb(235, 239, 246);
    private static readonly Color Muted = Color.FromArgb(173, 177, 190);
    private static readonly Color Accent = Color.FromArgb(243, 67, 113);
    private static readonly Color ActiveBackground = Color.FromArgb(49, 33, 47);
    private static readonly Color ActiveBorder = Color.FromArgb(214, 76, 112);
    private static readonly Color ActiveForeground = Color.FromArgb(255, 218, 230);
    private static readonly Color SupplyAccent = Color.FromArgb(88, 91, 100);
    private static readonly Color SupplyBackground = Color.FromArgb(34, 35, 40);
    private static readonly Color SupplyBorder = Color.FromArgb(96, 98, 106);
    // HUD 표시 대상은 체크한 사업장 + 나이트클럽으로 그때그때 다시 만든다.
    // 끈 사업장은 목록에서도 사라져야 HUD에 남지 않는다.
    private const string NightclubHudTargetKey = "nightclub";

    // ── 표시 배율 ─────────────────────────────────────────────────────────
    // 이 창의 모든 좌표/여백/글꼴 상수는 96 DPI 설계 캔버스 기준으로 작성한다.
    // 실제 화면에는 UiScaler가 계산한 단일 배율(_uiScale)만 곱해서 그린다.
    // WinForms 자동 DPI 배율(AutoScaleMode.Dpi)은 컨트롤 경계만 확대하고
    // OnPaint에서 직접 만든 Point 글꼴은 시스템 DPI로 따로 환산되기 때문에,
    // 4K처럼 화면 DPI와 시스템 DPI가 어긋나는 환경에서 글자가 상자 밖으로
    // 넘쳐 잘렸다. 배율 계산과 적용을 한 곳으로 모아 그 어긋남을 없앤다.
    private const int DesignClientWidth = 1280;
    private const int DesignClientHeight = 800;
    // 바깥 스크롤을 없애고 껍데기를 Dock.Fill로 채우므로, 최소 크기는 껍데기가
    // 실제로 필요로 하는 콘텐츠 크기와 같아야 한다. 이보다 작아지면 사이드바와
    // 설정 표가 서로를 밀어내 잘린다.
    private const int DesignMinimumWidth = 1040;
    private const int DesignMinimumHeight = 620;
    // 기준 화면: QHD 2560x1440 + Windows 125% (논리 세로 1152px).
    // 어떤 해상도/배율에서도 이 화면과 같은 크기로 보이게 맞춘다.
    private const float ReferenceScreenHeight = 1152f;
    private const float MinimumUiScale = 0.75f;
    private const float MaximumUiScale = 3f;
    private static float _uiScale = 1f;
    private readonly UiScaler _scaler = new();
    private float _appliedUiScale;
    private bool _uiScaleApplied;
    private HudConfig _config;
    private readonly TextBox _hudToggleHotkey = new();
    private readonly TextBox _channelPickerHotkey = new();
    private readonly CheckBox _sessionPopulation = new SettingsCheckBox(){Text="세션 인원 표시",AutoSize=true};
    private readonly CheckBox _secondarySessionPopulation =
        new SettingsCheckBox(){Text=$"{GtaSessionPresence.TrackedHostName} 미접속 시 {GtaSessionPresence.SecondaryHostName}",AutoSize=true};
    private bool _channelSelectionDirty;
    private readonly TextBox _exitHotkey = new();
    private readonly TextBox _saleStatusHotkey = new();
    private readonly TextBox _vinewoodTimerPopupHotkey = new();
    private readonly TextBox _readBusinessScreenHotkey = new();
    private bool _loadingPreferences;
    private readonly CheckBox _saleAutoHideIdle = new SettingsCheckBox() { Text = "미완료 판매가 있을 때 자동 표시 · 없으면 바로 숨김", AutoSize = true, Dock = DockStyle.Fill };
    private readonly CheckBox _showChannelName = new SettingsCheckBox() { Text = "HUD에 채널명 표시", AutoSize = true };
    private readonly CheckBox _readScreenResupply = new SettingsCheckBox() { Text = "인식 + 보급", AutoSize = true };
    private readonly CheckBox _readScreenSound = new SettingsCheckBox() { Text = "완료 소리", AutoSize = true };
    private readonly Dictionary<string, CheckBox> _productionSpeedButtons = new();
    private readonly Dictionary<string, Button> _dailyBoostButtons = new();
    private readonly WheelLockedComboBox _businessCharacter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _alertSupplyLow = new SettingsCheckBox() { Text = "보급 부족", AutoSize = true };
    private readonly CheckBox _alertStockFull = new SettingsCheckBox() { Text = "재고 가득", AutoSize = true };
    private readonly CheckBox _alertBoostEnded = new SettingsCheckBox() { Text = "부스트 종료", AutoSize = true };
    private readonly CheckBox _alertSound = new SettingsCheckBox() { Text = "소리", AutoSize = true };
    private readonly NumericUpDown _alertSupplyMinutes = new() { Minimum = 1, Maximum = 120, Increment = 1 };
    private readonly ComboBox _layoutChoice = new SettingsComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _layoutName = new();
    private readonly NumericUpDown _width = new() { Minimum = 0, Maximum = HudLayoutPreset.MaximumWidth, Increment = 1 };
    private readonly NumericUpDown _height = new() { Minimum = 0, Maximum = HudLayoutPreset.MaximumHeight, Increment = 1 };
    private readonly IosSlider _fontScale = CreateScaleSlider(30, "글자 크기");
    private readonly IosSlider _mediaScale = CreateScaleSlider(30, "GIF 스티커 및 미디어", 100);
    private readonly IosSlider _mediaOpacity = CreateScaleSlider(0, "미디어 투명도 (0% 숨김, 100% 선명)", 100);
    private readonly Label _mediaOpacityValue = CreateSliderValueLabel();
    private readonly IosSlider _emojiScale = CreateScaleSlider(30, "이모지 크기", 200);
    private readonly Label _emojiScaleValue = CreateSliderValueLabel();
    private readonly IosSlider _reactionEmojiScale = CreateScaleSlider(30, "반응 이모지 크기", 200);
    private readonly Label _reactionEmojiScaleValue = CreateSliderValueLabel();
    private readonly CheckBox _autoFontScale = new SettingsCheckBox() { Text = "자동", AutoSize = true };
    private readonly Label _fontScaleValue = CreateSliderValueLabel();
    private readonly Label _mediaScaleValue = CreateSliderValueLabel();
    private readonly IosSlider _backgroundTintAlpha = CreateAlphaSlider();
    private readonly IosSlider _nicknameBadgeAlpha = CreateAlphaSlider();
    private readonly IosSlider _nicknameGroupGap = new()
    {
        ValueDescription = "닉네임 그룹 간격",
        Minimum = 0,
        Maximum = 80,
        Value = 36,
        LargeChange = 8,
        SmallChange = 1,
        Height = 32,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 8, 0)
    };
    private readonly IosSlider _saleTintAlpha = CreateAlphaSlider();
    private readonly IosSlider _businessTintAlpha = CreateAlphaSlider();
    private readonly Label _businessTintValue = CreateSliderValueLabel();
    private readonly IosSlider _staffAssignmentTintAlpha = CreateAlphaSlider();
    private readonly Label _backgroundTintValue = CreateSliderValueLabel();
    private readonly Label _nicknameBadgeValue = CreateSliderValueLabel();
    private readonly Label _nicknameGroupGapValue = CreateSliderValueLabel();
    private readonly Label _saleTintValue = CreateSliderValueLabel();
    private readonly Label _staffAssignmentTintValue = CreateSliderValueLabel();
    private readonly RadioButton _lightTint = new() { Text = "라이트 · 흰색", AutoSize = true };
    private readonly RadioButton _darkTint = new() { Text = "다크 · 검은색", AutoSize = true };
    private readonly RadioButton _clockHeader = new() { Text = "채널명 옆", AutoSize = true };
    private readonly RadioButton _clockBottom = new() { Text = "하단 틴트 안", AutoSize = true };
    private readonly RadioButton _clockLeft = new() { Text = "왼쪽", AutoSize = true };
    private readonly RadioButton _clockRight = new() { Text = "오른쪽", AutoSize = true };
    private readonly CheckBox _alwaysVisible = new IosToggleButton();
    private readonly CheckBox _excludeFromCapture = new IosToggleButton();
    private readonly CheckBox _animateGif = new IosToggleButton();
    private readonly CheckBox _showSaleStatus = new IosToggleButton { Text = "하단 판매 현황 표시", AutoSize = true };
    private readonly CheckBox _showBusinessHud = new IosToggleButton();
    private readonly CheckBox _businessOnline = new IosToggleButton();
    private readonly List<string> _productionOrder = [];
    private readonly Dictionary<string, Control> _productionCards = new(StringComparer.OrdinalIgnoreCase);
    private TableLayoutPanel? _productionGrid;
    private readonly Dictionary<string, TableLayoutPanel> _productionSettingRows = new(StringComparer.OrdinalIgnoreCase);
    private TableLayoutPanel? _businessSettingsGrid;
    private TableLayoutPanel? _businessSettingsHeading;
    private Label? _businessSettingsNote;
    private FlowLayoutPanel? _businessSelectionRow;
    private readonly List<(string Key, string Name)> _businessHudTargetItems = [];
    private readonly CheckBox _businessAutoOnline = new SettingsCheckBox();
    private readonly AutomaticOnlineTracker _autoOnlineTracker = new();
    private readonly CheckBox _showVinewoodTimerPopup = new IosToggleButton();
    private readonly Button _mansionBoostToggle = new IosActionButton(false);
    private readonly Label _mansionBoostEndsAt = new();
    private readonly ComboBox _businessHudTarget = new SettingsComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _activeSupplyBusiness = new SettingsComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _businessDisplayMode = new SettingsComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _position = new();
    private readonly ListBox _channelList = new();
    private readonly Label _status = new();
    private readonly HashSet<int> _dirtyLayouts = [];
    private readonly HashSet<int> _dirtyLayoutWidths = [];
    private readonly HashSet<int> _dirtyLayoutHeights = [];
    private readonly Dictionary<string, BusinessEditor> _businessEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _businessTooltips = new();
    private readonly Dictionary<string, Button> _remoteStaffTimerButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<int, Button>> _remoteStaffMultiplierButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Button> _nightclubSafeMultiplierButtons = [];
    private readonly Dictionary<TextBox, string> _hotkeyBeforeEdit = [];
    private readonly Dictionary<int, Control> _settingsPages = [];
    private readonly Dictionary<int, IosNavigationButton> _settingsNavigation = [];
    private Label? _settingsPageTitle;
    private Label? _settingsPageSubtitle;
    private StockNumericUpDown? _nightclubSafeCash;
    private Label? _nightclubPopularityValue;
    private Label? _nightclubSafeIncomeValue;
    private Button? _nightclubSafeTimerButton;
    private readonly System.Windows.Forms.Timer _businessLiveTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _autoApplyTimer = new() { Interval = 250 };
    private readonly GameWindowTracker _businessGame = new();
    private readonly Mutex _businessTrackerMutex;
    private bool _ownsBusinessTracker;
    private bool _loadingBusiness;
    private bool _businessSelectionDirty;
    private bool _liveBusinessInitialized;
    private readonly BusinessProgressClock _businessLiveClock = new();
    private DateTimeOffset _businessLiveLastSave = DateTimeOffset.MinValue;
    private Control? _wheelArmedControl;
    private bool _layoutStructureDirty;
    private bool _loadingLayout;
    private bool _autoApplyEnabled;
    private bool _autoApplyRunning;
    private bool _autoApplyPending;
    private int _editingLayout;
    private ulong _selectedTargetChannelId;

    protected override bool ShowWithoutActivation => Environment.GetCommandLineArgs().Contains("--auto-config");

    public ConfigForm(HudConfig config) : this(config, false) { }
    internal ConfigForm(HudConfig config, bool designPreview)
    {
        SuspendLayout();
        _config = config;
        using var handoff = new EventWaitHandle(false, EventResetMode.ManualReset, AppInstance.BusinessHandoffEventName);
        handoff.Reset();
        _businessTrackerMutex = new Mutex(false, AppInstance.ConfigBusinessTrackerMutexName);
        if (!designPreview) TryAcquireBusinessTracker();
        if (_ownsBusinessTracker)
        {
            // The HUD flushes its live fractions before acknowledging ownership transfer.
            try
            {
                using var hud = Mutex.OpenExisting(AppInstance.HudMutexName);
                bool idle;
                try { idle = hud.WaitOne(0); }
                catch (AbandonedMutexException) { idle = true; }
                if (idle) hud.ReleaseMutex();
                else if (!handoff.WaitOne(TimeSpan.FromSeconds(3)))
                    throw new InvalidOperationException("HUD 최신 타이머 상태를 받지 못했습니다. HUD와 설정 실행 파일을 모두 v205로 교체한 뒤 다시 실행해주세요.");
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch
            {
                _businessTrackerMutex.ReleaseMutex();
                _ownsBusinessTracker = false;
                _businessTrackerMutex.Dispose();
                throw;
            }
            _config = config = ConfigStore.Load();
        }
        _selectedTargetChannelId = config.TargetChannelId;
        Text = "DiscordChatHUD Config · C# Public Source 334";
        // 96 DPI 설계 크기로 만든 뒤 ApplyUiScale이 화면에 맞는 배율을 한 번에 적용한다.
        ClientSize = new Size(DesignClientWidth, DesignClientHeight);
        MinimumSize = new Size(DesignMinimumWidth, DesignMinimumHeight);
        MaximumSize = Size.Empty; // Let Windows maximize to each monitor work area.
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = WindowBackground;
        ForeColor = Foreground;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        // WinForms 자동 배율은 사용하지 않는다. 경계는 확대하면서 직접 그린
        // 글꼴은 시스템 DPI로 따로 환산돼 4K에서 글자가 잘리던 원인이다.
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ResizeRedraw = true;
        try { Icon = new Icon(AppPaths.IconPath); } catch { }

        BuildInterface();
        // 컨트롤이 모두 설계 값(96 DPI)을 가진 이 시점에 원본을 기록한다.
        // BuildInterface 도중에 이미 창 핸들이 만들어지는 경로가 있어서
        // (OnHandleCreated -> ApplyUiScale) 이보다 늦게 기록하면 배율 적용이
        // 빈 상태로 먼저 돌아 버린다.
        _scaler.Capture(this);
        ConfigureHotkeyCaptureFields();
        Application.AddMessageFilter(this);
        PopulateFromConfig();
        ApplyTheme(this);
        UpdateBusinessHudVisibilityAppearance();
        UpdateBusinessOnlineAppearance();
        UpdateVinewoodTimerPopupAppearance();
        RefreshMentionBoostHeader(DateTimeOffset.UtcNow);
        _businessLiveTimer.Tick += BusinessLiveTimerOnTick;
        _businessLiveTimer.Start();
        _readScreenResupply.CheckedChanged += (_, _) => PersistCheckboxPreferences();
        _saleAutoHideIdle.CheckedChanged += (_, _) => PersistCheckboxPreferences();
        _showChannelName.CheckedChanged += (_, _) => PersistCheckboxPreferences();
        _businessDisplayMode.SelectedIndexChanged += (_, _) =>
        {
            RefreshBusinessHudOptionAvailability();
            if (!_autoApplyEnabled || _loadingPreferences || _businessDisplayMode.SelectedIndex < 0) return;
            var mode = _businessDisplayMode.SelectedIndex switch { 0 => "compact", 2 => "expanded", _ => "default" };
            try { ConfigStore.UpdateDisplayMode(mode); _config.BusinessDisplayMode = mode; _status.Text = "HUD 표시 모드 저장 완료"; }
            catch (Exception ex) { AppLog.Error("HUD 표시 모드 저장 실패", ex); _status.Text = "HUD 표시 모드 저장 실패 · 다시 선택해주세요"; }
        };
        WireAutomaticApply(this);
        _autoApplyTimer.Tick += AutoApplyTimerOnTick;
        _autoApplyEnabled = true;
        ApplyUiScale(force: true);
        ResumeLayout(true);
        if (Environment.GetCommandLineArgs().Contains("--auto-config")) WindowState = FormWindowState.Minimized;
        Shown += (_, _) =>
        {
            ApplyDarkWindowChrome(Handle);
            if (!designPreview) BeginInvoke(new Action(() => PromptForInitialTokenIfNeeded()));
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DesktopContext.Current is not null)
        {
            try { FlushBeforeUpdateAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                e.Cancel = true;
                _status.Text = "저장 실패 · 설정창을 유지합니다";
                MessageBox.Show(this, "설정을 저장하지 못해 종료하지 않았습니다.\n" + ex.Message, Text);
                return;
            }
        }
        _businessLiveTimer.Stop();
        _autoApplyTimer.Stop();
        if (_ownsBusinessTracker)
        {
            UpdateLiveBusinessState(DateTimeOffset.UtcNow, forceSave: true);
            try { _businessTrackerMutex.ReleaseMutex(); } catch { }
        }
        _businessTrackerMutex.Dispose();
        _businessGame.Dispose();
        _businessLiveTimer.Dispose();
        _autoApplyTimer.Dispose();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
        _businessTooltips.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        _wheelArmedControl = null;
        base.OnDeactivate(e);
    }

    public bool PreFilterMessage(ref Message m)
    {
        const int WmLeftButtonDown = 0x0201;
        const int WmRightButtonDown = 0x0204;
        const int WmMiddleButtonDown = 0x0207;
        const int WmMouseWheel = 0x020A;
        if (m.Msg == WmMouseWheel)
        {
            var hovered = FindWheelAdjustableControlAt(Cursor.Position);
            if (hovered is not null && !ReferenceEquals(hovered, _wheelArmedControl))
            {
                var delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xffff));
                ForwardWheelToScrollableParent(hovered, delta);
                return true;
            }
        }
        if (m.Msg is not (WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown)) return false;

        Control? clickedAdjustable = null;
        for (var control = Control.FromChildHandle(m.HWnd); control is not null; control = control.Parent)
        {
            if (!IsWheelAdjustable(control)) continue;
            clickedAdjustable = control;
            break;
        }
        _wheelArmedControl = m.Msg == WmLeftButtonDown ? clickedAdjustable : null;
        return false;
    }

    private Control? FindWheelAdjustableControlAt(Point screenPoint)
    {
        Control? match = null;
        foreach (var control in EnumerateDescendants(this))
        {
            if (!control.Visible || !control.Enabled || !IsWheelAdjustable(control)) continue;
            if (!control.RectangleToScreen(control.ClientRectangle).Contains(screenPoint)) continue;
            match = control;
        }
        return match;
    }

    private static IEnumerable<Control> EnumerateDescendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateDescendants(child)) yield return descendant;
        }
    }

    private static bool IsWheelAdjustable(Control control)
        => control is ComboBox or NumericUpDown or IosSlider;

    private static void ForwardWheelToScrollableParent(Control origin, int delta)
    {
        for (var parent = origin.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollableControl scroll || !scroll.AutoScroll) continue;
            var wheelLines = SystemInformation.MouseWheelScrollLines;
            var pixels = Math.Max(18, (wheelLines > 0 ? wheelLines : 3) * 18);
            var currentY = -scroll.AutoScrollPosition.Y;
            var currentX = -scroll.AutoScrollPosition.X;
            var targetY = Math.Max(0, currentY - Math.Sign(delta) * pixels);
            scroll.AutoScrollPosition = new Point(currentX, targetY);
            break;
        }
    }

    private void ConfigureHotkeyCaptureFields()
    {
        foreach (var field in new[]
                 {
                     _hudToggleHotkey,
                     _exitHotkey,
                     _channelPickerHotkey,
                     _saleStatusHotkey,
                     _vinewoodTimerPopupHotkey,
                     _readBusinessScreenHotkey
                 })
        {
            field.ReadOnly = true;
            field.ShortcutsEnabled = false;
            field.Cursor = Cursors.Hand;
            field.Enter += (_, _) =>
            {
                _hotkeyBeforeEdit[field] = field.Text;
                field.SelectAll();
            };
            field.MouseClick += (_, _) =>
            {
                _hotkeyBeforeEdit[field] = field.Text;
                field.SelectAll();
            };
            field.PreviewKeyDown += (_, e) => e.IsInputKey = true;
            field.KeyDown += HotkeyFieldOnKeyDown;
        }
    }

    private void HotkeyFieldOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox field) return;
        e.SuppressKeyPress = true;
        e.Handled = true;

        if (e.KeyCode == Keys.Escape)
        {
            if (_hotkeyBeforeEdit.TryGetValue(field, out var previous)) field.Text = previous;
            field.SelectAll();
            return;
        }

        // Delete remains a bindable shortcut. Backspace alone clears a field.
        if (e.KeyCode == Keys.Back && e.Modifiers == Keys.None)
        {
            field.Text = "NONE";
            field.SelectAll();
            return;
        }

        if (e.KeyCode is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
            or Keys.Menu or Keys.LMenu or Keys.RMenu
            or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey)
            return;

        var keyName = GetHotkeyKeyName(e.KeyCode);
        if (keyName is null) return;
        var parts = new List<string>(4);
        if (e.Control) parts.Add("CTRL");
        if (e.Alt) parts.Add("ALT");
        if (e.Shift) parts.Add("SHIFT");
        parts.Add(keyName);
        field.Text = string.Join('+', parts);
        field.SelectAll();
    }

    private static string? GetHotkeyKeyName(Keys key)
    {
        var code = (int)key;
        if (code >= (int)Keys.A && code <= (int)Keys.Z)
            return ((char)('A' + code - (int)Keys.A)).ToString();
        if (code >= (int)Keys.D0 && code <= (int)Keys.D9)
            return ((char)('0' + code - (int)Keys.D0)).ToString();
        if (code >= (int)Keys.NumPad0 && code <= (int)Keys.NumPad9)
            return ((char)('0' + code - (int)Keys.NumPad0)).ToString();
        if (code >= (int)Keys.F1 && code <= (int)Keys.F24)
            return $"F{code - (int)Keys.F1 + 1}";
        return key switch
        {
            Keys.Delete => "DELETE",
            Keys.Home => "HOME",
            Keys.End => "END",
            Keys.Insert => "INSERT",
            Keys.PageUp => "PAGEUP",
            Keys.PageDown => "PAGEDOWN",
            Keys.Enter => "ENTER",
            Keys.Space => "SPACE",
            Keys.Tab => "TAB",
            Keys.Up => "UP",
            Keys.Down => "DOWN",
            Keys.Left => "LEFT",
            Keys.Right => "RIGHT",
            _ => null
        };
    }

    private TableLayoutPanel? _settingsShell;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyUiScale(force: true);
        BeginInvoke(new Action(() => { if (!IsDisposed) ApplyUiScale(force: true); }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkWindowChrome(Handle);
        // 핸들이 생기기 전에는 주 모니터 DPI만 알 수 있다. 창이 실제로 올라간
        // 모니터를 알게 된 지금 배율을 다시 계산해 무조건 다시 적용한다.
        ApplyUiScale(force: true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyUiScale(force: true);
        BeginInvoke(new Action(() => { if (!IsDisposed) ApplyUiScale(force: true); }));
    }

    /// <summary>
    /// 이 창이 사용할 단일 배율을 계산한다. 기준은 QHD 2560x1440 + Windows 125%.
    /// </summary>
    /// <remarks>
    /// Windows 배율(DPI)을 그대로 따르되, 배율이 낮은 고해상도 화면(4K 100% 등)
    /// 에서는 화면 크기로 배율을 올려 기준 화면과 같은 크기로 보이게 한다.
    /// 마지막으로 설계 크기가 작업 영역을 넘지 않도록 상한을 걸어 어떤 조합에서도
    /// 창이 화면 밖으로 잘리지 않게 한다.
    /// </remarks>
    private static float CalculateUiScale(Rectangle workArea, int deviceDpi)
    {
        var dpiScale = deviceDpi > 0 ? deviceDpi / 96f : 1f;
        var screenScale = workArea.Height > 0 ? workArea.Height / ReferenceScreenHeight : 1f;
        var scale = Math.Clamp(Math.Max(dpiScale, screenScale), MinimumUiScale, MaximumUiScale);
        if (workArea.Width > 0) scale = Math.Min(scale, workArea.Width / (float)DesignClientWidth);
        if (workArea.Height > 0) scale = Math.Min(scale, workArea.Height / (float)DesignClientHeight);
        return Math.Clamp(scale, MinimumUiScale, MaximumUiScale);
    }

    private Rectangle CurrentWorkArea()
        => IsHandleCreated
            ? Screen.FromHandle(Handle).WorkingArea
            : Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;

    /// <param name="force">
    /// 핸들이 생기기 전에는 주 모니터 DPI밖에 알 수 없고, 핸들 생성/첫 표시 과정에서
    /// WinForms가 배치를 다시 만들 수 있다. 그 두 시점에는 계산값이 같아도 설계
    /// 원본에서 무조건 다시 적용한다. 적용은 멱등이므로 반복해도 값이 누적되지 않는다.
    /// </param>
    private void ApplyUiScale(bool force = false)
    {
        if (IsDisposed) return;
        // 설계 원본을 아직 기록하지 못했다면 아무것도 하지 않는다. 여기서
        // _uiScaleApplied 를 세워 버리면 정작 기록이 끝난 뒤의 호출이 전부
        // 건너뛰어져, 글자만 커지고 상자는 설계 크기로 남는다.
        if (_scaler.ControlCount == 0) return;
        var work = CurrentWorkArea();
        var scale = CalculateUiScale(work, DeviceDpi);
        if (force || !_uiScaleApplied || Math.Abs(scale - _appliedUiScale) > 0.005f)
        {
            _uiScale = scale;
            SuspendLayout();
            try
            {
                // 설계 원본에서 다시 계산하므로 반복 호출해도 값이 누적되지 않는다.
                _scaler.Apply(scale);
                _channelList.ItemHeight = Math.Max(1, (int)Math.Round(62 * scale));
            }
            finally { ResumeLayout(true); }
            _appliedUiScale = scale;
            _uiScaleApplied = true;
            ResizeWindowForScale(work, scale);
            Invalidate(true);
        }
        FitSettingsToScreen();
    }

    private void ResizeWindowForScale(Rectangle work, float scale)
    {
        if (WindowState != FormWindowState.Normal) return;
        // 최소 크기를 먼저 풀어야 축소 방향으로도 크기가 적용된다.
        MinimumSize = Size.Empty;
        var client = new Size(
            Math.Max(DesignMinimumWidth, (int)Math.Round(DesignClientWidth * scale)),
            Math.Max(DesignMinimumHeight, (int)Math.Round(DesignClientHeight * scale)));
        ClientSize = client;
        MinimumSize = new Size(
            Math.Min(Math.Max(1, (int)Math.Round(DesignMinimumWidth * scale)), Math.Max(1, work.Width)),
            Math.Min(Math.Max(1, (int)Math.Round(DesignMinimumHeight * scale)), Math.Max(1, work.Height)));
        // 크기가 바뀐 뒤 왼쪽 위 모서리를 그대로 두면 창이 화면 밖으로 밀린다.
        // 배율이 바뀌는 시점(첫 표시 · 모니터 이동)에만 현재 모니터 중앙으로 맞춘다.
        if (!IsHandleCreated) return;
        Location = new Point(
            work.Left + Math.Max(0, (work.Width - Width) / 2),
            work.Top + Math.Max(0, (work.Height - Height) / 2));
    }

    private void FitSettingsToScreen()
    {
        if (!IsHandleCreated) return;
        var work = Screen.FromHandle(Handle).WorkingArea;
        if (WindowState != FormWindowState.Normal)
        {
            UpdateSettingsViewport();
            return;
        }
        var width = Math.Min(Width, work.Width);
        var height = Math.Min(Height, work.Height);
        var bounds = new Rectangle(
            Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - width)),
            Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - height)),
            width,
            height);
        if (Bounds != bounds) Bounds = bounds;
        UpdateSettingsViewport();
    }

    private void UpdateSettingsViewport()
    {
        // The outer shell must always fit the real client area. Only page
        // contents may scroll; navigation and action buttons stay on screen.
        // 창의 최소 크기를 DesignMinimumWidth/Height에 배율을 곱한 값으로 잡아
        // 두므로, 이 껍데기는 어떤 배율에서도 필요한 자리를 확보한다.
        if (_settingsShell is not null) _settingsShell.Dock = DockStyle.Fill;
    }

    private static float Scaled(float designValue) => designValue * _uiScale;

    private static int ScaledInt(float designValue) => (int)Math.Round(designValue * _uiScale);

    /// <summary>
    /// OnPaint에서 직접 만드는 글꼴. Point 단위는 그리는 장치의 DPI에 따라
    /// 다시 환산되므로, 배율을 미리 곱한 Pixel 단위로 고정해 어느 화면에서도
    /// 상자 크기와 글자 크기가 같은 비율을 유지하게 한다.
    /// </summary>
    private static Font ScaledFont(float designPoints, FontStyle style)
        => new("Segoe UI", Math.Max(6f, designPoints * (96f / 72f) * _uiScale), style, GraphicsUnit.Pixel);

    private void BuildInterface()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            Margin = new Padding(0),
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WindowBackground
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _settingsShell = shell;
        Controls.Add(shell);
        shell.Controls.Add(BuildSidebar(), 0, 0);

        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0),
            Padding = new Padding(22, 0, 22, 18),
            BackColor = WindowBackground
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(main, 1, 0);

        main.Controls.Add(BuildHeader(), 0, 0);

        var pageHost = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = WindowBackground
        };
        RegisterSettingsPage(pageHost, 1, BuildChannelsPanel());
        RegisterSettingsPage(pageHost, 2, BuildHudColumn());
        RegisterSettingsPage(pageHost, 3, BuildSalePanel());
        RegisterSettingsPage(pageHost, 4, BuildBusinessSettingsPanel());
        RegisterSettingsPage(pageHost, 5, BuildBusinessPanel());
        RegisterSettingsPage(pageHost, 6, BuildSettingsImportPanel());
        main.Controls.Add(pageHost, 0, 1);

        _status.Dock = DockStyle.Fill;
        _status.ForeColor = Muted;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(4, 0, 0, 0);
        main.Controls.Add(_status, 0, 2);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Height = 92,
            AutoSize = false,
            Margin = new Padding(0), Padding = new Padding(0, 4, 0, 8), BackColor = WindowBackground
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        var utilities = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            Margin = new Padding(0), Padding = new Padding(0)
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = false,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Margin = new Padding(0), Padding = new Padding(0)
        };
        footer.Controls.Add(utilities, 0, 0);
        footer.Controls.Add(actions, 0, 1);
        actions.Controls.Add(CreateButton("닫기", (_, _) => Close(), secondary: true, width: 92));
        actions.Controls.Add(CreateButton("실행", async (_, _) => await ApplyAndRunAsync(), width: 96));
        var positionPair = new FlowLayoutPanel { AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink,
            FlowDirection=FlowDirection.RightToLeft, WrapContents=false, Margin=new Padding(0), Padding=new Padding(0) };
        var positionButton=CreateButton("위치/크기 조정",async (_,_)=>await AdjustHudPositionAsync(),secondary:true,width:136);
        positionPair.Controls.Add(positionButton);
        positionPair.Controls.Add(CreateButton("HUD 미리보기", async (_, _) => await ApplyAndRunAsync(preview: true), secondary: true, width: 122));
        actions.Controls.Add(positionPair);
        _businessTooltips.SetToolTip(positionButton,"내부 드래그로 이동, 변·모서리 드래그로 크기 조절. Enter·완료로 저장, Esc로 취소합니다.");
        var updateButton = CreateButton("업데이트", (_, _) => { using var dialog = new UpdateForm(this); dialog.ShowDialog(this); }, secondary: true, width: 140);
        utilities.Controls.Add(CreateButton("패치노트", (_, _) => { using var notes = new PatchNotesForm(); notes.ShowDialog(this); }, secondary: true, width: 86));
        utilities.Controls.Add(updateButton);
        var gameAuto = new IosToggleButton { Text = "GTA 자동 실행", Tag = "game-auto-launch", Appearance = Appearance.Button, AutoSize = false,
            Width = 140, Height = updateButton.Height, Margin = updateButton.Margin, TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat, Checked = GameAutoLaunch.Enabled, ForeColor = Color.White };
        void RefreshAutoAppearance()
        {
            gameAuto.BackColor = gameAuto.Checked ? ActiveBackground : FieldBackground;
            gameAuto.FlatAppearance.BorderColor = gameAuto.Checked ? ActiveBorder : Color.FromArgb(62, 255, 255, 255);
            gameAuto.Invalidate();
        }
        RefreshAutoAppearance();
        gameAuto.CheckedChanged += async (_, _) =>
        {
            RefreshAutoAppearance();
            try
            {
                if (gameAuto.Checked) { await ApplyAndRunAsync(automatic: true); GameAutoLaunch.SetEnabled(true); }
                else GameAutoLaunch.SetEnabled(false);
            }
            catch (Exception ex) { MessageBox.Show(this, "자동 실행 전환에 실패했습니다.\n" + ex.Message); }
        };
        _businessTooltips.SetToolTip(gameAuto, "기존 창은 유지합니다. GTA 실행 시 꺼져 있는 설정창과 HUD만 실행합니다. 수동 HUD 종료도 가능합니다.");
        utilities.Controls.Add(gameAuto);
        utilities.Controls.SetChildIndex(gameAuto, 0);
        // Reserve the full button height plus margins in both footer rows.
        // The native layout clips children to their immediate parent, even if the form fits.
        foreach (var button in utilities.Controls.Cast<Control>()
            .Concat(actions.Controls.Cast<Control>()).Concat(positionPair.Controls.Cast<Control>())
            .OfType<ButtonBase>())
        {
            button.Height = 34;
            button.Margin = new Padding(4, 3, 4, 3);
        }
        var updateTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        var updateStop = new CancellationTokenSource();
        bool checkingUpdate = false; long checkedAt = long.MinValue;
        async Task RefreshUpdate(bool force)
        {
            if (checkingUpdate || IsDisposed || (!force && checkedAt != long.MinValue && Environment.TickCount64 - checkedAt < 15_000)) return;
            checkingUpdate = true; checkedAt = Environment.TickCount64;
            try
            {
                var update = await AppUpdate.Check(updateStop.Token);
                if (IsDisposed || updateButton.IsDisposed) return;
                var available = update.Manifest.Version > AppUpdate.CurrentVersion;
                updateButton.Text = available ? "업데이트 가능!" : "업데이트";
                updateButton.BackColor = available ? Accent : Color.FromArgb(42, 43, 51);
                updateButton.ForeColor = Color.White;
                updateButton.Invalidate();
            }
            catch { /* Keep the last known indication on transient network failure. */ }
            finally { checkingUpdate = false; }
        }
        Shown += async (_, _) => { updateTimer.Start(); await RefreshUpdate(true); };
        Activated += async (_, _) =>
        {
            if (!_channelSelectionDirty)
            {
                var target=ConfigStore.Load().TargetChannelId;
                _config.TargetChannelId=target; _selectedTargetChannelId=target;
                for(var i=0;i<_channelList.Items.Count;i++)
                    if(_channelList.Items[i] is ChannelPreset c && c.Id==target){_channelList.SelectedIndex=i;break;}
                _channelSelectionDirty=false;
                _channelList.Invalidate();
            }
            await RefreshUpdate(false);
        };
        updateTimer.Tick += async (_, _) => await RefreshUpdate(true);
        FormClosed += (_, _) => { updateTimer.Dispose(); updateStop.Cancel(); updateStop.Dispose(); };
        var configStop = new EventWaitHandle(false, EventResetMode.AutoReset, GameAutoLaunch.ConfigStopSignal);
        var autoSignal = new EventWaitHandle(false, EventResetMode.AutoReset, GameAutoLaunch.ConfigSignal);
        var autoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        bool autoLaunching = false;
        bool autoClosing = false;
        autoTimer.Tick += async (_, _) =>
        {
            RefreshServerChannelList();
            if (autoClosing) return;
            if (configStop.WaitOne(0))
            {
                autoClosing=true;autoTimer.Stop();
                try { await FlushBeforeUpdateAsync(); }
                catch(Exception ex){ AppLog.Error("자동 종료 저장 실패",ex); }
                finally { Close(); }
                return;
            }
            if(GameAutoLaunch.IsUpdating(AppContext.BaseDirectory)) return;
            if (!autoSignal.WaitOne(0) || autoLaunching || GameAutoLaunch.SessionPaused || !GameAutoLaunch.IsGameRunning()) return;
            autoLaunching = true;
            try { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Show(); Activate();
                if (!GameAutoLaunch.HudRunning()) await ApplyAndRunAsync(gameAuto: true); }
            finally { autoLaunching = false; }
        };
        Shown += async (_, _) =>
        {
            autoTimer.Start();
            var args = Environment.GetCommandLineArgs();
            if (args.Contains("--auto-hud")) autoSignal.Set();
            GameAutoLaunch.EnsureWatcher();
            if (DesktopContext.Current is not null) return;
            // Manual settings launch also starts the HUD after login and UI initialization.
            // Game-triggered settings retain the existing game/session checks.
            if (!args.Contains("--auto-hud") && !args.Contains("--auto-config")
                && !GameAutoLaunch.IsUpdating(AppContext.BaseDirectory)
                && !GameAutoLaunch.HudRunning())
            {
                if (_config.TargetChannelId == 0)
                    _status.Text = "표시할 채널을 선택한 뒤 실행을 눌러주세요.";
                else
                    await ApplyAndRunAsync(startIfStopped: true);
            }
        };
        FormClosed += (_, _) => { autoTimer.Dispose(); autoSignal.Dispose(); configStop.Dispose(); };
        main.Controls.Add(footer, 0, 3);
        ShowSettingsPage(1);
    }

    private Control BuildSidebar()
    {
        var sidebar = new IosSidebarPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(14, 18, 14, 16),
            BackColor = SidebarBackground
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = SidebarBackground
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        var brand = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(7, 0, 0, 0),
            BackColor = SidebarBackground
        };
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        brand.Controls.Add(new Label
        {
            Text = "DiscordChatHUD",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Foreground,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        brand.Controls.Add(new Label
        {
            Text = "오버레이 설정",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Muted,
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);
        layout.Controls.Add(brand, 0, 0);

        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 2, 0, 0),
            BackColor = SidebarBackground
        };
        AddNavigationButton(navigation, 1, "채널 프리셋", "HUD 대상 채널");
        AddNavigationButton(navigation, 2, "HUD 레이아웃", "크기 · 글자 · 투명도");
        AddNavigationButton(navigation, 3, "판매", "판매 HUD 표시");
        AddNavigationButton(navigation, 4, "사업장 설정", "캐릭터 · 장비 · 알림");
        AddNavigationButton(navigation, 5, "사업장 현황", "재고 · 보급 · 타이머");
        AddNavigationButton(navigation, 6, "설정 가져오기", "저장한 설정 불러오기");
        layout.Controls.Add(navigation, 0, 1);
        var accountStatus = new Label
        {
            Text = AccountSettingsSync.Status,
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 7.8f, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(7, 0, 0, 2)
        };
        layout.Controls.Add(accountStatus, 0, 2);
        void RefreshAccountStatus()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (!accountStatus.IsDisposed) accountStatus.Text = AccountSettingsSync.Status; })); }
            catch (InvalidOperationException) { }
        }
        AccountSettingsSync.StatusChanged += RefreshAccountStatus;
        FormClosed += (_, _) => AccountSettingsSync.StatusChanged -= RefreshAccountStatus;

        sidebar.Controls.Add(layout);
        return sidebar;
    }

    private Control BuildHeader()
    {
        var pageHeading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(3, 13, 0, 7),
            BackColor = WindowBackground
        };
        pageHeading.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        pageHeading.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        _settingsPageTitle = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Foreground,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _settingsPageSubtitle = new Label
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Muted,
            TextAlign = ContentAlignment.TopLeft
        };
        pageHeading.Controls.Add(_settingsPageTitle, 0, 0);
        pageHeading.Controls.Add(_settingsPageSubtitle, 0, 1);
        return pageHeading;
    }

    private void AddNavigationButton(Control parent, int page, string title, string detail)
    {
        var button = new IosNavigationButton(page, title, detail)
        {
            Width = 190,
            Height = 61,
            Margin = new Padding(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            TabStop = true
        };
        button.Click += (_, _) => ShowSettingsPage(page);
        _settingsNavigation[page] = button;
        parent.Controls.Add(button);
    }

    private void RegisterSettingsPage(Control host, int page, Control content)
    {
        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0);
        content.Visible = false;
        _settingsPages[page] = content;
        host.Controls.Add(content);
    }

    private void ShowSettingsPage(int page)
    {
        page = Math.Clamp(page, 1, 6);
        foreach (var item in _settingsPages)
        {
            item.Value.Visible = item.Key == page;
            if (item.Key == page) item.Value.BringToFront();
        }
        foreach (var item in _settingsNavigation)
            item.Value.Selected = item.Key == page;

        var (title, subtitle) = page switch
        {
            1 => ("채널 프리셋", "HUD에 표시할 채널을 선택합니다"),
            2 => ("HUD 레이아웃", "HUD 크기 · 글자 · 위치 · 표시 방식을 설정합니다"),
            3 => ("판매 HUD", "판매 HUD의 시작 상태와 단축키를 설정합니다"),
            4 => ("사업장 설정", "캐릭터 · HUD 표시 · 알림 · 사업장 장비와 직원을 설정합니다"),
            5 => ("사업장 현황", "재고 · 보급 · 멘션 부스트 · 클럽 타이머를 관리합니다"),
            6 => ("설정 가져오기", "JSON 파일을 선택하거나 끌어놓아 현재 설정을 덮어씁니다"),
            _ => ("채널 프리셋", "HUD에 표시할 채널을 선택합니다")
        };
        if (_settingsPageTitle is not null) _settingsPageTitle.Text = title;
        if (_settingsPageSubtitle is not null) _settingsPageSubtitle.Text = subtitle;
    }

    private void PopulateFromConfig()
    {
        _loadingPreferences = true;
        try {
        _saleAutoHideIdle.Checked = _config.SaleAutoHideIdle;
        _showChannelName.Checked = _config.ShowChannelName;
        _hudToggleHotkey.Text = _config.HudToggleHotkey;
        _exitHotkey.Text = _config.ExitHotkey;
        _channelPickerHotkey.Text = _config.ChannelPickerHotkey;
        _sessionPopulation.Checked=_config.ShowSessionPopulation;
        _secondarySessionPopulation.Checked=_config.ShowSecondarySessionPopulation;
        _saleStatusHotkey.Text = _config.SaleStatusHotkey;
        _vinewoodTimerPopupHotkey.Text = _config.VinewoodTimerPopupHotkey;
        _readBusinessScreenHotkey.Text = _config.ReadBusinessScreenHotkey;
        _readScreenSound.Checked = _config.ReadScreenSound;
        _readScreenResupply.Checked = _config.ReadScreenResupply;
        _alertSupplyLow.Checked = _config.AlertSupplyLow;
        _alertStockFull.Checked = _config.AlertStockFull;
        _alertBoostEnded.Checked = _config.AlertBoostEnded;
        _alertSound.Checked = _config.AlertSound;
        _alertSupplyMinutes.Value = Math.Clamp(_config.AlertSupplyMinutes, 1, 120);
        _backgroundTintAlpha.Value = Math.Clamp(_config.BackgroundTintAlpha, 0, 100);
        _nicknameBadgeAlpha.Value = Math.Clamp(_config.NicknameBadgeAlpha, 0, 100);
        _nicknameGroupGap.Value = Math.Clamp(_config.NicknameGroupGap, 0, 80);
        _saleTintAlpha.Value = Math.Clamp(_config.SaleTintAlpha, 0, 100);
        _businessTintAlpha.Value = Math.Clamp(_config.BusinessTintAlpha, 0, 100);
        _staffAssignmentTintAlpha.Value = Math.Clamp(_config.StaffAssignmentTintAlpha, 0, 100);
        var darkTint = string.Equals(_config.TintMode, "dark", StringComparison.OrdinalIgnoreCase);
        _lightTint.Checked = !darkTint;
        _darkTint.Checked = darkTint;
        var headerClock = string.Equals(_config.ClockPlacement, "header", StringComparison.OrdinalIgnoreCase);
        _clockHeader.Checked = headerClock;
        _clockBottom.Checked = !headerClock;
        var leftClock = string.Equals(_config.ClockAlignment, "left", StringComparison.OrdinalIgnoreCase);
        _clockLeft.Checked = leftClock;
        _clockRight.Checked = !leftClock;
        _alwaysVisible.Checked = _config.AlwaysVisible;
        UpdateVisibilityModeAppearance(_alwaysVisible);
        _excludeFromCapture.Checked = _config.ExcludeFromCapture;
        UpdateCaptureExclusionAppearance();
        // Old still-frame values are reset once by HudConfig (GIF_PLAYBACK migration).
        _animateGif.Checked = _config.AnimateGif;
        UpdateGifAnimationAppearance();
        _showSaleStatus.Checked = _config.ShowSaleStatus;
        UpdateSaleStatusStartAppearance(_showSaleStatus);
        _showBusinessHud.Checked = _config.ShowBusinessSupplies;
        UpdateBusinessHudVisibilityAppearance();
        _showVinewoodTimerPopup.Checked = _config.ShowVinewoodTimerPopup;
        UpdateVinewoodTimerPopupAppearance();
        RefreshBusinessCharacterChoices();
        _loadingBusiness = true;
        try
        {
            _businessOnline.Checked = _config.BusinessTrackingOnline;
            _businessAutoOnline.Checked = _config.BusinessAutoOnline;
            UpdateBusinessOnlineAppearance();
            var lastBoostTarget = _config.BusinessSupplies
                .Where(entry => entry.MansionBoostStartedAtUtc is not null
                                && BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)
                                && profile.SupportsMansionBoost)
                .OrderByDescending(entry => entry.MansionBoostStartedAtUtc)
                .FirstOrDefault();
            SetActiveSupplyBusiness(lastBoostTarget?.Key ?? _config.ActiveSupplyBusinessKey);
        }
        finally { _loadingBusiness = false; }
        _businessDisplayMode.SelectedIndex = _config.BusinessDisplayMode switch
        {
            "compact" => 0,
            "expanded" => 2,
            _ => 1
        };
        LoadBusinessHudOptions();
        // 표시 대상 목록은 체크한 사업장으로 만들어지므로, 카드를 채운 뒤에
        // 저장된 대상을 고른다. 순서를 바꾸면 목록이 비어 있어 무시된다.
        PopulateBusinessEditors();
        SetBusinessHudTarget(_config.BusinessHudTargetKey);
        RefreshNightclubSafeControls();
        UpdateTintValueLabels();
        RefreshLayoutChoices(_config.ActiveLayoutPreset);
        LoadLayout(_config.ActiveLayoutPreset);
        RefreshChannelList();
        _channelSelectionDirty=false;
        } finally { _loadingPreferences = false; }
    }

    private bool PromptForInitialTokenIfNeeded()
    {
        if (RelayClientSettings.Enabled) return true;
        if (CredentialStore.HasUsablePortableToken) return true;

        using var dialog = new InitialTokenDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        try
        {
            CredentialStore.SaveToken(dialog.GetToken());
            _status.Text = "첫 실행 설정 완료";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"첫 실행 설정 저장 실패\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    internal async Task FlushBeforeUpdateAsync()
    {
        if (!ValidateChildren()) throw new InvalidDataException("입력한 설정을 확인해주세요.");
        _autoApplyTimer.Stop();
        await ApplyAndRunAsync(automatic: true, requireSave: true);
        if (_ownsBusinessTracker) UpdateLiveBusinessState(DateTimeOffset.UtcNow, forceSave: true);
    }

    private async Task ApplyAndRunAsync(bool preview = false, bool automatic = false, bool gameAuto = false, bool startIfStopped = false, bool requireSave = false)
    {
        if (startIfStopped && GameAutoLaunch.HudRunning()) return;
        CommitCurrentLayout();
        var targetChannel = _channelSelectionDirty ? _selectedTargetChannelId : ConfigStore.Load().TargetChannelId;
        if (targetChannel == 0 && !automatic)
        {
            if (!automatic) MessageBox.Show("01 채널 프리셋에서 HUD에 표시할 채널을 선택해.", Text);
            return;
        }
        if (_width.Value is > 0 and < HudLayoutPreset.MinimumWidth || _height.Value is > 0 and < HudLayoutPreset.MinimumHeight)
        {
            if (requireSave) throw new InvalidDataException("폭과 높이는 0 또는 200 이상이어야 합니다.");
            if (!automatic) MessageBox.Show("폭과 높이는 0(자동) 또는 200 이상이어야 해.", Text);
            return;
        }
        var hudToggle = HotkeyParser.Normalize(_hudToggleHotkey.Text, "ALT+T");
        var channelPicker = HotkeyParser.Normalize(_channelPickerHotkey.Text, "SHIFT+T");
        var exit = HotkeyParser.Normalize(_exitHotkey.Text, "NONE");
        var saleStatusToggle = HotkeyParser.Normalize(_saleStatusHotkey.Text, "F6");
        var vinewoodPopupToggle = HotkeyParser.Normalize(_vinewoodTimerPopupHotkey.Text, "ALT+V");
        var readBusinessScreen = HotkeyParser.Normalize(_readBusinessScreenHotkey.Text, "F7");
        if (!HotkeyParser.TryParse(hudToggle, out _)
            || !HotkeyParser.TryParse(channelPicker, out _)
            || !HotkeyParser.TryParse(exit, out _)
            || !HotkeyParser.TryParse(saleStatusToggle, out _)
            || !HotkeyParser.TryParse(vinewoodPopupToggle, out _)
            || !HotkeyParser.TryParse(readBusinessScreen, out _))
        {
            if (requireSave) throw new InvalidDataException("단축키 형식을 확인해주세요.");
            if (!automatic) MessageBox.Show("단축키 형식이 이상해. 예: ALT+T, F6, CTRL+ALT+Q, NONE", Text);
            return;
        }
        var enabledHotkeys = new[] { hudToggle, exit, saleStatusToggle, vinewoodPopupToggle, readBusinessScreen, channelPicker }
            .Concat((_config.BusinessActionHotkeys ?? new()).Values)
            .Where(value => !string.Equals(value, "NONE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (enabledHotkeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != enabledHotkeys.Length)
        {
            if (requireSave) throw new InvalidDataException("중복된 단축키를 확인해주세요.");
            if (!automatic) MessageBox.Show("HUD 숨기기/표시, 종료, 판매, 직원 배정 HUD, 화면 판독, 채널 선택, 사업장 보급·판매 단축키는 서로 다르게 지정해야 해.", Text);
            return;
        }

        var businessSupplies = BuildBusinessEntries();

        try
        {
            if (!automatic && !PromptForInitialTokenIfNeeded()) return;

            MergeLatestGeometry();
            _config.TargetChannelId = targetChannel;
            _selectedTargetChannelId=targetChannel;
            _config.ChannelPickerHotkey=channelPicker;
            _config.ShowSessionPopulation=_sessionPopulation.Checked;
            _config.ShowSecondarySessionPopulation=_secondarySessionPopulation.Checked;
            _config.HudToggleHotkey = hudToggle;
            _config.ToggleHotkey = "NONE";
            _config.ExitHotkey = exit;
            _config.SaleStatusHotkey = saleStatusToggle;
            _config.VinewoodTimerPopupHotkey = vinewoodPopupToggle;
            _config.ReadBusinessScreenHotkey = readBusinessScreen;
            _config.ReadScreenSound = _readScreenSound.Checked;
            _config.ReadScreenResupply = _readScreenResupply.Checked;
            _config.AlertSupplyLow = _alertSupplyLow.Checked;
            _config.AlertStockFull = _alertStockFull.Checked;
            _config.AlertBoostEnded = _alertBoostEnded.Checked;
            _config.AlertSound = _alertSound.Checked;
            _config.AlertSupplyMinutes = (int)_alertSupplyMinutes.Value;
            _config.BackgroundTintAlpha = (int)_backgroundTintAlpha.Value;
            _config.NicknameBadgeAlpha = (int)_nicknameBadgeAlpha.Value;
            _config.NicknameGroupGap = (int)_nicknameGroupGap.Value;
            _config.SaleTintAlpha = (int)_saleTintAlpha.Value;
            _config.BusinessTintAlpha = (int)_businessTintAlpha.Value;
            _config.StaffAssignmentTintAlpha = (int)_staffAssignmentTintAlpha.Value;
            _config.TintMode = _darkTint.Checked ? "dark" : "light";
            _config.ClockPlacement = _clockHeader.Checked ? "header" : "bottom";
            _config.ClockAlignment = _clockLeft.Checked ? "left" : "right";
            _config.AlwaysVisible = _alwaysVisible.Checked;
            _config.ExcludeFromCapture = _excludeFromCapture.Checked;
            _config.AnimateGif = _animateGif.Checked;
            _config.ShowSaleStatus = _showSaleStatus.Checked;
            _config.ShowBusinessSupplies = _showBusinessHud.Checked;
            _config.ShowVinewoodTimerPopup = _showVinewoodTimerPopup.Checked;
            _config.VinewoodPopupMigrationVersion = 103;
            _config.BusinessTrackingOnline = _businessOnline.Checked;
            _config.BusinessTrackingOnlineMigrationVersion = 89;
            _config.ActiveSupplyBusinessKey = SelectedSupplyBusinessKey();
            _config.BusinessHudTargetKey = SelectedBusinessHudTargetKey();
            _config.BusinessDisplayMode = _businessDisplayMode.SelectedIndex switch
            {
                0 => "compact",
                2 => "expanded",
                _ => "default"
            };
            _config.BusinessSupplies = businessSupplies;
            _config.ActiveLayoutPreset = Math.Clamp(_editingLayout, 0, _config.LayoutPresets.Count - 1);
            ConfigStore.Save(_config);
            _channelSelectionDirty=false;
            ClearLayoutEdits();
            LoadLayout(_editingLayout);
            if (automatic)
            {
                // The running HUD polls the saved configuration and applies
                // display data in place. Never terminate or relaunch it for an
                // ordinary edit; this also prevents visible HUD flicker while
                // dragging a slider.
                _status.Text = "변경 감지 · 자동 저장";
                return;
            }

            if (DesktopContext.Current is { } desktop)
            {
                await desktop.StartHudAsync(preview, keepExisting: startIfStopped || gameAuto);
                _status.Text = "저장 완료 · HUD 실행";
                return;
            }
            throw new InvalidOperationException("통합 클라이언트에서 설정창을 실행해주세요.");
        }
        catch (Exception ex)
        {
            AppLog.Error("설정 저장/실행 실패", ex);
            _status.Text = "저장 실패 · 변경 내용을 다시 저장해주세요";
            if (requireSave) throw;
            if (!automatic)
                MessageBox.Show($"저장 또는 실행 실패\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool _requestingPositionEdit;
    private async Task AdjustHudPositionAsync()
    {
        if(_requestingPositionEdit)return;
        _requestingPositionEdit=true;
        try
        {
            await ApplyAndRunAsync(automatic:true);
            bool Signal()
            {
                if(!EventWaitHandle.TryOpenExisting(AppInstance.PositionEditEventName,out var signal))return false;
                using(signal){signal.Set();}return true;
            }
            // Reuse the live HUD; starting one is only necessary if no listener exists.
            if(!Signal())
            {
                await ApplyAndRunAsync();
                var connected=false;
                for(var i=0;i<40 && !IsDisposed;i++)
                {await Task.Delay(250);if(Signal()){connected=true;break;}}
                if(!connected){_status.Text="HUD 연결 후 위치 조정을 다시 눌러주세요.";return;}
            }
            _status.Text="내부를 드래그해 이동, 변·모서리를 드래그해 크기 조절. Enter로 완료, Esc로 취소합니다.";
        }
        catch(Exception ex){AppLog.Error("위치/크기 조정 실패",ex);_status.Text="위치/크기 조정을 시작하지 못했습니다.";}
        finally{_requestingPositionEdit=false;}
    }
    private void WireAutomaticApply(Control root)
    {
        foreach (Control child in root.Controls)
        {
            switch (child)
            {
                case TextBox textBox:
                    textBox.TextChanged += (_, _) => ScheduleAutomaticApply();
                    break;
                case CheckBox checkBox:
                    checkBox.CheckedChanged += (_, _) => ScheduleAutomaticApply();
                    break;
                case RadioButton radioButton:
                    radioButton.CheckedChanged += (_, _) => ScheduleAutomaticApply();
                    break;
                case ComboBox comboBox:
                    comboBox.SelectedIndexChanged += (_, _) => ScheduleAutomaticApply();
                    break;
                case NumericUpDown numeric:
                    numeric.ValueChanged += (_, _) => ScheduleAutomaticApply();
                    break;
                case IosSlider slider:
                    slider.ValueChanged += (_, _) => ScheduleAutomaticApply();
                    break;
                case ListBox listBox:
                    listBox.SelectedIndexChanged += (_, _) => ScheduleAutomaticApply();
                    break;
            }
            WireAutomaticApply(child);
        }
    }

    private void PersistCheckboxPreferences()
    {
        if (!_autoApplyEnabled || _loadingPreferences || IsDisposed) return;
        try
        {
            ConfigStore.UpdateCheckboxPreferences(_saleAutoHideIdle.Checked, _readScreenResupply.Checked, _showChannelName.Checked);
            _config.SaleAutoHideIdle = _saleAutoHideIdle.Checked;
            _config.ReadScreenResupply = _readScreenResupply.Checked;
            _config.ShowChannelName = _showChannelName.Checked;
        }
        catch (Exception ex)
        {
            AppLog.Error("체크 설정 저장 실패", ex);
            _status.Text = "체크 설정 저장 실패 · 다시 시도해주세요";
        }
    }

    private void ScheduleAutomaticApply()
    {
        if (!_autoApplyEnabled || _loadingPreferences || _loadingBusiness || _loadingLayout || IsDisposed) return;
        _autoApplyTimer.Stop();
        _autoApplyTimer.Start();
    }

    private async void AutoApplyTimerOnTick(object? sender, EventArgs e)
    {
        _autoApplyTimer.Stop();
        if (_autoApplyRunning)
        {
            _autoApplyPending = true;
            return;
        }
        _autoApplyRunning = true;
        try
        {
            await ApplyAndRunAsync(automatic: true);
        }
        finally
        {
            _autoApplyRunning = false;
            if (_autoApplyPending)
            {
                _autoApplyPending = false;
                ScheduleAutomaticApply();
            }
        }
    }

    private void ClearLayoutEdits()
    {
        _dirtyLayouts.Clear();
        _dirtyLayoutWidths.Clear();
        _dirtyLayoutHeights.Clear();
        _layoutStructureDirty = false;
    }

    private void MergeLatestGeometry()
    {
        if (_layoutStructureDirty) return;
        var latest = ConfigStore.Load();
        if (latest.LayoutPresets.Count != _config.LayoutPresets.Count) return;
        for (var i = 0; i < _config.LayoutPresets.Count; i++)
        {
            var current = _config.LayoutPresets[i];
            var saved = latest.LayoutPresets[i];
            if (!_dirtyLayouts.Contains(i))
            {
                _config.LayoutPresets[i] = saved.Clone();
                continue;
            }
            // Scale/name edits do not own window dimensions. Preserve the
            // latest HUD drag size unless this exact dimension was edited.
            if (!_dirtyLayoutWidths.Contains(i)) current.Width = saved.Width;
            if (!_dirtyLayoutHeights.Contains(i)) current.Height = saved.Height;
            current.PositionX = saved.PositionX;
            current.PositionY = saved.PositionY;
            current.PositionSpace = saved.PositionSpace;
            current.PositionWorkWidth = saved.PositionWorkWidth;
            current.PositionWorkHeight = saved.PositionWorkHeight;
        }
    }
}
