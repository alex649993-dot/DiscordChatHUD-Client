using DiscordChatHUD.Services;
namespace DiscordChatHUD.Windows;
internal sealed class UpdateForm : Form
{
    private readonly RichTextBox _status = new() { Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = RichTextBoxScrollBars.ForcedVertical, DetectUrls = false, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16) };
    private readonly Button _action = new() { Text = "업데이트 확인", AutoSize = true, Dock = DockStyle.Fill, MinimumSize = new Size(0, 44), Padding = new Padding(12, 8, 12, 8) };
    private readonly CancellationTokenSource _stop = new();
    private readonly Form _owner;
    private (UpdateManifest Manifest, byte[] Envelope)? _available;
    internal UpdateForm(Form owner)
    {
        _owner = owner; Text = "DiscordChatHUD · 업데이트";
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Segoe UI", 11);
        ClientSize = new Size(640, 440); MinimumSize = new Size(420, 280); Padding = new Padding(24);
        BackColor = Color.FromArgb(16, 17, 21); ForeColor = Color.White;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = false;
        StatusText = $"현재 버전: Preview {AppUpdate.CurrentVersion}\n새 버전을 확인합니다.";
        _action.BackColor = Color.FromArgb(250, 45, 85); _action.ForeColor = Color.White; _action.FlatStyle = FlatStyle.Flat;
        _status.BackColor = BackColor; _status.ForeColor = ForeColor;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_status, 0, 0); layout.Controls.Add(_action, 0, 1);
        Controls.Add(layout);
        _action.Click += async (_, _) => await Execute();
        Shown += async (_, _) => await Execute(); FormClosed += (_, _) => _stop.Cancel();
    }
    // Use Windows newlines consistently, including notes received from the publisher.
    private string StatusText
    {
        set => _status.Text = value.ReplaceLineEndings("\r\n");
    }
    private static string FormatNotes(string notes)
        => string.Join("\r\n\r\n", notes.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task Execute()
    {
        _action.Enabled = false; string? folder = null;
        try
        {
            if (_available is null)
            {
                StatusText = $"현재 버전: Preview {AppUpdate.CurrentVersion}\n업데이트 확인 중…";
                var update = await AppUpdate.Check(_stop.Token);
                if (update.Manifest.Version <= AppUpdate.CurrentVersion)
                { StatusText = $"Preview {AppUpdate.CurrentVersion} · 최신 버전입니다."; return; }
                _available = update; _action.Text = "다운로드하고 업데이트";
                StatusText = $"Preview {AppUpdate.CurrentVersion} → {update.Manifest.Version}\n\n다운로드: {update.Manifest.Size / 1024d / 1024d:0.#} MB\n\n{ReleaseNotes.Format(update.Manifest, AppUpdate.CurrentVersion)}";
                return;
            }
            var next = _available.Value;
            StatusText = "업데이트 다운로드 중…";
            folder = await AppUpdate.Download(next.Manifest, next.Envelope, new Progress<int>(value => { if (!IsDisposed) StatusText = $"업데이트 다운로드 중 · {value}%"; }), _stop.Token);
            if (IsDisposed) return;
            if (MessageBox.Show(this, "다운로드와 서명 확인이 끝났습니다.\n설정 창과 HUD를 닫고 업데이트한 뒤 다시 실행합니다.\n설정과 로그인 정보는 유지됩니다.",
                "업데이트 적용", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            if (_owner is ConfigForm settings) await settings.FlushBeforeUpdateAsync();
            AppUpdate.StartInstaller(folder); folder = null;
            DialogResult = DialogResult.OK; Close();
            if (DesktopContext.Current is { } desktop) await desktop.ExitAllAsync(); else _owner.Close();
        }
        catch (OperationCanceledException) { if (!IsDisposed) StatusText = "다운로드가 취소되었거나 응답 시간이 초과되었습니다. 다시 시도해주세요."; }
        catch (Exception ex) { if (!IsDisposed) StatusText = "업데이트를 완료하지 못했습니다.\n" + ex.Message; }
        finally { if (folder is not null) AppUpdate.Clean(folder); if (!IsDisposed) _action.Enabled = true; }
    }
}
