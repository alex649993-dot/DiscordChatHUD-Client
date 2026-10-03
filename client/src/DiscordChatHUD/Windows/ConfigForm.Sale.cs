using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 03 판매 페이지.
internal sealed partial class ConfigForm
{

    private Control BuildSalePanel()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            Margin = new Padding(0), BackColor = WindowBackground
        };
        var group = CreateGroup("판매 HUD 표시");
        group.Dock = DockStyle.Top;
        group.Height = 280;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 6,
            Margin = new Padding(0),
            Padding = new Padding(2, 0, 2, 0),
            BackColor = PanelBackground
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(CreateInlineLabel("시작 상태"), 0, 0);
        _showSaleStatus.Appearance = Appearance.Button;
        _showSaleStatus.AutoSize = false;
        _showSaleStatus.Dock = DockStyle.Fill;
        _showSaleStatus.FlatStyle = FlatStyle.Flat;
        _showSaleStatus.TextAlign = ContentAlignment.MiddleCenter;
        _showSaleStatus.Margin = new Padding(0, 2, 12, 5);
        _showSaleStatus.Tag = "sale-status-start";
        table.Controls.Add(_showSaleStatus, 1, 0);
        var startHint = CreateInlineLabel("HUD를 실행할 때 적용됩니다.");
        startHint.ForeColor = Muted;
        table.Controls.Add(startHint, 2, 0);

        table.Controls.Add(CreateInlineLabel("표시 전환 단축키"), 0, 1);
        _saleStatusHotkey.Dock = DockStyle.None;
        _saleStatusHotkey.Anchor = AnchorStyles.Left;
        _saleStatusHotkey.Width = 130;
        _saleStatusHotkey.Margin = new Padding(0, 3, 0, 3);
        table.Controls.Add(_saleStatusHotkey, 1, 1);
        var keyHint = CreateInlineLabel("판매 HUD를 표시하거나 숨깁니다.");
        keyHint.ForeColor = Muted;
        table.Controls.Add(keyHint, 2, 1);

        var autoHeading = CreateInlineLabel("자동 표시 / 숨김");
        autoHeading.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        table.Controls.Add(autoHeading, 0, 3);
        table.SetColumnSpan(autoHeading, 3);
        _saleAutoHideIdle.Dock = DockStyle.Fill;
        _saleAutoHideIdle.Margin = new Padding(3, 0, 0, 0);
        table.Controls.Add(_saleAutoHideIdle, 0, 4);
        table.SetColumnSpan(_saleAutoHideIdle, 3);
        var hint = CreateInlineLabel("마지막 판매가 완료되면 바로 숨깁니다.\n수동으로 숨겨도 새 판매가 등록되면 다시 표시합니다.");
        hint.ForeColor = Muted;
        hint.TextAlign = ContentAlignment.TopLeft;
        hint.Margin = new Padding(3, 4, 0, 0);
        table.Controls.Add(hint, 0, 5);
        table.SetColumnSpan(hint, 3);
        _showSaleStatus.CheckedChanged += (_, _) => UpdateSaleStatusStartAppearance(_showSaleStatus);
        UpdateSaleStatusStartAppearance(_showSaleStatus);
        group.Controls.Add(table);
        host.Controls.Add(group);
        return host;
    }
}
