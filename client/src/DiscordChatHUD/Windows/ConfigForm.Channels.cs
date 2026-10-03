using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 01 채널 프리셋 페이지와 채널 목록.
internal sealed partial class ConfigForm
{

    private Control BuildChannelsPanel()
    {
        var group = CreateGroup("채널 선택");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(2, 0, 2, 0),
            BackColor = PanelBackground
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var guide = CreateInlineLabel("HUD에 표시할 채널을 선택하세요.");
        guide.ForeColor = Muted;
        guide.Margin = new Padding(3, 0, 3, 6);
        panel.Controls.Add(guide, 0, 0);

        _channelList.Dock = DockStyle.Fill;
        _channelList.IntegralHeight = false;
        _channelList.ItemHeight = 60;
        _channelList.DrawMode = DrawMode.OwnerDrawFixed;
        _channelList.BorderStyle = BorderStyle.None;
        _channelList.BackColor = PanelBackground;
        _channelList.Margin = new Padding(0, 0, 0, 14);
        _channelList.DrawItem += DrawChannelPresetItem;
        panel.Controls.Add(_channelList, 0, 1);

        var options = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 3, Margin = new Padding(3, 0, 3, 0),
            Padding = new Padding(0), BackColor = PanelBackground
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 3; row++) options.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        options.Controls.Add(CreateInlineLabel("채널 선택 단축키"), 0, 0);
        var channelKeys = new SettingsFlowPanel
        {
            Dock = DockStyle.Fill, AutoSize = false, WrapContents = false,
            Margin = new Padding(0, 0, 0, 6), Padding = new Padding(0)
        };
        _channelPickerHotkey.Width = 130;
        _channelPickerHotkey.Margin = new Padding(0, 3, 12, 3);
        channelKeys.Controls.Add(_channelPickerHotkey);
        var keyHint = CreateFlowLabel("게임에서 채널 선택창 열기");
        keyHint.ForeColor = Muted;
        keyHint.Margin = new Padding(0, 5, 0, 3);
        channelKeys.Controls.Add(keyHint);
        options.Controls.Add(channelKeys, 1, 0);

        options.Controls.Add(CreateInlineLabel("표시 옵션"), 0, 1);
        var displayOptions = new SettingsFlowPanel
        {
            Dock = DockStyle.Fill, AutoSize = false, WrapContents = false,
            Margin = new Padding(0, 0, 0, 6), Padding = new Padding(0)
        };
        _showChannelName.BackColor = PanelBackground;
        _showChannelName.Margin = new Padding(0, 5, 20, 5);
        _sessionPopulation.Margin = new Padding(0, 5, 0, 5);
        displayOptions.Controls.Add(_showChannelName);
        displayOptions.Controls.Add(_sessionPopulation);
        options.Controls.Add(displayOptions, 1, 1);

        options.Controls.Add(CreateInlineLabel("대체 세션"), 0, 2);
        _secondarySessionPopulation.Anchor = AnchorStyles.Left;
        _secondarySessionPopulation.Margin = new Padding(0, 5, 0, 5);
        options.Controls.Add(_secondarySessionPopulation, 1, 2);
        _businessTooltips.SetToolTip(_secondarySessionPopulation,
            $"{GtaSessionPresence.TrackedHostName}가 GTA 세션에 없을 때 "
            + $"{GtaSessionPresence.SecondaryHostName}의 세션 인원을 대신 표시합니다. "
            + "두 사람 모두 세션에 없으면 대기 문구를 표시합니다. "
            + "상대가 디스코드 상태를 온라인으로 두고 GTA 활동 표시를 켜 두어야 읽을 수 있습니다.");
        panel.Controls.Add(options, 0, 2);
        group.Controls.Add(panel);

