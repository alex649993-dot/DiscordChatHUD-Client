using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 06 설정 가져오기 페이지.
internal sealed partial class ConfigForm
{

    private Control BuildSettingsImportPanel()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            Padding = new Padding(0), BackColor = WindowBackground
        };
        // Use one layout-managed title, not a caption painted underneath it.
        var card = new IosCardPanel("")
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24), BackColor = PanelBackground
        };
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 4, Margin = new Padding(0), Padding = new Padding(0),
            BackColor = PanelBackground
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label
        {
            Text = "설정 파일 가져오기", AutoSize = true, Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 10),
            Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Foreground
        };
        var note = new Label
        {
            Text = "기존 DiscordChatHUD_Settings.json 또는 내보낸 JSON 파일을 선택하세요.\n가져온 파일의 설정으로 현재 설정을 교체합니다.",
            AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 16),
            Font = new Font("Segoe UI", 9f), ForeColor = Muted
        };
        var select = CreateButton("설정 파일 선택", (_, _) => SelectSettingsImportFile(), width: 156);
        select.Anchor = AnchorStyles.Left;
        select.Margin = new Padding(0, 0, 0, 20);
        var drop = new Label
        {
            Text = "JSON 파일을 여기에 끌어놓아도 됩니다.", AutoSize = false, Dock = DockStyle.Top,
            Height = 76, Margin = new Padding(0), Padding = new Padding(16),
            BackColor = FieldBackground, ForeColor = Muted, TextAlign = ContentAlignment.MiddleCenter
        };
        rows.Controls.Add(title, 0, 0);
        rows.Controls.Add(note, 0, 1);
        rows.Controls.Add(select, 0, 2);
        rows.Controls.Add(drop, 0, 3);
        card.Controls.Add(rows);
        host.Controls.Add(card);
        // Labels/buttons cover most of the card: register those targets too.
        void EnableImportDrop(Control target)
        {
            target.AllowDrop = true;
            target.DragEnter += (_, e) =>
            {
                var valid = e.Data?.GetData(DataFormats.FileDrop) is string[] files
                            && files.Length == 1
                            && string.Equals(Path.GetExtension(files[0]), ".json", StringComparison.OrdinalIgnoreCase);
                e.Effect = valid ? DragDropEffects.Copy : DragDropEffects.None;
            };
            target.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1)
                    ImportSettingsFile(files[0]);
            };
            foreach (Control child in target.Controls) EnableImportDrop(child);
        }
        EnableImportDrop(host);
        return host;
    }

    private void SelectSettingsImportFile()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON 설정 파일 (*.json)|*.json|모든 파일 (*.*)|*.*", Multiselect = false, Title = "DiscordChatHUD 설정 가져오기" };
        if (dialog.ShowDialog(this) == DialogResult.OK) ImportSettingsFile(dialog.FileName);
    }

    private void ImportSettingsFile(string path)
    {
        try
        {
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("JSON 파일만 가져올 수 있습니다.");
            _config = ConfigStore.LoadImportFile(path);
            ConfigStore.Save(_config, replaceBusinessState: true);
            ClearLayoutEdits();
            _selectedTargetChannelId = _config.TargetChannelId;
            PopulateFromConfig();
            _status.Text = "설정 가져오기 완료 · 가져온 설정을 적용했습니다";
        }
        catch (Exception ex)
        {
            AppLog.Error("설정 JSON 가져오기 실패", ex);
            MessageBox.Show($"설정 가져오기 실패\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
