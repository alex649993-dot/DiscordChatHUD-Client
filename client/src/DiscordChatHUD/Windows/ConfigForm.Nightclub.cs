using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 나이트클럽 카드(기술자·금고·인기도)와 원거리 직원 배정 타이머.
internal sealed partial class ConfigForm
{

    private Control CreateNightclubBusinessCard()
    {
        var card = new ToolkitBusinessCard("나이트클럽 · 창고 기술자 / 금고")
        {
            Height = 540,
            Margin = new Padding(0, 0, 0, 12)
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 344));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = BusinessSupplyCatalog.NightclubProfiles.Count() + 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        for (var index = 0; index < BusinessSupplyCatalog.NightclubProfiles.Count(); index++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var productHeading = CreateInlineLabel("상품");
        productHeading.ForeColor = Color.FromArgb(193, 205, 218, 230);
        productHeading.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold, GraphicsUnit.Point);
        var stockHeading = CreateInlineLabel("재고 수량");
        stockHeading.ForeColor = productHeading.ForeColor;
        stockHeading.Font = productHeading.Font;
        var progressHeading = CreateInlineLabel("재고 현황");
        progressHeading.ForeColor = productHeading.ForeColor;
        progressHeading.Font = productHeading.Font;
        var staffCount = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(185, 210, 230, 255),
            Font = new Font("Segoe UI", 8.4f, FontStyle.Bold, GraphicsUnit.Point)
        };
        table.Controls.Add(productHeading, 0, 0);
        table.Controls.Add(stockHeading, 1, 0);
        table.Controls.Add(progressHeading, 2, 0);
        table.Controls.Add(staffCount, 3, 0);
        table.Controls.Add(CreateBusinessSettingHeading("판매 · 단축키"), 4, 0);
        table.Controls.Add(CreateBusinessSettingHeading("생산"), 5, 0);

        var row = 1;
        var summaryMeter = new StockMeter
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(5, 7, 7, 7),
            AccentColor = Accent,
            Maximum = BusinessSupplyCatalog.NightclubProfiles.Sum(profile => profile.StockCapacity)
        };
        var summaryLabel = CreateInlineLabel("요약");
        summaryLabel.Font = new Font("Segoe UI", 8.4f, FontStyle.Bold, GraphicsUnit.Point);
        foreach (var profile in BusinessSupplyCatalog.NightclubProfiles)
        {
            var name = CreateInlineLabel(profile.Name);
            name.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _businessTooltips.SetToolTip(name, $"연결 사업장: {profile.SourceBusiness}");
            var stock = CreateBusinessUnitInput(profile.StockCapacity, $"{profile.Name} 재고");
            _businessTooltips.SetToolTip(stock, "이 칸을 클릭한 뒤 휠로 조정할 수 있어.");
            var meter = new StockMeter { Dock = DockStyle.Fill, Margin = new Padding(5, 7, 7, 7), AccentColor = Accent };
            var assigned = new IosToggleButton
            {
                Text = "배정 X",
                Appearance = Appearance.Button,
                AutoSize = false,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(3, 4, 5, 4),
                ForeColor = Foreground,
                BackColor = FieldBackground,
                Font = new Font("Segoe UI", 7.6f, FontStyle.Bold, GraphicsUnit.Point)
            };
            assigned.Tag = "nightclub-assignment";
            assigned.FlatAppearance.BorderColor = Color.FromArgb(70, 255, 255, 255);
            table.Controls.Add(name, 0, row);
            table.Controls.Add(stock, 1, row);
            table.Controls.Add(meter, 2, row);
            table.Controls.Add(assigned, 3, row);
            table.Controls.Add(CreateBusinessSaleButton(profile.Key), 4, row);
            table.Controls.Add(CreateProductionSpeedButton(profile.Key), 5, row);
            var editor = new BusinessEditor(profile, assigned, null, null, stock, meter, null, null, null, null)
            {
                NightclubStaffCountLabel = staffCount,
                NightclubSummaryLabel = summaryLabel,
                NightclubSummaryMeter = summaryMeter
            };
            _businessEditors[profile.Key] = editor;
            stock.ValueChanged += (_, _) =>
            {
                if (_loadingBusiness) return;
                editor.StockEdited = true;
                RefreshBusinessEditorPreview(editor);
            };
            stock.WheelValueChanged += (_, change) =>
                ApplyStockWheelSupplyAdjustment(editor, change.PreviousValue, change.CurrentValue);
            assigned.CheckedChanged += (_, _) =>
            {
                UpdateNightclubAssignmentAppearance(assigned);
                NightclubAssignmentChanged(editor);
            };
            row++;
        }

        table.Controls.Add(summaryLabel, 0, row);
        table.Controls.Add(summaryMeter, 1, row);
        table.SetColumnSpan(summaryMeter, 2);
        var summaryHint = CreateInlineLabel("총 360칸");
        summaryHint.TextAlign = ContentAlignment.MiddleRight;
        summaryHint.ForeColor = Muted;
        summaryHint.Font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        table.Controls.Add(summaryHint, 3, row);
        table.Controls.Add(CreateBusinessSaleButton(null), 4, row);
        row++;

        var note = CreateInlineLabel("기술자는 최대 5명 · 한국어 상품 판매 화면에서 F7 → 나이트클럽 자동 인식 → 인식 후 재고 자동 적용");
        note.ForeColor = Muted;
        note.Font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        table.Controls.Add(note, 0, row);
        table.SetColumnSpan(note, 6);
        content.Controls.Add(table, 0, 0);
        content.Controls.Add(CreateNightclubSafePanel(), 0, 1);
        card.Controls.Add(content);
        return card;
    }

    private Control CreateNightclubSafePanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 5, 0, 0),
            Padding = new Padding(0)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = CreateInlineLabel("금고 · 인기도");
        heading.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point);
        heading.ForeColor = Color.FromArgb(202, 216, 230);
        panel.Controls.Add(heading, 0, 0);

        var statusRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        _nightclubSafeCash = new StockNumericUpDown
        {
            Minimum = 0,
            Maximum = NightclubSafeState.Capacity,
            Increment = 1_000,
            ThousandsSeparator = true,
            DecimalPlaces = 0,
            Dock = DockStyle.Fill,
            Margin = new Padding(2),
            TextAlign = HorizontalAlignment.Right,
            AccessibleName = "나이트클럽 금고 현재 금액"
        };
        _businessTooltips.SetToolTip(_nightclubSafeCash, "금고 금액을 직접 입력하거나 이 칸을 클릭한 뒤 휠로 조정");
        _nightclubSafeCash.ValueChanged += (_, _) => NightclubSafeCashChanged();
        var popularityMinus = CreateButton("−", (_, _) => ChangeNightclubPopularity(-5), secondary: true, width: 38);
        popularityMinus.Dock = DockStyle.Fill;
        popularityMinus.Margin = new Padding(2);
        var popularityPlus = CreateButton("+", (_, _) => ChangeNightclubPopularity(5), secondary: true, width: 38);
        popularityPlus.Dock = DockStyle.Fill;
        popularityPlus.Margin = new Padding(2);
        _nightclubPopularityValue = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Foreground,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point)
        };
        var popularityTip =
            "인력 업그레이드를 산 기준입니다. 금고 주기(48분 = 게임 내 하루)마다 인기도가 "
            + "2.5%p씩 줄어, 두 주기(96분)마다 5%p 내려갑니다. 업그레이드가 없으면 두 배 빠른 "
            + "5%p입니다. 적립액은 그 주기 동안 유지한 인기도로 정해지고, 그 뒤에 내려갑니다.\n"
            + "95~100% $50,000 · 90% $45,000 · 85% $25,000 · 80% $24,000 · 75% $23,000 · "
            + "70% $22,000 · 65% $21,000 · 60% $20,000 · 55% $10,000 · 50% $9,500 · "
            + "45% $9,000 · 40% $8,500 · 35% $8,000 · 30% $2,500 · 25% $2,200 · "
            + "20% $2,000 · 15% $1,800 · 10% $1,600 · 5% 이하 $1,500\n"
            + "90%와 85% 사이, 60%와 55% 사이, 35%와 30% 사이에서 크게 떨어지니 "
            + "홍보 임무나 DJ 교체로 90% 위를 유지하는 편이 좋습니다.";
        _businessTooltips.SetToolTip(popularityMinus, popularityTip);
        _businessTooltips.SetToolTip(popularityPlus, popularityTip);
        _businessTooltips.SetToolTip(_nightclubPopularityValue, popularityTip);
        statusRow.Controls.Add(CreateInlineLabel("금고"), 0, 0);
        statusRow.Controls.Add(_nightclubSafeCash, 1, 0);
        statusRow.Controls.Add(CreateInlineLabel("인기도"), 2, 0);
        statusRow.Controls.Add(popularityMinus, 3, 0);
        statusRow.Controls.Add(_nightclubPopularityValue, 4, 0);
        statusRow.Controls.Add(popularityPlus, 5, 0);
        panel.Controls.Add(statusRow, 0, 1);

        var timerRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        timerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        timerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        timerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        timerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        timerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        timerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        _nightclubSafeIncomeValue = CreateInlineLabel("다음 수익 $50,000");
        _nightclubSafeIncomeValue.Font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
        _nightclubSafeTimerButton = CreateButton("금고 · 시작 (48:00)", (_, _) => ToggleNightclubSafeTimer(), secondary: true);
        _nightclubSafeTimerButton.Dock = DockStyle.Fill;
        _nightclubSafeTimerButton.Margin = new Padding(2);
        _nightclubSafeTimerButton.Font = new Font("Segoe UI", 7.8f, FontStyle.Bold, GraphicsUnit.Point);
        var reset = CreateButton("초기화", (_, _) => ResetNightclubSafeTimer(), secondary: true, width: 54);
        reset.Dock = DockStyle.Fill;
        reset.Margin = new Padding(1);
        reset.Font = new Font("Segoe UI", 7.4f, FontStyle.Bold, GraphicsUnit.Point);
        _businessTooltips.SetToolTip(reset, "금고 타이머만 시작 전 상태로 초기화");
        timerRow.Controls.Add(_nightclubSafeIncomeValue, 0, 0);
        timerRow.Controls.Add(_nightclubSafeTimerButton, 1, 0);
        timerRow.Controls.Add(reset, 2, 0);
        foreach (var multiplier in new[] { 2, 3, 4 })
        {
            var speedButton = CreateButton($"{multiplier}×", (_, _) => ToggleNightclubSafeMultiplier(multiplier), secondary: true, width: 42);
            speedButton.Dock = DockStyle.Fill;
            speedButton.Margin = new Padding(1);
            speedButton.Font = new Font("Segoe UI", 7.8f, FontStyle.Bold, GraphicsUnit.Point);
            _nightclubSafeMultiplierButtons[multiplier] = speedButton;
            timerRow.Controls.Add(speedButton, multiplier + 1, 0);
        }
        panel.Controls.Add(timerRow, 0, 2);

        var note = CreateInlineLabel("통합 온라인 + GTA 실행 중에만 진행 · 48분마다 현재 인기도 수익 적립 · 금고 최대 $250,000");
        note.ForeColor = Muted;
        note.Font = new Font("Segoe UI", 7.8f, FontStyle.Regular, GraphicsUnit.Point);
        panel.Controls.Add(note, 0, 3);
        return panel;
    }

    private Control CreateRemoteStaffTimerCard()
    {
        var card = new ToolkitBusinessCard("원거리 직원 타이머")
        {
            Height = 304,
            Margin = new Padding(0, 0, 0, 12)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = RemoteStaffTimerEntry.CreateDefaults().Count + 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        foreach (var _ in RemoteStaffTimerEntry.CreateDefaults())
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));

        var popupSettings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 1, 0, 1),
            BackColor = Color.Transparent
        };
        popupSettings.Controls.Add(CreateFlowLabel("HUD 표시"));
        _showVinewoodTimerPopup.AutoSize = false;
        _showVinewoodTimerPopup.Size = new Size(168, 28);
        _showVinewoodTimerPopup.Margin = new Padding(5, 1, 12, 1);
        _showVinewoodTimerPopup.Tag = "vinewood-popup-visibility";
        popupSettings.Controls.Add(_showVinewoodTimerPopup);
        popupSettings.Controls.Add(CreateFlowLabel("토글 단축키"));
        _vinewoodTimerPopupHotkey.Width = 122;
        _vinewoodTimerPopupHotkey.Margin = new Padding(6, 2, 0, 2);
        popupSettings.Controls.Add(_vinewoodTimerPopupHotkey);
        table.Controls.Add(popupSettings, 0, 0);
        table.SetColumnSpan(popupSettings, 5);

        var hint = CreateInlineLabel("작업 버튼 · 초기화 · 생산 배수 2× / 3× / 4× (선택 배수를 다시 누르면 1×)");
        hint.ForeColor = Muted;
        hint.Font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        table.Controls.Add(hint, 0, 1);
        table.SetColumnSpan(hint, 5);

        var row = 2;
        foreach (var timer in _config.RemoteStaffTimers)
        {
            var button = CreateButton($"{timer.Name} · 시작 (48:00)", (_, _) => StartRemoteStaffTimer(timer.Key), secondary: true);
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 2, 6, 2);
            button.Font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
            _remoteStaffTimerButtons[timer.Key] = button;
            table.Controls.Add(button, 0, row++);

            var resetButton = CreateButton("초기화", (_, _) => ResetRemoteStaffTimer(timer.Key), secondary: true, width: 54);
            resetButton.Dock = DockStyle.Fill;
            resetButton.Margin = new Padding(1);
            resetButton.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold, GraphicsUnit.Point);
            _businessTooltips.SetToolTip(resetButton, "해당 타이머를 시작 전 상태로 초기화");
            table.Controls.Add(resetButton, 1, row - 1);

            var multiplierButtons = new Dictionary<int, Button>();
            foreach (var multiplier in new[] { 2, 3, 4 })
            {
                var speedButton = CreateButton($"{multiplier}×", (_, _) => ToggleRemoteStaffMultiplier(timer.Key, multiplier), secondary: true, width: 42);
                speedButton.Dock = DockStyle.Fill;
                speedButton.Margin = new Padding(1);
                speedButton.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold, GraphicsUnit.Point);
                multiplierButtons[multiplier] = speedButton;
                table.Controls.Add(speedButton, multiplier, row - 1);
            }
            _remoteStaffMultiplierButtons[timer.Key] = multiplierButtons;
        }

        card.Controls.Add(table);
        RefreshRemoteStaffTimers(DateTimeOffset.UtcNow);
        return card;
    }

    private void StartRemoteStaffTimer(string key)
    {
        var timer = _config.RemoteStaffTimers.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (timer is null) return;
        var now = DateTimeOffset.UtcNow;
        var duration = GetRemoteStaffDuration(timer);
        var remaining = GetRemoteStaffRemaining(timer, now);
        if (timer.StartedAtUtc is not null && remaining > TimeSpan.Zero)
        {
            timer.RemainingSeconds = remaining.TotalSeconds;
            timer.StartedAtUtc = null;
            timer.HasStarted = true;
            _status.Text = $"{timer.Name} 직원 배정 · {FormatRemoteStaffClock(remaining)}에서 정지";
        }
        else if (timer.HasStarted && timer.StartedAtUtc is null && remaining > TimeSpan.Zero)
        {
            timer.StartedAtUtc = now;
            _status.Text = $"{timer.Name} 직원 배정 · 남은 시간부터 재시작";
        }
        else
        {
            timer.HasStarted = true;
            timer.RemainingSeconds = duration.TotalSeconds;
            timer.StartedAtUtc = now;
            _status.Text = $"{timer.Name} 직원 배정 · {duration.TotalMinutes:0}분 타이머 시작";
        }
        ConfigStore.UpdateRemoteStaffTimers(_config.RemoteStaffTimers, _config.BusinessCharacterGeneration);
        RefreshRemoteStaffTimers(now);
        ScheduleAutomaticApply();
    }

    private void ToggleRemoteStaffMultiplier(string key, int multiplier)
    {
        var timer = _config.RemoteStaffTimers.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (timer is null) return;
        var now = DateTimeOffset.UtcNow;
        var oldDuration = GetRemoteStaffDuration(timer);
        var oldRemaining = GetRemoteStaffRemaining(timer, now);
        var wasRunning = timer.StartedAtUtc is not null && oldRemaining > TimeSpan.Zero;
        timer.SpeedMultiplier = timer.SpeedMultiplier == multiplier ? 1 : multiplier;
        if (timer.HasStarted)
        {
            var newDuration = GetRemoteStaffDuration(timer);
            var remainingRatio = oldDuration.TotalSeconds <= 0d
                ? 0d
                : Math.Clamp(oldRemaining.TotalSeconds / oldDuration.TotalSeconds, 0d, 1d);
            timer.RemainingSeconds = newDuration.TotalSeconds * remainingRatio;
            timer.StartedAtUtc = wasRunning && timer.RemainingSeconds > 0d ? now : null;
        }
        ConfigStore.UpdateRemoteStaffTimers(_config.RemoteStaffTimers, _config.BusinessCharacterGeneration);
        RefreshRemoteStaffTimers(now);
        _status.Text = $"{timer.Name} 직원 배정 · 생산 시간 {timer.SpeedMultiplier}×";
        ScheduleAutomaticApply();
    }

    private void ResetRemoteStaffTimer(string key)
    {
        var timer = _config.RemoteStaffTimers.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (timer is null) return;
        timer.HasStarted = false;
        timer.StartedAtUtc = null;
        timer.RemainingSeconds = 0d;
        ConfigStore.UpdateRemoteStaffTimers(_config.RemoteStaffTimers, _config.BusinessCharacterGeneration);
        RefreshRemoteStaffTimers(DateTimeOffset.UtcNow);
        _status.Text = $"{timer.Name} 직원 배정 · 타이머 초기화";
        ScheduleAutomaticApply();
    }

    private void NightclubSafeCashChanged()
    {
        if (_loadingBusiness || _nightclubSafeCash is null) return;
        _config.NightclubSafe.Cash = (int)_nightclubSafeCash.Value;
        _businessSelectionDirty = true;
        RefreshNightclubSafeControls();
    }

    private void ChangeNightclubPopularity(int delta)
    {
        _config.NightclubSafe.PopularityPercent = Math.Clamp(
            _config.NightclubSafe.PopularityPercent + delta,
            0,
            100);
        // 직접 인기도를 손대면 다음 감소는 처음부터 센다.
        _config.NightclubSafe.PopularityDecayCarry = 0d;
        _businessSelectionDirty = true;
        RefreshNightclubSafeControls();
        _status.Text = $"나이트클럽 인기도 · {_config.NightclubSafe.PopularityPercent}%";
        ScheduleAutomaticApply();
    }

    private void ToggleNightclubSafeTimer()
    {
        var state = _config.NightclubSafe;
        if (!state.HasStarted)
        {
            state.HasStarted = true;
            state.IsPaused = false;
            state.RemainingSeconds = NightclubSafeCalculator.GetCycleDuration(state).TotalSeconds;
            _status.Text = "나이트클럽 금고 · 수익 타이머 시작";
        }
        else
        {
            state.IsPaused = !state.IsPaused;
            _status.Text = state.IsPaused
                ? "나이트클럽 금고 · 타이머 정지"
                : "나이트클럽 금고 · 남은 시간부터 재시작";
        }
        _businessSelectionDirty = true;
        RefreshNightclubSafeControls();
        ScheduleAutomaticApply();
    }

    private void ResetNightclubSafeTimer()
    {
        var state = _config.NightclubSafe;
        state.HasStarted = false;
        state.IsPaused = false;
        state.RemainingSeconds = NightclubSafeCalculator.GetCycleDuration(state).TotalSeconds;
        state.PopularityDecayCarry = 0d;
        _businessSelectionDirty = true;
        RefreshNightclubSafeControls();
        _status.Text = "나이트클럽 금고 · 타이머 초기화";
        ScheduleAutomaticApply();
    }

    private void ToggleNightclubSafeMultiplier(int multiplier)
    {
        var state = _config.NightclubSafe;
        var oldDuration = NightclubSafeCalculator.GetCycleDuration(state);
        var oldRemaining = TimeSpan.FromSeconds(Math.Clamp(
            state.RemainingSeconds,
            0d,
            oldDuration.TotalSeconds));
        state.SpeedMultiplier = state.SpeedMultiplier == multiplier ? 1 : multiplier;
        var newDuration = NightclubSafeCalculator.GetCycleDuration(state);
        var remainingRatio = oldDuration.TotalSeconds <= 0d
            ? 0d
            : Math.Clamp(oldRemaining.TotalSeconds / oldDuration.TotalSeconds, 0d, 1d);
        state.RemainingSeconds = newDuration.TotalSeconds * remainingRatio;
        if (!state.HasStarted) state.RemainingSeconds = newDuration.TotalSeconds;
        _businessSelectionDirty = true;
        RefreshNightclubSafeControls();
        _status.Text = $"나이트클럽 금고 · 수익 시간 {state.SpeedMultiplier}×";
        ScheduleAutomaticApply();
    }

    private void RefreshNightclubSafeControls()
    {
        if (_nightclubSafeCash is null
            || _nightclubPopularityValue is null
            || _nightclubSafeIncomeValue is null
            || _nightclubSafeTimerButton is null)
            return;

        var state = _config.NightclubSafe;
        var previousLoading = _loadingBusiness;
        _loadingBusiness = true;
        try
        {
            _nightclubSafeCash.Value = Math.Clamp(
                state.Cash,
                (int)_nightclubSafeCash.Minimum,
                (int)_nightclubSafeCash.Maximum);
        }
        finally { _loadingBusiness = previousLoading; }

        _nightclubPopularityValue.Text = $"{state.PopularityPercent}%";
        _nightclubSafeIncomeValue.Text = $"다음 수익 {FormatMoney(NightclubSafeCalculator.GetIncome(state.PopularityPercent))}";
        UpdateNightclubSafeMultiplierAppearance(state.SpeedMultiplier);
        var duration = NightclubSafeCalculator.GetCycleDuration(state);
        var remaining = TimeSpan.FromSeconds(Math.Clamp(state.RemainingSeconds, 0d, duration.TotalSeconds));
        if (!state.HasStarted)
        {
            _nightclubSafeTimerButton.Text = $"금고 · 시작 ({FormatRemoteStaffClock(duration)})";
            _nightclubSafeTimerButton.BackColor = FieldBackground;
            _nightclubSafeTimerButton.FlatAppearance.BorderColor = Color.FromArgb(70, 255, 255, 255);
            _businessTooltips.SetToolTip(_nightclubSafeTimerButton, "누르면 48분 수익 주기 시작");
        }
        else if (state.IsPaused)
        {
            _nightclubSafeTimerButton.Text = $"금고 · 정지 {FormatRemoteStaffClock(remaining)}";
            _nightclubSafeTimerButton.BackColor = Color.FromArgb(64, 130, 88, 38);
            _nightclubSafeTimerButton.FlatAppearance.BorderColor = Color.FromArgb(135, 235, 164, 82);
            _businessTooltips.SetToolTip(_nightclubSafeTimerButton, "정지됨 · 누르면 남은 시간부터 재시작");
        }
        else
        {
            _nightclubSafeTimerButton.Text = $"금고 · {FormatRemoteStaffClock(remaining)}";
            _nightclubSafeTimerButton.BackColor = ActiveBackground;
            _nightclubSafeTimerButton.FlatAppearance.BorderColor = ActiveBorder;
            _businessTooltips.SetToolTip(_nightclubSafeTimerButton, "진행 중 · 누르면 현재 남은 시간에서 정지");
        }
    }

    private void UpdateNightclubSafeMultiplierAppearance(int selectedMultiplier)
    {
        foreach (var (multiplier, button) in _nightclubSafeMultiplierButtons)
        {
            var selected = multiplier == selectedMultiplier;
            button.BackColor = selected ? Accent : FieldBackground;
            button.FlatAppearance.BorderColor = selected
                ? Color.FromArgb(150, 150, 162, 255)
                : Color.FromArgb(70, 255, 255, 255);
        }
    }

    private void RefreshRemoteStaffTimers(DateTimeOffset now)
    {
        foreach (var timer in _config.RemoteStaffTimers)
        {
            if (!_remoteStaffTimerButtons.TryGetValue(timer.Key, out var button)) continue;
            var multiplier = timer.SpeedMultiplier is >= 2 and <= 4 ? timer.SpeedMultiplier : 1;
            var duration = GetRemoteStaffDuration(timer);
            UpdateRemoteStaffMultiplierAppearance(timer.Key, multiplier);
            if (!timer.HasStarted)
            {
                button.Text = $"{timer.Name} · 시작 ({duration.TotalMinutes:0}:00)";
                button.BackColor = FieldBackground;
                button.FlatAppearance.BorderColor = Color.FromArgb(70, 255, 255, 255);
                _businessTooltips.SetToolTip(button, "누르면 카운트다운 시작");
                continue;
            }

            var remaining = GetRemoteStaffRemaining(timer, now);
            if (timer.StartedAtUtc is not null && remaining > TimeSpan.Zero)
            {
                button.Text = $"{timer.Name} · {FormatRemoteStaffClock(remaining)}";
                button.BackColor = ActiveBackground;
                button.FlatAppearance.BorderColor = ActiveBorder;
                _businessTooltips.SetToolTip(button, "진행 중 · 누르면 현재 남은 시간에서 정지");
            }
            else if (remaining > TimeSpan.Zero)
            {
                button.Text = $"{timer.Name} · 정지 {FormatRemoteStaffClock(remaining)}";
                button.BackColor = Color.FromArgb(64, 130, 88, 38);
                button.FlatAppearance.BorderColor = Color.FromArgb(135, 235, 164, 82);
                _businessTooltips.SetToolTip(button, "정지됨 · 누르면 남은 시간부터 재시작");
            }
            else
            {
                button.Text = $"{timer.Name} · 완료 · 다시 시작";
                button.BackColor = ActiveBackground;
                button.FlatAppearance.BorderColor = ActiveBorder;
                _businessTooltips.SetToolTip(button, $"{duration.TotalMinutes:0}분 완료 · 누르면 다시 시작");
            }
        }
    }

    private static TimeSpan GetRemoteStaffDuration(RemoteStaffTimerEntry timer)
    {
        var multiplier = timer.SpeedMultiplier is >= 2 and <= 4 ? timer.SpeedMultiplier : 1;
        return TimeSpan.FromMinutes(RemoteStaffTimerEntry.DurationMinutes / (double)multiplier);
    }

    private static TimeSpan GetRemoteStaffRemaining(RemoteStaffTimerEntry timer, DateTimeOffset now)
    {
        if (!timer.HasStarted) return GetRemoteStaffDuration(timer);
        var stored = TimeSpan.FromSeconds(Math.Max(0d, timer.RemainingSeconds));
        return timer.StartedAtUtc is { } started ? stored - (now - started) : stored;
    }

    private static string FormatRemoteStaffClock(TimeSpan value)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    private void UpdateRemoteStaffMultiplierAppearance(string key, int selectedMultiplier)
    {
        if (!_remoteStaffMultiplierButtons.TryGetValue(key, out var buttons)) return;
        foreach (var (multiplier, button) in buttons)
        {
            var selected = multiplier == selectedMultiplier;
            button.BackColor = selected ? Accent : FieldBackground;
            button.FlatAppearance.BorderColor = selected
                ? Color.FromArgb(150, 150, 162, 255)
                : Color.FromArgb(70, 255, 255, 255);
        }
    }
}