        _channelList.SelectedIndexChanged += (_, _) => SelectTargetFromChannelList();
        return group;
    }

    private static void DrawChannelPresetItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count) return;
        int Px(float value) => ScaledInt(value);
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var canvas = new SolidBrush(PanelBackground);
        e.Graphics.FillRectangle(canvas, e.Bounds);
        var card = new RectangleF(
            e.Bounds.X + Scaled(3f),
            e.Bounds.Y + Scaled(3f),
            Math.Max(1, e.Bounds.Width - Scaled(7f)),
            Math.Max(1, e.Bounds.Height - Scaled(6f)));
        using var path = CreateChannelCardPath(card, Scaled(10f));
        using var background = new SolidBrush(selected ? Color.FromArgb(36, 39, 51) : FieldBackground);
        using var border = new Pen(selected ? ActiveBorder : Color.FromArgb(46, 255, 255, 255), 1f);
        e.Graphics.FillPath(background, path);
        e.Graphics.DrawPath(border, path);

        var numberBounds = new Rectangle((int)card.Left + Px(12), (int)card.Top + Px(11), Px(34), Px(31));
        using var numberPath = CreateChannelCardPath(numberBounds, Scaled(10f));
        using var numberFill = new SolidBrush(selected ? Accent : Color.FromArgb(58, 59, 67));
        using var numberFont = ScaledFont(8f, FontStyle.Bold);
        using var titleFont = ScaledFont(9.2f, FontStyle.Bold);
        using var detailFont = ScaledFont(7.7f, FontStyle.Regular);
        e.Graphics.FillPath(numberFill, numberPath);
        TextRenderer.DrawText(
            e.Graphics,
            $"{e.Index + 1:00}",
            numberFont,
            numberBounds,
            Foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var channel = list.Items[e.Index] as ChannelPreset;
        var titleBounds = new Rectangle((int)card.Left + Px(58), (int)card.Top + Px(7), Math.Max(1, (int)card.Width - Px(74)), Px(24));
        TextRenderer.DrawText(
            e.Graphics,
            channel?.Name ?? list.GetItemText(list.Items[e.Index]),
            titleFont,
            titleBounds,
            Foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var detailBounds = new Rectangle(titleBounds.X, titleBounds.Bottom, titleBounds.Width, Px(19));
        TextRenderer.DrawText(
            e.Graphics,
            selected ? "HUD에 표시 중" : "클릭하여 선택",
            detailFont,
            detailBounds,
            selected ? ActiveForeground : Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath CreateChannelCardPath(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 271, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private int _serverChannelRevision = -1;
    private void RefreshServerChannelList()
    {
        if (!RelayClientSettings.Enabled || _serverChannelRevision == ConfigStore.CatalogRevision) return;
        _serverChannelRevision = ConfigStore.CatalogRevision;
        var latest = ConfigStore.Load();
        // SaveCore may already have refreshed _config; compare the actual visible rows.
        if (_channelList.Items.Cast<ChannelPreset>().Select(x => (x.Id, x.Name)).SequenceEqual(latest.ChannelPresets.Select(x => (x.Id, x.Name)))) return;
        var dirty = _channelSelectionDirty;
        _config.ChannelPresets = latest.ChannelPresets;
        if (!dirty || !_config.ChannelPresets.Any(x => x.Id == _selectedTargetChannelId))
        { _selectedTargetChannelId = latest.TargetChannelId; dirty = false; }
        RefreshChannelList(); _channelSelectionDirty = dirty;
    }
    private void RefreshChannelList()
    {
        _channelList.Items.Clear();
        foreach (var channel in _config.ChannelPresets) _channelList.Items.Add(channel);
        var selectedIndex = _config.ChannelPresets.FindIndex(channel => channel.Id == _selectedTargetChannelId);
        if (selectedIndex >= 0) _channelList.SelectedIndex = selectedIndex;
    }

    private void SelectTargetFromChannelList()
    {
        if (_channelList.SelectedItem is ChannelPreset channel) { _selectedTargetChannelId = channel.Id; _channelSelectionDirty=true; }
        _sessionPopulation.Visible=true;
        _secondarySessionPopulation.Visible=true;
    }
}
