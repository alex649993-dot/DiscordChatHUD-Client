using DiscordChatHUD.Services;
namespace DiscordChatHUD.Windows;
internal sealed class PatchNotesForm : Form
{
    internal PatchNotesForm()
    {
        Text="DiscordChatHUD · 패치노트";
        AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96);
        Font=new Font("Segoe UI",11); ClientSize=new Size(580,390); MinimumSize=new Size(380,260);
        StartPosition=FormStartPosition.CenterParent; BackColor=Color.FromArgb(16,17,21); ForeColor=Color.White;
        Padding=new Padding(24);
        var text=new RichTextBox { ReadOnly=true, BorderStyle=BorderStyle.None, Dock=DockStyle.Fill, WordWrap=true,
            ScrollBars=RichTextBoxScrollBars.ForcedVertical, DetectUrls=false, BackColor=BackColor,ForeColor=ForeColor,
            Text=ReleaseNotes.FormatEntries(ReleaseNotes.Local().OrderByDescending(entry => entry.Version)) };
        Controls.Add(text);
        var stop = new CancellationTokenSource();
        Shown += async (_, _) =>
        {
            try
            {
                var update = await AppUpdate.Check(stop.Token);
                if (!IsDisposed && update.Manifest.Version > AppUpdate.CurrentVersion)
                    text.Text = $"업데이트 가능: Preview {AppUpdate.CurrentVersion} → {update.Manifest.Version}\r\n\r\n"
                        + ReleaseNotes.Format(update.Manifest, AppUpdate.CurrentVersion);
            }
            catch { /* Offline history remains available. */ }
        };
        FormClosed += (_, _) => { stop.Cancel(); };
    }
}
