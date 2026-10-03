using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 04 사업장 설정 / 05 사업장 현황 페이지 구성, 사업장 카드와 카드 안의 버튼(판매·보급·생산 2×·일일 부스트·단축키).
internal sealed partial class ConfigForm
{

    private CheckBox? _nightclubCardToggle;
    private Control? _nightclubStatusCard;

    /// <summary>체크한 사업장 카드만 두 칸 격자에 다시 채운다.</summary>
    private void ArrangeProductionCards()
    {
        if (_productionGrid is null) return;
        var visible = _productionOrder
            .Where(key => _businessEditors.TryGetValue(key, out var editor) && editor.Enabled.Checked)
            .ToList();
        _productionGrid.SuspendLayout();
        _productionGrid.Controls.Clear();
        _productionGrid.RowStyles.Clear();
        var rowCount = Math.Max(1, (visible.Count + 1) / 2);
        _productionGrid.RowCount = rowCount;
        for (var row = 0; row < rowCount; row++)
            _productionGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var card in _productionCards.Values) card.Visible = false;
        for (var index = 0; index < visible.Count; index++)
        {
            var card = _productionCards[visible[index]];
            card.Visible = true;
            _productionGrid.Controls.Add(card, index % 2, index / 2);
        }
        _productionGrid.ResumeLayout(true);
        ArrangeBusinessSettingRows(visible);
        RefreshBusinessHudTargets();
    }

    private void ProductionSelectionChanged(string key)
    {
        ArrangeProductionCards();
        if (_loadingBusiness) return;
        if (_businessEditors.TryGetValue(key, out var editor)) RefreshBusinessEditorPreview(editor);
        _businessSelectionDirty = true;
        UpdateLiveBusinessState(DateTimeOffset.UtcNow, forceSave: true);
        ScheduleAutomaticApply();
    }

    /// <summary>
    /// 04 사업장 설정. 캐릭터 선택과 한 번 정하면 잘 바꾸지 않는 항목만 모은다.
    /// 캐릭터를 바꾸면 05 사업장 현황도 그 캐릭터 상태로 바뀐다.
    /// </summary>
    private Control BuildBusinessSettingsPanel()
    {
        var group = CreateGroup("사업장 설정");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(1, 1, 1, 10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 캐릭터는 이 페이지에만 두는 대신 한눈에 보이도록 강조 카드에 크게 둔다.
        var hero = new ToolkitBusinessCard("현재 캐릭터")
        {
            Highlighted = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        var heroRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        heroRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heroRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heroRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        heroRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _businessCharacter.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        _businessCharacter.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _businessCharacter.Margin = new Padding(0, 0, 10, 0);
        _businessTooltips.SetToolTip(_businessCharacter,
            "고르면 지금 캐릭터의 사업장 상태를 저장하고 선택한 캐릭터의 상태로 바꿉니다. "
            + "GTA는 접속한 캐릭터의 사업장만 생산하므로 다른 캐릭터의 재고와 타이머는 멈춰 있습니다.");
        _businessCharacter.SelectedIndexChanged += (_, _) => BusinessCharacterSelectionChanged();
        heroRow.Controls.Add(_businessCharacter, 0, 0);
        var characterButtons = new SettingsFlowPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = Color.Transparent
        };
        characterButtons.Controls.Add(CreateButton("추가", (_, _) => AddBusinessCharacterFromUi(), secondary: true, width: 70));
        characterButtons.Controls.Add(CreateButton("이름", (_, _) => RenameBusinessCharacterFromUi(), secondary: true, width: 70));
        characterButtons.Controls.Add(CreateButton("삭제", (_, _) => RemoveBusinessCharacterFromUi(), secondary: true, width: 70));
        heroRow.Controls.Add(characterButtons, 1, 0);
        var heroHint = CreateInlineLabel("바꾸면 05 사업장 현황의 재고 · 보급 · 타이머도 이 캐릭터 것으로 바뀝니다.");
        heroHint.ForeColor = Muted;
        heroHint.AutoEllipsis = true;
        heroHint.Margin = new Padding(0, 2, 0, 0);
        heroHint.Font = new Font("Segoe UI", 8.2f, FontStyle.Regular, GraphicsUnit.Point);
        heroRow.Controls.Add(heroHint, 0, 1);
        heroRow.SetColumnSpan(heroHint, 2);
        hero.Controls.Add(heroRow);
        root.Controls.Add(hero, 0, 0);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 4,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hudSelection = BuildBusinessHudSelection();
        header.Controls.Add(hudSelection, 0, 1); header.SetColumnSpan(hudSelection, 4);
        header.Controls.Add(CreateInlineLabel("HUD 표시 대상"), 0, 0);
        _businessHudTarget.Dock = DockStyle.Fill;
        _businessHudTarget.Margin = new Padding(0, 2, 8, 2);
        _businessHudTargetCount.Items.AddRange(["선택한 사업장 하나", "선택한 사업장 두 개"]);
        _businessHudTargetCount.Dock = DockStyle.Fill;
        _businessHudTargetCount.Margin = new Padding(0, 4, 16, 4);
        header.Controls.Add(_businessHudTargetCount, 1, 0);
        header.Controls.Add(CreateInlineLabel("HUD 표시 모드"), 2, 0);
        _businessDisplayMode.Items.AddRange(["컴팩트", "일반", "확장"]);
        _businessDisplayMode.Dock = DockStyle.Fill;
        _businessDisplayMode.Margin = new Padding(0, 4, 0, 4);
        header.Controls.Add(_businessDisplayMode, 3, 0);

        _showBusinessHud.Appearance = Appearance.Button;
        _showBusinessHud.AutoSize = false;
        _showBusinessHud.Dock = DockStyle.Fill;
        _showBusinessHud.FlatStyle = FlatStyle.Flat;
        _showBusinessHud.TextAlign = ContentAlignment.MiddleCenter;
        _showBusinessHud.Margin = new Padding(0, 2, 8, 2);
        _showBusinessHud.Tag = "business-hud-visibility";
        header.Controls.Add(_showBusinessHud, 0, 2);

        var readRow = new SettingsFlowPanel { Dock=DockStyle.Fill, WrapContents=false, Margin=new Padding(0), Padding=new Padding(4,5,0,0), BackColor=Color.Transparent };
        readRow.Controls.Add(CreateFlowLabel("화면 판독 단축키"));
        _readBusinessScreenHotkey.Width = 96;
        _readBusinessScreenHotkey.Margin = new Padding(8, 2, 0, 0);
        readRow.Controls.Add(_readBusinessScreenHotkey);
        _readScreenSound.Margin = new Padding(12, 5, 0, 0);
        _readScreenResupply.Margin = new Padding(12, 5, 0, 0);
        readRow.Controls.Add(_readScreenSound);
        readRow.Controls.Add(_readScreenResupply);
        header.Controls.Add(readRow, 1, 2);
        header.SetColumnSpan(readRow, 3);

        var alertRow = new SettingsFlowPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            MinimumSize = new Size(0, 42),
            Margin = new Padding(0, 4, 0, 8),
            Padding = new Padding(0, 3, 0, 3),
            BackColor = Color.Transparent
        };
        alertRow.Controls.Add(CreateFlowLabel("알림"));
        foreach (var box in new[] { _alertSupplyLow, _alertStockFull, _alertBoostEnded, _alertSound })
        {
            box.Anchor = AnchorStyles.Left;
            box.Margin = new Padding(8, 5, 0, 0);
            alertRow.Controls.Add(box);
        }
        _alertSupplyMinutes.Width = 52;
        _alertSupplyMinutes.Margin = new Padding(10, 2, 2, 0);
        alertRow.Controls.Add(_alertSupplyMinutes);
        alertRow.Controls.Add(CreateFlowLabel("분 전"));
        alertRow.Controls.Add(CreateButton("소리 확인", (_, _) => NotificationSound.Play(), secondary: true, width: 78));
        header.Controls.Add(alertRow, 0, 3);
        header.SetColumnSpan(alertRow, 4);
        _businessTooltips.SetToolTip(_readScreenResupply, "판독 성공 시 인식한 사업장의 보급 도착 타이머를 시작합니다. 이미 배송 중이면 유지합니다. 나이트클럽에는 적용하지 않습니다.");
        _businessTooltips.SetToolTip(_alertSupplyLow, "보급이 바닥나기 정해진 시간 전에 한 번 알립니다. 보급을 채우면 다시 알립니다.");
        _businessTooltips.SetToolTip(_alertStockFull, "재고가 가득 차면 알립니다. 이 시점부터 생산분은 버려집니다.");
        _businessTooltips.SetToolTip(_alertBoostEnded, "멘션 부스트나 LSD 일일 부스트가 끝나면 알립니다.");
        _businessTooltips.SetToolTip(_alertSound, "알림에 소리를 함께 냅니다.");
        _businessTooltips.SetToolTip(_readBusinessScreenHotkey,
            "GTA 사업장 노트북 화면을 띄운 상태에서 누르면 막대를 읽어 인식된 사업장의 재고와 보급에 넣습니다. "
            + "벙커·LSD 연구소의 어두운 화면과 오토바이 클럽 사업장의 밝은 화면을 모두 읽습니다. "
            + "화면을 복사해 색만 판독하며, 게임에는 아무것도 보내지 않습니다. "
            + "전체화면(독점) 모드에서는 읽을 수 없으니 테두리 없는 창모드를 쓰세요.");
        _showBusinessHud.CheckedChanged += (_, _) => UpdateBusinessHudVisibilityAppearance();
        root.Controls.Add(header, 0, 1);

        // 사업장 고르기. 체크한 사업장만 05 현황에 카드가 뜨고 HUD에도 나온다.
        // 체크 상자는 05 카드를 만들 때 함께 채운다.
        _businessSelectionRow = new SettingsFlowPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            AutoSize = true,
            MinimumSize = new Size(0, 42),
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(0, 5, 0, 5),
            BackColor = Color.Transparent
        };
        _businessSelectionRow.Controls.Add(CreateFlowLabel("현황 카드 선택"));
        root.Controls.Add(_businessSelectionRow, 0, 2);

        var details = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var equipment = new ToolkitBusinessCard("사업장별 장비 · 직원")
        {
            Dock = DockStyle.Top,
            Height = 60,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0)
        };
        _businessSettingsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _businessSettingsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _businessSettingsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _businessSettingsHeading = CreateBusinessSettingRow(
            CreateBusinessSettingHeading("사업장"),
            CreateBusinessSettingHeading("장비 설정"),
            CreateBusinessSettingHeading("직원 배정"),
            CreateBusinessSettingHeading("HUD"));
        _businessSettingsHeading.Height = 26;
        _businessSettingsGrid.Controls.Add(_businessSettingsHeading, 0, 0);
        _businessSettingsNote = CreateInlineLabel("나이트클럽 기술자 배정과 원거리 직원 타이머는 05 사업장 현황에 있습니다.");
        _businessSettingsNote.Dock = DockStyle.Fill;
        _businessSettingsNote.Height = 30;
        _businessSettingsNote.ForeColor = Muted;
        _businessSettingsNote.Font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        equipment.Controls.Add(_businessSettingsGrid);
        details.Controls.Add(BuildBusinessHudOptions(), 0, 0);
        details.Controls.Add(equipment, 0, 1);
        root.Controls.Add(details, 0, 3);
        // One page scroll keeps wrapped option rows reachable at smaller window sizes.
        var pageScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        pageScroll.Controls.Add(root);
        group.Controls.Add(pageScroll);
        return group;
    }

    private static Label CreateBusinessSettingHeading(string text)
    {
        var label = CreateInlineLabel(text);
        label.ForeColor = Color.FromArgb(193, 205, 218, 230);
        label.Font = new Font("Segoe UI", 8.2f, FontStyle.Bold, GraphicsUnit.Point);
        return label;
    }

    /// <summary>04 사업장별 설정 한 줄: 이름 · 장비 · 직원 · HUD 가득 타이머.</summary>
    private static TableLayoutPanel CreateBusinessSettingRow(Control name, Control tier, Control staff, Control hud)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 36,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.Controls.Add(name, 0, 0);
        row.Controls.Add(tier, 1, 0);
        row.Controls.Add(staff, 2, 0);
        row.Controls.Add(hud, 3, 0);
        return row;
    }

    /// <summary>체크한 사업장만 04 설정 목록에 다시 채운다.</summary>
    private void ArrangeBusinessSettingRows(IReadOnlyList<string> visible)
    {
        if (_businessSettingsGrid is null || _businessSettingsHeading is null || _businessSettingsNote is null) return;
        _businessSettingsGrid.SuspendLayout();
        _businessSettingsGrid.Controls.Clear();
        _businessSettingsGrid.RowStyles.Clear();
        _businessSettingsGrid.RowCount = visible.Count + 2;
        for (var row = 0; row < visible.Count + 2; row++)
            _businessSettingsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _businessSettingsGrid.Controls.Add(_businessSettingsHeading, 0, 0);
        foreach (var row in _productionSettingRows.Values) row.Visible = false;
        for (var index = 0; index < visible.Count; index++)
        {
            if (!_productionSettingRows.TryGetValue(visible[index], out var row)) continue;
            row.Visible = true;
            _businessSettingsGrid.Controls.Add(row, 0, index + 1);
        }
        _businessSettingsGrid.Controls.Add(_businessSettingsNote, 0, visible.Count + 1);
        _businessSettingsGrid.ResumeLayout(true);
    }

    /// <summary>05 사업장 현황. 게임하면서 보고 만지는 재고 · 보급 · 부스트 · 타이머.</summary>
    private Control BuildBusinessPanel()
    {
        var group = CreateGroup("사업장 · 재고 / 보급");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(1)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        _businessOnline.Appearance = Appearance.Button;
        _businessOnline.AutoSize = false;
        _businessOnline.Dock = DockStyle.Fill;
        _businessOnline.FlatStyle = FlatStyle.Flat;
        _businessOnline.TextAlign = ContentAlignment.MiddleCenter;
        _businessOnline.Margin = new Padding(0, 2, 0, 2);
        _businessOnline.Tag = "business-global-online";
        var onlineRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 8, 0) };
        onlineRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        onlineRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        onlineRow.Controls.Add(_businessOnline, 0, 0);
        _businessAutoOnline.Appearance = Appearance.Normal;
        _businessAutoOnline.AutoSize = true;
        _businessAutoOnline.Anchor = AnchorStyles.Left;
        _businessAutoOnline.Margin = new Padding(10, 0, 0, 0);
        _businessAutoOnline.Text = "자동 체크";
        onlineRow.Controls.Add(_businessAutoOnline, 1, 0);
        header.Controls.Add(onlineRow, 0, 0);
        header.SetColumnSpan(onlineRow, 2);
        _businessTooltips.SetToolTip(_businessAutoOnline, "gta5_Enhanced.exe 실행이 1분 유지되면 온라인, 종료 감지 시 바로 오프라인. 게임 내부 세션 접속 여부를 읽지는 않습니다.");
        _businessAutoOnline.CheckedChanged += (_, _) =>
        {
            _businessAutoOnline.Text = "자동 체크";
            _businessOnline.Enabled = !_businessAutoOnline.Checked;
            if (_loadingBusiness) return;
            _autoOnlineTracker.Reset();
            _config.BusinessAutoOnline = _businessAutoOnline.Checked;
            ConfigStore.UpdateAutomaticOnline(_businessAutoOnline.Checked);
        };
        var onlineHint = CreateInlineLabel("보급은 온라인 + GTA 실행 중 10분 뒤 도착");
        onlineHint.ForeColor = Muted;
        onlineHint.AutoEllipsis = true;
        onlineHint.Font = new Font("Segoe UI", 8.2f, FontStyle.Regular, GraphicsUnit.Point);
        header.Controls.Add(onlineHint, 2, 0);
        header.SetColumnSpan(onlineHint, 2);
        _businessTooltips.SetToolTip(onlineHint, "보급은 온라인 + GTA 실행 시간 10분 뒤 도착합니다. 오프라인이면 멈췄다가 다시 온라인이 되면 이어집니다.");

        header.Controls.Add(CreateInlineLabel("멘션 부스트 사업장"), 0, 1);
        _activeSupplyBusiness.Dock = DockStyle.Fill;
        _activeSupplyBusiness.Margin = new Padding(0, 2, 8, 2);
        foreach (var profile in MansionBoostProfiles)
            _activeSupplyBusiness.Items.Add(profile.Name);
        header.Controls.Add(_activeSupplyBusiness, 1, 1);

        _mansionBoostToggle.Dock = DockStyle.Fill;
        _mansionBoostToggle.FlatStyle = FlatStyle.Flat;
        _mansionBoostToggle.Margin = new Padding(0, 2, 8, 2);
        _mansionBoostToggle.ForeColor = Foreground;
        _mansionBoostToggle.Font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Point);
        _mansionBoostToggle.Cursor = Cursors.Hand;
        _mansionBoostToggle.Click += (_, _) => ToggleSelectedMansionBoost();
        header.Controls.Add(_mansionBoostToggle, 2, 1);

        _mansionBoostEndsAt.Dock = DockStyle.Fill;
        _mansionBoostEndsAt.TextAlign = ContentAlignment.MiddleLeft;
        _mansionBoostEndsAt.ForeColor = Muted;
        _mansionBoostEndsAt.AutoEllipsis = true;
        _mansionBoostEndsAt.Font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        var boostTimeRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};
        boostTimeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        boostTimeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,82));
        boostTimeRow.Controls.Add(_mansionBoostEndsAt,0,0);
        var editBoost=CreateButton("시간 입력",(_,_)=>EditMansionBoostRemaining(),secondary:true,width:80);
        editBoost.Dock=DockStyle.Fill;editBoost.Margin=new Padding(3,2,0,2);
        boostTimeRow.Controls.Add(editBoost,1,0);header.Controls.Add(boostTimeRow,3,1);

        _businessOnline.CheckedChanged += (_, _) => BusinessOnlineChanged();
        _showVinewoodTimerPopup.CheckedChanged += (_, _) => UpdateVinewoodTimerPopupAppearance();
        root.Controls.Add(header, 0, 0);

        var boostNotice = CreateInlineLabel("멘션 부스트: 한 사업장 · 실제 시각 24시간 · 게임을 꺼도 종료 타이머 유지");
        boostNotice.Font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        boostNotice.ForeColor = Color.FromArgb(180, 198, 218, 255);
        root.Controls.Add(boostNotice, 0, 1);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 1, 0, 0) };
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var primary = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 9),
            Padding = new Padding(0)
        };
        primary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        primary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        // 벙커 / LSD 연구소에 MC 사업장 5종까지, 체크한 것만 두 칸씩 채운다.
        // 자리를 비워 두면 구멍이 생기므로 켤 때마다 다시 배치한다.
        // 배율 원본을 기록하려면 카드가 만들어진 시점에 모두 트리 안에 있어야
        // 한다. 여기서 숨겨 버리면 UiScaler 가 설계 값을 못 잡아 4K에서 다시
        // 잘린다. 실제 선택 반영은 저장본을 읽은 뒤 PopulateBusinessEditors 가
        // 부르는 ArrangeProductionCards 가 한다. 04 설정 줄도 같은 이유로 모두
        // 넣어 둔 채 시작한다.
        _productionGrid = primary;
        var trackedProfiles = BusinessSupplyCatalog.TrackedProductionProfiles.ToArray();
        primary.RowCount = (trackedProfiles.Length + 1) / 2;
        for (var row = 0; row < primary.RowCount; row++)
            primary.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (_businessSettingsGrid is not null && _businessSettingsNote is not null)
        {
            _businessSettingsGrid.RowCount = trackedProfiles.Length + 2;
            for (var row = 1; row < trackedProfiles.Length + 2; row++)
                _businessSettingsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        for (var index = 0; index < trackedProfiles.Length; index++)
        {
            var profile = trackedProfiles[index];
            var toggle = new SettingsCheckBox
            {
                Text = profile.Name,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(10, 4, 0, 0),
                ForeColor = Foreground
            };
            _businessTooltips.SetToolTip(
                toggle,
                $"{profile.Name}의 재고와 보급을 추적합니다. 끄면 05 사업장 현황의 카드와 HUD 표시가 모두 사라집니다.");
            _businessSelectionRow?.Controls.Add(toggle);
            _productionOrder.Add(profile.Key);
            var card = CreateProductionBusinessCard(profile, toggle);
            _productionCards[profile.Key] = card;
            primary.Controls.Add(card, index % 2, index / 2);
            if (_businessSettingsGrid is not null && _productionSettingRows.TryGetValue(profile.Key, out var settingRow))
                _businessSettingsGrid.Controls.Add(settingRow, 0, index + 1);
            var key = profile.Key;
            toggle.CheckedChanged += (_, _) => ProductionSelectionChanged(key);
        }
        if (_businessSettingsGrid is not null && _businessSettingsNote is not null)
            _businessSettingsGrid.Controls.Add(_businessSettingsNote, 0, trackedProfiles.Length + 1);
        rows.Controls.Add(primary, 0, 0);
        rows.Controls.Add(CreateRemoteStaffTimerCard(), 0, 1);
        _nightclubStatusCard=CreateNightclubBusinessCard();
        rows.Controls.Add(_nightclubStatusCard, 0, 2);
        _nightclubCardToggle=new SettingsCheckBox { Text="나이트클럽",AutoSize=true,Anchor=AnchorStyles.Left,Margin=new Padding(10,4,0,0),ForeColor=Foreground,Checked=true };
        _businessTooltips.SetToolTip(_nightclubCardToggle,"나이트클럽 현황 카드를 표시하거나 숨깁니다. 기술자 배정과 재고 추적은 유지됩니다.");
        _businessSelectionRow?.Controls.Add(_nightclubCardToggle);
        _nightclubCardToggle.CheckedChanged+=(_,_)=>
        {
            _nightclubStatusCard.Visible=_nightclubCardToggle.Checked;
            if(_loadingBusiness)return;
            try
            {
                ConfigStore.UpdateNightclubCardVisibility(_nightclubCardToggle.Checked);
                _config.ShowNightclubStatusCard=_nightclubCardToggle.Checked;
                ScheduleAutomaticApply();
            }
            catch(Exception ex)
            {
                AppLog.Error("나이트클럽 카드 표시 저장 실패",ex);
                _loadingBusiness=true;
                try{_nightclubCardToggle.Checked=_config.ShowNightclubStatusCard;}
                finally{_loadingBusiness=false;}
            }
        };
        scroll.Controls.Add(rows);
        root.Controls.Add(scroll, 0, 2);
        _activeSupplyBusiness.SelectedIndexChanged += (_, _) => ActiveSupplyBusinessChanged();
        group.Controls.Add(root);
        return group;
    }

    private Control CreateProductionBusinessCard(BusinessSupplyProfile profile, CheckBox enabled)
    {
        var card = new ToolkitBusinessCard(profile.Name)
        {
            Height = 220,
            Margin = new Padding(4, 0, 4, 12)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var tier = new WheelLockedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(2, 1, 0, 1) };
        string[] tierOptions = profile.Category == BusinessCategory.AcidLab
            ? ["기본 장비", "장비 업그레이드"]
            : ["기본", "직원 또는 장비", "직원+장비"];
        tier.Items.AddRange(tierOptions);
        var staffAssignment = new WheelLockedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(2, 1, 0, 1) };
        // 오토바이 클럽 사업장에는 배정할 인력 개념이 없다.
        string[] staffOptions = profile.Category switch
        {
            BusinessCategory.Bunker => ["제조", "제조 + 연구", "연구"],
            BusinessCategory.AcidLab => ["Mutt · 원격 보급 가능"],
            _ => ["해당 없음"]
        };
        staffAssignment.Items.AddRange(staffOptions);
        if (profile.Category != BusinessCategory.Bunker) staffAssignment.Enabled = false;
        var stock = CreateBusinessMoneyInput(profile, $"{profile.Name} 현재 재고 금액");
        if (profile.Category == BusinessCategory.MotorcycleClub) _businessTooltips.SetToolTip(stock, "직원+장비 업그레이드, 먼 지역 판매 기준 환산 금액입니다. 이벤트·공개 세션·추가 사업장 보너스는 제외합니다.");
        _businessTooltips.SetToolTip(
            stock,
            "이 칸을 클릭한 뒤 휠로 조정할 수 있어. 재고를 올리면 생산 비율만큼 보급도 즉시 줄어들어.");
        var stockBar = new StockMeter { Dock = DockStyle.Fill, Margin = new Padding(4, 4, 2, 4), AccentColor = Accent };
        var supplyBar = CreateScaleSlider(0, $"{profile.Name} 보급", 100);
        supplyBar.SmallChange = 1;
        var supplyValue = CreateSliderValueLabel();
        var resupply = CreateButton("보급", (_, _) =>
        {
            var editor = _businessEditors[profile.Key];
            RequestSupplyDelivery(editor);
        }, secondary: true, width: 92);
        resupply.BackColor = SupplyBackground;
        resupply.FlatAppearance.BorderColor = SupplyBorder;
        resupply.Dock = DockStyle.Fill;
        resupply.Margin = new Padding(0, 1, 5, 1);
        var state = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Muted,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.1f, FontStyle.Regular, GraphicsUnit.Point),
            AutoEllipsis = true
        };
        // 장비 · 직원 · HUD 가득 타이머는 04 사업장 설정의 한 줄로 간다.
        var settingName = CreateInlineLabel(profile.Name);
        settingName.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold, GraphicsUnit.Point);
        _businessTooltips.SetToolTip(staffAssignment, profile.Category switch
        {
            BusinessCategory.Bunker => "벙커 직원 배정",
            BusinessCategory.AcidLab => "LSD 연구소 관리 직원",
            _ => "오토바이 클럽 사업장에는 배정할 인력이 없습니다."
        });
        table.Controls.Add(CreateBusinessSaleButton(profile.Key), 0, 0);
        table.Controls.Add(stock, 1, 0);
        table.Controls.Add(CreateInlineLabel("재고 현황"), 0, 1);
        table.Controls.Add(stockBar, 1, 1);
        table.Controls.Add(WithBusinessHotkey(resupply, "supply:" + profile.Key), 0, 2);
        table.Controls.Add(CreateSliderField(supplyBar, supplyValue), 1, 2);
        table.Controls.Add(profile.SupportsDailyBoost
            ? CreateDailyBoostButton(profile.Key)
            : CreateProductionSpeedButton(profile.Key), 0, 3);
        table.Controls.Add(state, 1, 3);
        table.Controls.Add(CreateInlineLabel(profile.Category == BusinessCategory.MotorcycleClub ? "원거리 금액 기준" : "완충 기준"), 0, 4);
        var forecast = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(190, 204, 215, 230),
            Font = new Font("Segoe UI", 8.1f, FontStyle.Regular, GraphicsUnit.Point),
            AutoEllipsis = true
        };
        var stockFullTimerToggle = new SettingsCheckBox
        {
            Text = "HUD 가득 타이머",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 0, 1, 0),
            ForeColor = Foreground,
            BackColor = Color.Transparent
        };
        _businessTooltips.SetToolTip(stockFullTimerToggle, "켜면 이 사업장 HUD에 현재 재고가 100%가 될 때까지 남은 시간을 표시합니다.");
        table.Controls.Add(forecast, 1, 4);
        tier.Dock = DockStyle.None;
        tier.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        tier.Margin = new Padding(0, 0, 8, 0);
        staffAssignment.Dock = DockStyle.None;
        staffAssignment.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        staffAssignment.Margin = new Padding(0, 0, 8, 0);
        _productionSettingRows[profile.Key] = CreateBusinessSettingRow(settingName, tier, staffAssignment, stockFullTimerToggle);

        var editor = new BusinessEditor(profile, enabled, tier, staffAssignment, stock, stockBar, supplyBar, resupply, state, forecast)
        {
            SupplyValueLabel = supplyValue,
            StockFullTimerToggle = stockFullTimerToggle
        };
        _businessEditors[profile.Key] = editor;
        supplyBar.ValueChanged += (_, _) =>
        {
            supplyValue.Text = $"{supplyBar.Value}%";
            if (_loadingBusiness || editor.Snapshot is null) return;
            var next = editor.Snapshot.CloneNormalized();
            BusinessSupplyCalculator.SetSupplyUnits(next, editor.Profile.SupplyCapacity * supplyBar.Value / 100d);
            next.StateRevision = Math.Max(0, next.StateRevision) + 1;
            editor.Snapshot = next;
            _businessSelectionDirty = true;
            UpdateLiveBusinessState(DateTimeOffset.UtcNow, forceSave: true);
            ScheduleAutomaticApply();
        };
        stock.ValueChanged += (_, _) =>
        {
            if (_loadingBusiness) return;
            editor.StockEdited = true;
            RefreshBusinessEditorPreview(editor);
        };
        stock.WheelValueChanged += (_, change) =>
            ApplyStockWheelSupplyAdjustment(editor, change.PreviousValue, change.CurrentValue);
        tier.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingBusiness) RefreshBusinessEditorPreview(editor);
        };
        staffAssignment.SelectedIndexChanged += (_, _) =>
        {
            if (!_loadingBusiness) RefreshBusinessEditorPreview(editor);
        };
        stockFullTimerToggle.CheckedChanged += (_, _) =>
        {
            if (_loadingBusiness || editor.Snapshot is null) return;
            var next = editor.Snapshot.CloneNormalized();
            next.ShowStockFullTimer = stockFullTimerToggle.Checked;
            next.StateRevision = Math.Max(0, next.StateRevision) + 1;
            editor.Snapshot = next;
            _businessSelectionDirty = true;
            UpdateLiveBusinessState(DateTimeOffset.UtcNow, forceSave: true);
            ScheduleAutomaticApply();
        };
        card.Controls.Add(table);
        return card;
    }

    private static StockNumericUpDown CreateBusinessUnitInput(int maximum, string tooltip) => new()
    {
        Minimum = 0,
        Maximum = Math.Max(1, maximum),
        Increment = 1,
        Dock = DockStyle.Fill,
        Margin = new Padding(4, 6, 2, 4),
        TextAlign = HorizontalAlignment.Center,
        AccessibleName = tooltip
    };

    private static StockNumericUpDown CreateBusinessMoneyInput(BusinessSupplyProfile profile, string tooltip) => new()
    {
        Minimum = 0,
        Maximum = BusinessSupplyCalculator.FullStockValue(profile, "full"),
        Increment = profile.Category == BusinessCategory.Bunker ? 10_500 : 1_000,
        ThousandsSeparator = true,
        DecimalPlaces = 0,
        Dock = DockStyle.Fill,
        Margin = new Padding(4, 6, 2, 4),
        TextAlign = HorizontalAlignment.Right,
        AccessibleName = tooltip
    };

    private CheckBox CreateProductionSpeedButton(string key)
    {
        var button = new SettingsCheckBox { Text = "생산 2×", Appearance = Appearance.Button,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat, Margin = new Padding(2, 1, 2, 1), Font = new Font("Segoe UI", 8f) };
        _productionSpeedButtons[key] = button;
        button.Tag = "production-speed";
        UpdateProductionSpeedAppearance(button);
        _businessTooltips.SetToolTip(button, "HUD 생산 속도를 2배로 계산합니다. 다시 누르면 1배로 돌아갑니다.");
        button.CheckedChanged += (_, _) =>
        {
            UpdateProductionSpeedAppearance(button);
            if (_loadingBusiness || !_businessEditors.TryGetValue(key, out var editor)) return;
            var requested = button.Checked;
            BusinessLiveTimerOnTick(this, EventArgs.Empty);
            if (!_ownsBusinessTracker || editor.Snapshot is null) return;
            var next = editor.Snapshot.CloneNormalized();
            next.ProductionDoubleSpeed = requested;
            next.StateRevision = Math.Max(0, next.StateRevision) + 1;
            editor.Snapshot = next;
            SyncConfigFromBusinessSnapshots();
            ConfigStore.UpdateBusinessTrackingState(_config.BusinessSupplies, SelectedSupplyBusinessKey(), _businessOnline.Checked, _config.NightclubSafe,
                _config.BusinessCharacterGeneration);
            RefreshLiveBusinessEditors(_businessGame.IsGameAlive);
        };
        return button;
    }

    // LSD 연구소 일일 부스트. 수동 2×와 달리 80개 생산 또는 24시간이 지나면
    // 스스로 끝난다. 다시 누르면 진행 중인 부스트를 취소한다.
    private Button CreateDailyBoostButton(string key)
    {
        var button = new Button
        {
            Text = "일일 부스트",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(2, 1, 2, 1),
            Font = new Font("Segoe UI", 8f),
            ForeColor = Foreground,
            BackColor = FieldBackground,
            Tag = "daily-boost"
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(70, 255, 255, 255);
        _dailyBoostButtons[key] = button;
        _businessTooltips.SetToolTip(button,
            $"연구소 안에서 일일 부스트를 켠 순간 누르세요. 생산 2배는 {BusinessSupplyCalculator.DailyBoostUnits}개를 만들거나 "
            + "실제 시각 24시간이 지나면 끝납니다. 진행 중에 누르면 취소합니다.");
        button.Click += (_, _) =>
        {
            if (_loadingBusiness || !_businessEditors.TryGetValue(key, out var editor)) return;
            BusinessLiveTimerOnTick(this, EventArgs.Empty);
            if (!_ownsBusinessTracker || editor.Snapshot is null) return;
            var now = DateTimeOffset.UtcNow;
            var next = editor.Snapshot.CloneNormalized();
            if (BusinessSupplyCalculator.IsDailyBoostActive(next, now))
                BusinessSupplyCalculator.CancelDailyBoost(next);
            else if (!BusinessSupplyCalculator.StartDailyBoost(next, now))
                return;
            next.StateRevision = Math.Max(0, next.StateRevision) + 1;
            editor.Snapshot = next;
            SyncConfigFromBusinessSnapshots();
            ConfigStore.UpdateBusinessTrackingState(_config.BusinessSupplies, SelectedSupplyBusinessKey(), _businessOnline.Checked,
                _config.NightclubSafe, _config.BusinessCharacterGeneration);
            RefreshLiveBusinessEditors(_businessGame.IsGameAlive);
        };
        return button;
    }

    private void UpdateDailyBoostAppearance(Button button, BusinessSupplyProgress progress)
    {
        var active = progress.DailyBoostActive;
        button.Text = active
            ? $"2× · {(int)Math.Ceiling(progress.DailyBoostUnitsRemaining)}개"
            : "일일 부스트";
        button.BackColor = active ? Accent : FieldBackground;
        button.FlatAppearance.BorderColor = active ? ActiveBorder : Color.FromArgb(70, 255, 255, 255);
        if (active && progress.DailyBoostTimeRemaining is { } left)
            _businessTooltips.SetToolTip(button,
                $"생산 2배 진행 중 · 남은 {Math.Ceiling(progress.DailyBoostUnitsRemaining)}개 또는 {FormatDuration(left)} 중 먼저 끝나는 쪽. 누르면 취소합니다.");
    }

    private static void UpdateProductionSpeedAppearance(CheckBox button)
    {
        button.Text = "생산 2×";
        button.BackColor = button.Checked ? Accent : FieldBackground;
        button.ForeColor = Foreground;
        button.FlatAppearance.BorderColor = button.Checked ? ActiveBorder : Color.FromArgb(70, 255, 255, 255);
    }

    private Control WithBusinessHotkey(Button actionButton, string action)
    {
        var row=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,2,4,2)};
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,34));
        var keyButton=CreateButton("키",(_,_)=>EditBusinessHotkey(action),secondary:true,width:28);
        keyButton.Dock=DockStyle.Fill;keyButton.Margin=new Padding(4,1,0,1);keyButton.Padding=Padding.Empty;
        keyButton.Font=new Font("Segoe UI",8f);
        actionButton.Dock=DockStyle.Fill;actionButton.Margin=new Padding(1);
        row.Controls.Add(actionButton,0,0);row.Controls.Add(keyButton,1,0);
        void UpdateHint()
        {
            var value=(_config.BusinessActionHotkeys ?? new()).GetValueOrDefault(action,"NONE");
            keyButton.BackColor=value=="NONE"?FieldBackground:Accent;
            if (IsDisposed || Disposing || keyButton.IsDisposed || !IsHandleCreated || !keyButton.IsHandleCreated
                || !ReferenceEquals(keyButton.FindForm(), this)) return;
            // The guard above still lost a race while a modal dialog (first-run
            // prompt) rebuilt handles: ToolTip threw NullReferenceException into an
            // unhandled-exception dialog (296 test run). The hint is cosmetic and is
            // refreshed on the next click, so never let it take the window down.
            try { _businessTooltips.SetToolTip(keyButton,"단축키: "+(value=="NONE"?"미지정":value)+" · 클릭해서 변경"); }
            catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException or ObjectDisposedException) { }
        }
        // VisibleChanged can fire while the form/control handle hierarchy is being
        // torn down or rebuilt. Creating the shared ToolTip handle from that event
        // caused ToolTip.CreateHandle() to see no top-level control. Refresh the
        // tooltip only once a real button handle exists, and after user edits.
        keyButton.HandleCreated+=(_,_)=>UpdateHint();
        keyButton.Click+=(_,_)=>UpdateHint();
        var value=(_config.BusinessActionHotkeys ?? new()).GetValueOrDefault(action,"NONE");
        keyButton.BackColor=value=="NONE"?FieldBackground:Accent;
        return row;
    }
    private void EditBusinessHotkey(string action)
    {
        _config.BusinessActionHotkeys ??= new();
        var parts=action.Split(':',2);
        var name=parts[1]=="nightclub"?"나이트클럽 전체":_config.BusinessSupplies.FirstOrDefault(e=>e.Key==parts[1])?.Name ?? parts[1];
        using var dialog=new Form {Text=name+" · "+(parts[0]=="sale"?"판매":"보급")+" 단축키",ClientSize=new Size(430,200),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=WindowBackground,ForeColor=Foreground,Font=Font};
        var hint=new Label{Text="아래 칸을 누르고 원하는 키 조합을 입력하세요.\nBackspace: 미지정 · Esc: 이전 값",AutoSize=false,Bounds=new Rectangle(18,16,394,48)};
        var field=new TextBox{ReadOnly=true,ShortcutsEnabled=false,Text=_config.BusinessActionHotkeys.GetValueOrDefault(action,"NONE"),Bounds=new Rectangle(18,72,394,28),BackColor=FieldBackground,ForeColor=Foreground};
        field.Enter+=(_,_)=>{_hotkeyBeforeEdit[field]=field.Text;field.SelectAll();};
        field.PreviewKeyDown+=(_,e)=>e.IsInputKey=true;field.KeyDown+=HotkeyFieldOnKeyDown;
        var status=new Label{Bounds=new Rectangle(18,105,394,36),ForeColor=Accent};
        var save=CreateButton("저장",(_,_)=>
        {
            var key=HotkeyParser.Normalize(field.Text,"NONE");
            var reserved=new[]{_hudToggleHotkey.Text,_exitHotkey.Text,_channelPickerHotkey.Text,_saleStatusHotkey.Text,_vinewoodTimerPopupHotkey.Text,_readBusinessScreenHotkey.Text,"SHIFT+PAGEUP","SHIFT+PAGEDOWN","END"};
            if(!HotkeyParser.TryParse(key,out _)){status.Text="사용할 수 없는 단축키입니다.";return;}
            if(key!="NONE" && (reserved.Contains(key,StringComparer.OrdinalIgnoreCase) || _config.BusinessActionHotkeys.Any(p=>p.Key!=action && p.Value.Equals(key,StringComparison.OrdinalIgnoreCase))))
            {status.Text="이미 사용하는 단축키입니다. 다른 키를 지정하세요.";return;}
            _config.BusinessActionHotkeys[action]=key;ScheduleAutomaticApply();dialog.Close();
        },width:92);save.Location=new Point(220,151);
        var cancel=CreateButton("취소",(_,_)=>dialog.Close(),secondary:true,width:92);cancel.Location=new Point(320,151);
        dialog.Controls.AddRange([hint,field,status,save,cancel]);
        dialog.Shown+=(_,_)=>field.Focus();dialog.ShowDialog(this);_hotkeyBeforeEdit.Remove(field);
    }
    private Control CreateBusinessSaleButton(string? key)
    {
        var button = CreateButton(key is null ? "전체 판매" : "판매", (_, _) => SellBusinessStock(key), secondary: true, width: 54);
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(2, 1, 2, 1);
        button.Font = new Font("Segoe UI", 8f);
        _businessTooltips.SetToolTip(button, "판매 완료한 재고를 0으로 기록합니다.");
        return WithBusinessHotkey(button, "sale:" + (key ?? "nightclub"));
    }

    private void SellBusinessStock(string? key)
    {
        BusinessLiveTimerOnTick(this, EventArgs.Empty);
        if (!_ownsBusinessTracker)
        {
            _status.Text = "다른 설정창을 닫은 뒤 판매를 눌러주세요.";
            return;
        }
        var targets = _businessEditors.Values.Where(editor => key is null
            ? BusinessSupplyCatalog.NightclubProfiles.Any(p => p.Key == editor.Profile.Key)
            : editor.Profile.Key == key).ToArray();
        foreach (var editor in targets)
        {
            if (editor.Snapshot is null) continue;
            var next = editor.Snapshot.CloneNormalized();
            BusinessSupplyCalculator.RecordSale(next);
            editor.Snapshot = next;
            editor.StockEdited = false;
        }
        SyncConfigFromBusinessSnapshots();
        ConfigStore.UpdateBusinessTrackingState(_config.BusinessSupplies, SelectedSupplyBusinessKey(),
            _businessOnline.Checked, _config.NightclubSafe, _config.BusinessCharacterGeneration);
        RefreshLiveBusinessEditors(_businessGame.IsGameAlive);
        _status.Text = (key is null ? "나이트클럽 전체" : targets.FirstOrDefault()?.Profile.Name) + " 판매 · 재고 0으로 반영";
    }

    private void RequestSupplyDelivery(BusinessEditor editor)
    {
        if (editor.Snapshot is null) return;
        var now = DateTimeOffset.UtcNow;
        var next = editor.Snapshot.CloneNormalized();
        var remaining = BusinessSupplyCalculator.SupplyDeliveryRemaining(next, now);
        if (remaining is { } activeDelivery && activeDelivery > TimeSpan.Zero)
        {
            next.SupplyDeliveryRequestedAtUtc = null;
            next.SupplyDeliveryRemainingSeconds = null;
        }
        else if (!BusinessSupplyCalculator.RequestSupplyDelivery(next, now))
            return;

        next.StateRevision = Math.Max(0, next.StateRevision) + 1;
        editor.Snapshot = next;
        editor.SupplyDeliveryRequestedAtUtc = next.SupplyDeliveryRequestedAtUtc;
        _businessSelectionDirty = true;
        RefreshBusinessEditorPreview(editor);
        UpdateLiveBusinessState(now, forceSave: true);
        ScheduleAutomaticApply();
    }

    private void RefreshSupplyDeliveryButton(BusinessEditor editor, DateTimeOffset now)
    {
        if (editor.ResupplyButton is null) return;
        var preview = editor.Snapshot?.CloneNormalized();
        if (preview is null) return;
        preview.SupplyDeliveryRequestedAtUtc = editor.SupplyDeliveryRequestedAtUtc;
        var remaining = BusinessSupplyCalculator.SupplyDeliveryRemaining(preview, now);
        var waiting = remaining is { } duration && duration > TimeSpan.Zero;

        editor.ResupplyButton.Enabled = true;
        editor.ResupplyButton.Text = waiting
            ? $"취소 {FormatDeliveryCountdown(remaining!.Value)}"
            : "보급";
        editor.ResupplyButton.BackColor = waiting ? FieldBackground : SupplyBackground;
        editor.ResupplyButton.ForeColor = Foreground;
        editor.ResupplyButton.FlatAppearance.BorderColor = waiting
            ? Color.FromArgb(46, 255, 255, 255)
            : SupplyBorder;
        _businessTooltips.SetToolTip(
            editor.ResupplyButton,
            waiting
                ? "배송 중입니다. 누르면 주문을 취소하고 처음의 보급 가능 상태로 돌아갑니다."
                : "보급을 주문하면 GTA 실행 시간 기준 10분 뒤 100%로 도착합니다.");
    }

    private static string FormatDeliveryCountdown(TimeSpan remaining)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private void RefreshBusinessEditorPreview(BusinessEditor editor)
    {
        if (editor.Snapshot is null) return;
        var preview = editor.Snapshot.CloneNormalized();
        preview.Enabled = editor.Enabled.Checked;
        if (editor.Tier is not null) preview.UpgradeTier = IndexToTier(editor.Profile, editor.Tier.SelectedIndex);
        if (editor.StaffAssignment is not null) preview.BunkerStaffAssignment = IndexToBunkerAssignment(editor.StaffAssignment.SelectedIndex);
        preview.MansionBoostStartedAtUtc = editor.MansionBoostStartedAtUtc;
        preview.SupplyDeliveryRequestedAtUtc = editor.SupplyDeliveryRequestedAtUtc;
        if (editor.StockEdited) ApplyStockInput(preview, editor);
        BusinessSupplyCalculator.CompleteSupplyDeliveryIfDue(preview, DateTimeOffset.UtcNow);
        var progress = BusinessSupplyCalculator.Calculate(preview, trackingOnline: _businessOnline.Checked);
        if (!editor.StockEdited) SetStockEditorDisplay(editor, preview, progress);
        editor.StockMeter.Value = progress.StockUnits;
        editor.StockMeter.Maximum = progress.StockCapacity;
        editor.StockMeter.Caption = UsesMoneyInput(editor.Profile)
            ? $"{FormatMoney(BusinessSupplyCalculator.StockUnitsToValue(progress.StockUnits, editor.Profile, preview.UpgradeTier))}/{FormatMoney(BusinessSupplyCalculator.FullStockValue(editor.Profile, preview.UpgradeTier))}"
            : $"{Math.Round(progress.StockUnits)}/{progress.StockCapacity} · {progress.StockPercent}%";
        if (editor.SupplySlider is not null)
        {
            var previousLoading = _loadingBusiness;
            _loadingBusiness = true;
            try
            {
                editor.SupplySlider.Value = progress.SupplyPercent ?? 0;
                if (editor.SupplyValueLabel is not null)
                    editor.SupplyValueLabel.Text = $"{progress.SupplyPercent ?? 0}%";
            }
            finally { _loadingBusiness = previousLoading; }
        }
        if (editor.State is not null) editor.State.Text = FormatBusinessState(progress);
        if (editor.Forecast is not null) editor.Forecast.Text = FormatBusinessForecast(progress);
        if (_dailyBoostButtons.TryGetValue(editor.Profile.Key, out var dailyBoost))
            UpdateDailyBoostAppearance(dailyBoost, progress);
        RefreshSupplyDeliveryButton(editor, DateTimeOffset.UtcNow);
        if (editor.Profile.Category == BusinessCategory.Nightclub && !_loadingBusiness)
            UpdateNightclubStaffCount();
    }
}
