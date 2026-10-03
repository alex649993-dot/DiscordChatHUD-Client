using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 02 HUD 레이아웃 페이지: HUD 동작 카드, 레이아웃 프리셋, 크기·글자·투명도·틴트·시계.
internal sealed partial class ConfigForm
{

    private Control BuildHudColumn()
    {
        var panel = BuildLayoutPanel();
        panel.Margin = new Padding(0);
        return panel;
    }

    private Control BuildLayoutPanel()
    {
        var group = CreateGroup("레이아웃과 표시 방식");
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 0, Margin = new Padding(0), Padding = new Padding(2, 0, 4, 8)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Section(string title)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var label = CreateInlineLabel(title);
            label.ForeColor = ActiveForeground;
            label.Font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
            label.Margin = new Padding(0, 10, 0, 3);
            table.Controls.Add(label, 0, row);
            table.SetColumnSpan(label, 2);
        }
        void Field(string label, Control field)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            AddLabeledField(table, row, label, field);
            field.Margin = new Padding(0, 5, 0, 5);
        }
        Control Slider(IosSlider slider, Label value) => CreateSliderField(slider, value);

        Section("프리셋 · 크기와 위치");
        var presetRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        presetRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        presetRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        _layoutChoice.Dock = DockStyle.Fill;
        _layoutChoice.Margin = new Padding(0, 2, 8, 0);
        presetRow.Controls.Add(_layoutChoice, 0, 0);
        var addPreset = CreateButton("추가", (_, _) => AddLayout(), secondary: true, width: 62);
        var deletePreset = CreateButton("삭제", (_, _) => DeleteLayout(), secondary: true, width: 62);
        addPreset.Dock = deletePreset.Dock = DockStyle.Fill;
        presetRow.Controls.Add(addPreset, 1, 0);
        presetRow.Controls.Add(deletePreset, 2, 0);
        Field("프리셋", presetRow);
        presetRow.Margin = new Padding(0, 2, 0, 2);
        Field("이름", _layoutName);

        var sizeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
        sizeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        sizeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sizeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        sizeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sizeRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _width.Dock = _height.Dock = DockStyle.Fill;
        _width.Margin = _height.Margin = new Padding(0);
        sizeRow.Controls.Add(CreateInlineLabel("너비"), 0, 0);
        sizeRow.Controls.Add(_width, 1, 0);
        var heightLabel = CreateInlineLabel("높이");
        heightLabel.Padding = new Padding(10, 0, 0, 0);
        sizeRow.Controls.Add(heightLabel, 2, 0);
        sizeRow.Controls.Add(_height, 3, 0);
        Field("HUD 크기", sizeRow);
        Field("현재 위치", _position);
        _businessTooltips.SetToolTip(_position, "하단 위치/크기 조정에서 HUD를 옮기거나 크기를 바꿀 수 있습니다.");

        Section("글자 · 이미지와 이모지");
        var fontRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        fontRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fontRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fontRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _autoFontScale.Anchor = AnchorStyles.Left;
        _autoFontScale.Margin = new Padding(0, 0, 12, 0);
        fontRow.Controls.Add(_autoFontScale, 0, 0);
        fontRow.Controls.Add(Slider(_fontScale, _fontScaleValue), 1, 0);
        Field("글자 크기", fontRow);
        _businessTooltips.SetToolTip(_autoFontScale, "화면에 맞춰 자동 조절합니다. FHD 75% · QHD 100% · 4K 150%");
        Field("이미지 · GIF 크기", Slider(_mediaScale, _mediaScaleValue));
        Field("이모지 크기", Slider(_emojiScale, _emojiScaleValue));
        Field("반응 이모지 크기", Slider(_reactionEmojiScale, _reactionEmojiScaleValue));
        Field("닉네임 사이 간격", Slider(_nicknameGroupGap, _nicknameGroupGapValue));
        _businessTooltips.SetToolTip(_emojiScale, "이모지만 있는 메시지의 크기입니다. 텍스트와 함께 쓰인 이모지는 글자 크기를 따릅니다.");
        _businessTooltips.SetToolTip(_reactionEmojiScale, "메시지 아래 반응 이모지 크기입니다. 반응 개수 글자는 유지됩니다.");
        _businessTooltips.SetToolTip(_nicknameGroupGap, "서로 다른 닉네임 메시지 그룹 사이의 세로 간격입니다. 같은 사람이 연속으로 보낸 메시지 간격은 바뀌지 않습니다.");

        Section("투명도 · 배경");
        Field("배경 투명도", Slider(_backgroundTintAlpha, _backgroundTintValue));
        Field("닉네임 투명도", Slider(_nicknameBadgeAlpha, _nicknameBadgeValue));
        Field("미디어 투명도", Slider(_mediaOpacity, _mediaOpacityValue));
        Field("판매 HUD 투명도", Slider(_saleTintAlpha, _saleTintValue));
        Field("사업장 HUD 투명도", Slider(_businessTintAlpha, _businessTintValue));
        Field("직원 배정 HUD 투명도", Slider(_staffAssignmentTintAlpha, _staffAssignmentTintValue));
        Field("배경 색상", CreateTintModeSelector());
        _businessTooltips.SetToolTip(_mediaOpacity, "첨부 이미지·GIF·스티커에 적용합니다. 0%는 숨김, 100%는 원본 불투명도입니다.");
        _businessTooltips.SetToolTip(_businessTintAlpha, "사업장 HUD 배경: 0% 투명, 100% 불투명. 글자와 아이콘은 유지됩니다.");

        Section("시계");
        Field("시계 위치", CreateClockPlacementSelector());
        Field("시계 정렬", CreateClockAlignmentSelector());
        _reactionEmojiScale.ValueChanged += (_, _) => { UpdateScaleValueLabels(); MarkLayoutDirty(); };
        _layoutChoice.SelectedIndexChanged += (_, _) => LayoutSelectionChanged();
        _layoutName.TextChanged += (_, _) => MarkLayoutDirty();
        _width.ValueChanged += (_, _) =>
        {
            if (_loadingLayout) return;
            _dirtyLayoutWidths.Add(_editingLayout);
            MarkLayoutDirty();
        };
        _height.ValueChanged += (_, _) =>
        {
            if (_loadingLayout) return;
            _dirtyLayoutHeights.Add(_editingLayout);
            MarkLayoutDirty();
        };
        _fontScale.ValueChanged += (_, _) =>
        {
            UpdateScaleValueLabels();
            if (!_autoFontScale.Checked) MarkLayoutDirty();
        };
        _mediaScale.ValueChanged += (_, _) =>
        {
            UpdateScaleValueLabels();
            MarkLayoutDirty();
        };
        _autoFontScale.CheckedChanged += (_, _) => AutomaticFontScaleChanged();
        _mediaOpacity.ValueChanged += (_, _) => { _mediaOpacityValue.Text = $"{_mediaOpacity.Value}%"; MarkLayoutDirty(); };
        _emojiScale.ValueChanged += (_, _) =>
        {
            UpdateScaleValueLabels();
            MarkLayoutDirty();
        };
        _backgroundTintAlpha.ValueChanged += (_, _) => UpdateTintValueLabels();
        _nicknameBadgeAlpha.ValueChanged += (_, _) => UpdateTintValueLabels();
        _nicknameGroupGap.ValueChanged += (_, _) => UpdateScaleValueLabels();
        _saleTintAlpha.ValueChanged += (_, _) => UpdateTintValueLabels();
        _businessTintAlpha.ValueChanged += (_, _) => UpdateTintValueLabels();
        _staffAssignmentTintAlpha.ValueChanged += (_, _) => UpdateTintValueLabels();
        foreach (var label in table.Controls.OfType<Label>())
        {
            if (table.GetColumnSpan(label) == 1) label.AutoSize = true;
        }
        scroll.Controls.Add(table);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 134));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHudBehaviorCard(), 0, 0);
        root.Controls.Add(scroll, 0, 1);
        group.Controls.Add(root);
        return group;
    }

    /// <summary>
    /// 프리셋과 상관없는 HUD 전체 동작. 프리셋 목록 맨 아래에 묻혀 잘 안 보였으므로
    /// 02 맨 위, 스크롤 밖에 둔다.
    /// </summary>
    private Control BuildHudBehaviorCard()
    {
        var card = new ToolkitBusinessCard("HUD 동작 · 모든 프리셋 공통")
        {
            Margin = new Padding(0, 0, 0, 10)
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        _alwaysVisible.Appearance = Appearance.Button;
        _alwaysVisible.FlatStyle = FlatStyle.Flat;
        _alwaysVisible.FlatAppearance.BorderSize = 1;
        _alwaysVisible.Dock = DockStyle.Fill;
        _alwaysVisible.Margin = new Padding(0, 3, 12, 3);
        _alwaysVisible.TextAlign = ContentAlignment.MiddleCenter;
        _alwaysVisible.Tag = "hud-visibility-mode";
        _alwaysVisible.CheckedChanged += (_, _) => UpdateVisibilityModeAppearance(_alwaysVisible);
        _businessTooltips.SetToolTip(_alwaysVisible,
            "항상 켜짐: 다른 앱 사용 중이나 GTA5 종료 후에도 표시합니다. GTA5에서만 켜짐: GTA5가 활성 창일 때 표시합니다. 수동 숨김과 캡처 숨김 설정은 두 모드 모두 적용됩니다.");
        UpdateVisibilityModeAppearance(_alwaysVisible);

        _excludeFromCapture.Appearance = Appearance.Button;
        _excludeFromCapture.FlatStyle = FlatStyle.Flat;
        _excludeFromCapture.FlatAppearance.BorderSize = 1;
        _excludeFromCapture.Dock = DockStyle.Fill;
        _excludeFromCapture.Margin = new Padding(0, 3, 12, 3);
        _excludeFromCapture.TextAlign = ContentAlignment.MiddleCenter;
        _excludeFromCapture.Tag = "capture-exclusion";
        _excludeFromCapture.CheckedChanged += (_, _) => UpdateCaptureExclusionAppearance();

        _animateGif.Appearance = Appearance.Button;
        _animateGif.FlatStyle = FlatStyle.Flat;
        _animateGif.FlatAppearance.BorderSize = 1;
        _animateGif.Dock = DockStyle.Fill;
        _animateGif.Margin = new Padding(0, 3, 0, 3);
        _animateGif.TextAlign = ContentAlignment.MiddleCenter;
        _animateGif.Tag = "gif-animation";
        _animateGif.CheckedChanged += (_, _) => UpdateGifAnimationAppearance();
        _businessTooltips.SetToolTip(
            _animateGif,
            "켜면 GIF·APNG·애니메이션 WebP를 재생합니다(화면에 보이는 것만 최대 약 30fps). "
            + "끄면 첫 장면으로 멈춰 보여주고 다시 그리지 않아 CPU를 덜 씁니다.");

        // 세 토글은 버튼 글자가 곧 상태라 이름표 없이 한 줄에 둔다.
        var toggles = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        for (var column = 0; column < 3; column++) toggles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333f));
        toggles.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toggles.Controls.Add(_alwaysVisible, 0, 0);
        toggles.Controls.Add(_excludeFromCapture, 1, 0);
        toggles.Controls.Add(_animateGif, 2, 0);
        table.Controls.Add(toggles, 0, 0);
        table.SetColumnSpan(toggles, 4);

        table.Controls.Add(CreateInlineLabel("HUD 숨기기/표시"), 0, 1);
        _hudToggleHotkey.Dock = DockStyle.Fill;
        _hudToggleHotkey.Margin = new Padding(0, 6, 12, 6);
        table.Controls.Add(_hudToggleHotkey, 1, 1);

        table.Controls.Add(CreateInlineLabel("종료 단축키"), 2, 1);
        var exitRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        exitRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        exitRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        exitRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _exitHotkey.Dock = DockStyle.Fill;
        _exitHotkey.Margin = new Padding(0, 6, 8, 6);
        exitRow.Controls.Add(_exitHotkey, 0, 0);
        var stopHud = CreateButton("HUD 종료", (_, _) => GameAutoLaunch.StopHud(), secondary: true, width: 110);
        stopHud.Anchor = AnchorStyles.Right;
        stopHud.Margin = new Padding(0, 2, 0, 2);
        _businessTooltips.SetToolTip(stopHud, "지금 떠 있는 HUD를 닫습니다. 이번 게임 동안은 자동으로 다시 켜지지 않습니다.");
        exitRow.Controls.Add(stopHud, 1, 0);
        table.Controls.Add(exitRow, 3, 1);
        foreach (var label in table.Controls.OfType<Label>()) label.AutoSize = true;
        card.Controls.Add(table);
        return card;
    }

    private void RefreshLayoutChoices(int selected)
    {
        _loadingLayout = true;
        try
        {
            _layoutChoice.Items.Clear();
            for (var i = 0; i < _config.LayoutPresets.Count; i++)
                _layoutChoice.Items.Add($"{i + 1}: {_config.LayoutPresets[i].Name}");
            _layoutChoice.SelectedIndex = Math.Clamp(selected, 0, _config.LayoutPresets.Count - 1);
        }
        finally { _loadingLayout = false; }
    }

    private void LoadLayout(int index)
    {
        index = Math.Clamp(index, 0, _config.LayoutPresets.Count - 1);
        _loadingLayout = true;
        try
        {
            _editingLayout = index;
            var preset = _config.LayoutPresets[index];
            _layoutName.Text = preset.Name;
            _width.Value = Math.Clamp(preset.Width, 0, HudLayoutPreset.MaximumWidth);
            _height.Value = Math.Clamp(preset.Height, 0, HudLayoutPreset.MaximumHeight);
            var automaticFontScale = preset.FontScale == 0;
            _autoFontScale.Checked = automaticFontScale;
            _fontScale.InteractionEnabled = !automaticFontScale;
            _fontScale.Value = automaticFontScale
                ? GetAutomaticFontScaleForCurrentMonitor()
                : Math.Clamp(preset.FontScale, 30, 200);
            _mediaScale.Value = Math.Clamp(preset.MediaScale, 30, 100);
            _mediaOpacity.Value = Math.Clamp(preset.MediaOpacity, 0, 100);
            _mediaOpacityValue.Text = $"{_mediaOpacity.Value}%";
            _emojiScale.Value = Math.Clamp(preset.EmojiScale, 30, 200);
            _reactionEmojiScale.Value = Math.Clamp(preset.ReactionEmojiScale, 30, 200);
            UpdateScaleValueLabels();
            _position.Text = preset.PositionX is null || preset.PositionY is null
                ? "자동"
                : $"X {preset.PositionX}, Y {preset.PositionY}";
        }
        finally { _loadingLayout = false; }
    }

    private void CommitCurrentLayout(bool markDirty = false)
    {
        if (_config.LayoutPresets.Count == 0) return;
        var preset = _config.LayoutPresets[Math.Clamp(_editingLayout, 0, _config.LayoutPresets.Count - 1)];
        preset.Name = string.IsNullOrWhiteSpace(_layoutName.Text) ? $"프리셋 {_editingLayout + 1}" : _layoutName.Text.Trim();
        preset.Width = (int)_width.Value;
        preset.Height = (int)_height.Value;
        preset.FontScale = _autoFontScale.Checked ? 0 : _fontScale.Value;
        preset.MediaScale = _mediaScale.Value;
        preset.MediaOpacity = _mediaOpacity.Value;
        preset.EmojiScale = _emojiScale.Value;
        preset.ReactionEmojiScale = _reactionEmojiScale.Value;
        preset.Normalize(_editingLayout + 1);
        if (markDirty) _dirtyLayouts.Add(_editingLayout);
    }

    private void LayoutSelectionChanged()
    {
        if (_loadingLayout || _layoutChoice.SelectedIndex < 0) return;
        CommitCurrentLayout();
        LoadLayout(_layoutChoice.SelectedIndex);
    }

    private void MarkLayoutDirty()
    {
        // Controls filled before LoadLayout still hold construction defaults;
        // committing them here would overwrite the saved preset with 100s.
        if (_loadingLayout || _loadingPreferences) return;
        CommitCurrentLayout(markDirty: true);
        var selected = _layoutChoice.SelectedIndex;
        RefreshLayoutChoices(selected);
    }

    private void AddLayout()
    {
        CommitCurrentLayout();
        if (_config.LayoutPresets.Count >= 8)
        {
            MessageBox.Show("레이아웃 프리셋은 최대 8개까지 가능해.", Text);
            return;
        }
        var created = _config.LayoutPresets[_editingLayout].Clone();
        created.Name = $"프리셋 {_config.LayoutPresets.Count + 1}";
        created.PositionX = null;
        created.PositionY = null;
        created.PositionSpace = null;
        created.PositionWorkWidth = null;
        created.PositionWorkHeight = null;
        _config.LayoutPresets.Add(created);
        _layoutStructureDirty = true;
        RefreshLayoutChoices(_config.LayoutPresets.Count - 1);
        LoadLayout(_config.LayoutPresets.Count - 1);
    }

    private void DeleteLayout()
    {
        if (_config.LayoutPresets.Count <= 2)
        {
            MessageBox.Show("레이아웃 프리셋은 최소 2개를 유지해야 해.", Text);
            return;
        }
        _config.LayoutPresets.RemoveAt(_editingLayout);
        _layoutStructureDirty = true;
        var next = Math.Min(_editingLayout, _config.LayoutPresets.Count - 1);
        RefreshLayoutChoices(next);
        LoadLayout(next);
    }

    private void AutomaticFontScaleChanged()
    {
        if (_loadingLayout) return;

        _loadingLayout = true;
        try
        {
            _fontScale.InteractionEnabled = !_autoFontScale.Checked;
            if (_autoFontScale.Checked && ReferenceEquals(_wheelArmedControl, _fontScale))
                _wheelArmedControl = null;
            if (_autoFontScale.Checked)
                _fontScale.Value = GetAutomaticFontScaleForCurrentMonitor();
            UpdateScaleValueLabels();
        }
        finally { _loadingLayout = false; }

        MarkLayoutDirty();
    }

    private int GetAutomaticFontScaleForCurrentMonitor()
    {
        var screen = Screen.FromControl(this).Bounds;
        return HudLayoutPreset.CalculateAutomaticFontScale(screen.Size);
    }

    private Control CreateTintModeSelector()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _lightTint.Margin = new Padding(0, 5, 12, 0);
        _darkTint.Margin = new Padding(0, 5, 0, 0);
        panel.Controls.Add(_lightTint);
        panel.Controls.Add(_darkTint);
        return panel;
    }

    private Control CreateClockPlacementSelector()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _clockHeader.Margin = new Padding(0, 5, 18, 0);
        _clockBottom.Margin = new Padding(0, 5, 0, 0);
        panel.Controls.Add(_clockHeader);
        panel.Controls.Add(_clockBottom);
        return panel;
    }

    private Control CreateClockAlignmentSelector()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _clockLeft.Margin = new Padding(0, 5, 18, 0);
        _clockRight.Margin = new Padding(0, 5, 0, 0);
        panel.Controls.Add(_clockLeft);
        panel.Controls.Add(_clockRight);
        return panel;
    }

    private void UpdateTintValueLabels()
    {
        _backgroundTintValue.Text = $"{_backgroundTintAlpha.Value}%";
        _nicknameBadgeValue.Text = $"{_nicknameBadgeAlpha.Value}%";
        _saleTintValue.Text = $"{_saleTintAlpha.Value}%";
        _businessTintValue.Text = $"{_businessTintAlpha.Value}%";
        _staffAssignmentTintValue.Text = $"{_staffAssignmentTintAlpha.Value}%";
    }

    private void UpdateScaleValueLabels()
    {
        _fontScaleValue.Text = $"{_fontScale.Value}%";
        _mediaScaleValue.Text = $"{_mediaScale.Value}%";
        _emojiScaleValue.Text = $"{_emojiScale.Value}%";
        _reactionEmojiScaleValue.Text = $"{_reactionEmojiScale.Value}%";
        _nicknameGroupGapValue.Text = $"{_nicknameGroupGap.Value}px";
    }
}
