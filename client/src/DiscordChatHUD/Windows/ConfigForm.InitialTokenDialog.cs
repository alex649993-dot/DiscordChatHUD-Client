using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 설정창 전용 컨트롤 클래스(배율, 첫 실행 창, 슬라이더, 카드, 버튼 등).
// ConfigForm — 첫 실행 연결 정보 입력 창.
internal sealed partial class ConfigForm
{

    private sealed class InitialTokenDialog : Form
    {
        private const int DesignDialogWidth = 520;
        private const int DesignDialogHeight = 271;
        private readonly UiScaler _scaler = new();
        private readonly TextBox _tokenEntry = new()
        {
            UseSystemPasswordChar = true,
            PlaceholderText = "Discord 연결 토큰 입력"
        };

        public InitialTokenDialog()
        {
            Text = "DiscordChatHUD · 첫 실행";
            // 설정창과 같은 배율을 쓴다. 크기 고정은 배율 적용이 끝난 뒤에.
            ClientSize = new Size(DesignDialogWidth, DesignDialogHeight);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = WindowBackground;
            ForeColor = Foreground;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.None;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(20, 16, 20, 16),
                BackColor = WindowBackground
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            root.Controls.Add(new Label
            {
                Text = "처음 한 번만 연결 정보를 저장합니다",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Point),
                ForeColor = Foreground,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            var card = CreateGroup("Discord 연결");
            card.Margin = new Padding(0);
            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(2),
                BackColor = PanelBackground
            };
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            fields.Controls.Add(new Label
            {
                Text = "저장된 정보가 있으면 이 창은 다시 나타나지 않습니다.",
                Dock = DockStyle.Fill,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            _tokenEntry.Dock = DockStyle.Fill;
            _tokenEntry.Margin = new Padding(0, 3, 0, 3);
            fields.Controls.Add(_tokenEntry, 0, 1);
            var showToken = new SettingsCheckBox
            {
                Text = "입력 내용 표시",
                AutoSize = true,
                ForeColor = Foreground,
                Margin = new Padding(0, 7, 0, 0)
            };
            showToken.CheckedChanged += (_, _) => _tokenEntry.UseSystemPasswordChar = !showToken.Checked;
            fields.Controls.Add(showToken, 0, 2);
            card.Controls.Add(fields);
            root.Controls.Add(card, 0, 1);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = WindowBackground
            };
            var save = CreateButton("저장하고 시작", (_, _) => SaveAndClose(), width: 132);
            var cancel = CreateButton("나중에", (_, _) => { }, secondary: true, width: 86);
            cancel.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(save);
            footer.Controls.Add(cancel);
            root.Controls.Add(footer, 0, 2);
            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
            ApplyTheme(this);
            _scaler.Capture(this);
            _scaler.Apply(_uiScale);
            ClientSize = new Size(ScaledInt(DesignDialogWidth), ScaledInt(DesignDialogHeight));
            MinimumSize = Size;
            MaximumSize = Size;
        }

        public string GetToken() => _tokenEntry.Text.Trim();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDarkWindowChrome(Handle);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _tokenEntry.Focus();
        }

        private void SaveAndClose()
        {
            if (string.IsNullOrWhiteSpace(_tokenEntry.Text))
            {
                MessageBox.Show("연결 정보를 입력해 주세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _tokenEntry.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
