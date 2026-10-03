using DiscordChatHUD.Interop;
namespace DiscordChatHUD.Windows;

// Temporary desktop windows only: no game hooks, screenshots, or display changes.
internal sealed class HudPositionEditor : IDisposable
{
    private readonly Form _hud;
    private readonly Action<bool> _finish;
    private readonly List<Form> _shades = new();
    private readonly Form _bar;
    private Point _startMouse, _startHud;
    private Rectangle _startBounds; private int _corner;
    private bool _dragging, _disposed, _finished;
    internal HudPositionEditor(Form hud, Action<bool> finish)
    {
        _hud = hud; _finish = finish;
        foreach (var screen in Screen.AllScreens)
        {
            var shade = new EditorWindow(() => Finish(true), () => Finish(false))
            {
                FormBorderStyle=FormBorderStyle.None, ShowInTaskbar=false, TopMost=true, StartPosition=FormStartPosition.Manual, AutoScaleMode=AutoScaleMode.None,
                NoActivate = true, Bounds = screen.Bounds, BackColor = Color.Black, Opacity = .28,
                Cursor = Cursors.Default
            };
            shade.Paint += (_, e) =>
            {
                var box = _hud.Bounds; box.Offset(-shade.Left, -shade.Top); box.Inflate(3,3);
                using var pen = new Pen(Color.White, 3); e.Graphics.DrawRectangle(pen, box);
            };
            shade.MouseDown += (_, e) =>
            {
                if(e.Button != MouseButtons.Left) return;
                _corner=HitCorner(_hud.Bounds,Cursor.Position);
                if(_corner==0 && !_hud.Bounds.Contains(Cursor.Position))return;
                _startBounds=_hud.Bounds;
                _dragging = true; _startMouse = Cursor.Position; _startHud = _hud.Location; shade.Capture = true;
            };
            shade.MouseMove += (_, _) =>
            {
                if (_dragging)
                {
                    if(_corner==0)_hud.Location = Offset(_startHud, _startMouse, Cursor.Position);
                    else _hud.Bounds=ResizeCorner(_startBounds,_startMouse,Cursor.Position,_corner);
                    foreach(var s in _shades) s.Invalidate();
                }
                else {var corner=HitCorner(_hud.Bounds,Cursor.Position);shade.Cursor=corner is 1 or 4 ? Cursors.SizeNWSE : corner is 2 or 3 ? Cursors.SizeNESW : corner is 5 or 6 ? Cursors.SizeWE : corner is 7 or 8 ? Cursors.SizeNS : _hud.Bounds.Contains(Cursor.Position) ? Cursors.SizeAll : Cursors.Default;}
            };
            shade.MouseUp += (_, _) => { _dragging=false; shade.Capture=false; RaiseToolbar(); };
            shade.MouseCaptureChanged += (_, _) => { if(!shade.Capture) _dragging=false; };
            _shades.Add(shade);
        }
        var work = Screen.FromRectangle(hud.Bounds).WorkingArea;
        _bar = new EditorWindow(() => Finish(true), () => Finish(false))
        {
            FormBorderStyle=FormBorderStyle.None, ShowInTaskbar=false, TopMost=true, StartPosition=FormStartPosition.Manual,
            BackColor = Color.FromArgb(25,26,32), ForeColor=Color.White,
            Font=new Font("맑은 고딕",10), AutoScaleMode=AutoScaleMode.Dpi,
            Size=new Size(Math.Min(540,work.Width),90),
            Location=new Point(work.Left+Math.Max(0,(work.Width-540)/2),work.Top+20)
        };
        var row=new FlowLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(12), WrapContents=true };
        row.Controls.Add(new Label { Text="내부: 이동 · 테두리: 크기 · Enter 완료 / Esc 취소",AutoSize=true,Margin=new Padding(3,3,15,8) });
        var save=new Button {Text="완료",AutoSize=true,BackColor=Color.FromArgb(255,40,88),ForeColor=Color.White};
        var cancel=new Button {Text="취소",AutoSize=true,BackColor=Color.FromArgb(55,56,62),ForeColor=Color.White};
        save.Click+=(_,_)=>Finish(true); cancel.Click+=(_,_)=>Finish(false);
        row.Controls.Add(save);row.Controls.Add(cancel);_bar.Controls.Add(row);
        _bar.AcceptButton=save; _bar.CancelButton=cancel;
    }
    internal static int HitCorner(Rectangle bounds,Point p)
    {
        const int grip=12;
        bool left=Math.Abs(p.X-bounds.Left)<=grip,right=Math.Abs(p.X-bounds.Right)<=grip;
        bool top=Math.Abs(p.Y-bounds.Top)<=grip,bottom=Math.Abs(p.Y-bounds.Bottom)<=grip;
        if(left&&top)return 1;if(right&&top)return 2;if(left&&bottom)return 3;if(right&&bottom)return 4;
        if(p.Y>=bounds.Top && p.Y<=bounds.Bottom){if(left)return 5;if(right)return 6;}
        if(p.X>=bounds.Left && p.X<=bounds.Right){if(top)return 7;if(bottom)return 8;}
        return 0;
    }
    internal static Rectangle ResizeCorner(Rectangle start,Point anchor,Point mouse,int corner)
    {
        int dx=mouse.X-anchor.X,dy=mouse.Y-anchor.Y;
        bool left=corner is 1 or 3 or 5,top=corner is 1 or 2 or 7;
        bool right=corner is 2 or 4 or 6,bottom=corner is 3 or 4 or 8;
        int l=left?Math.Min(start.Left+dx,start.Right-200):start.Left;
        int r=right?Math.Max(start.Right+dx,start.Left+200):start.Right;
        int t=top?Math.Min(start.Top+dy,start.Bottom-200):start.Top;
        int b=bottom?Math.Max(start.Bottom+dy,start.Top+200):start.Bottom;
        return Rectangle.FromLTRB(l,t,r,b);
    }
    internal static Point Offset(Point start,Point anchor,Point mouse) => new(start.X+mouse.X-anchor.X,start.Y+mouse.Y-anchor.Y);
    internal void Show()
    {
        foreach(var shade in _shades) shade.Show();
        NativeMethods.SetWindowPos(_hud.Handle,NativeMethods.HwndTopMost,0,0,0,0,
            NativeMethods.SwpNoMove|NativeMethods.SwpNoSize|NativeMethods.SwpNoActivate);
        _bar.Show(); _bar.Activate();
    }
    private void RaiseToolbar()
    {
        if (_disposed) return;
        NativeMethods.SetWindowPos(_bar.Handle,NativeMethods.HwndTopMost,0,0,0,0,
            NativeMethods.SwpNoMove|NativeMethods.SwpNoSize|NativeMethods.SwpNoActivate);
    }
    private void Finish(bool save)
    {
        if(_disposed || _finished)return;
        _finished=true;_dragging=false;
        foreach(var shade in _shades)shade.Capture=false;
        _finish(save);
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_dragging=false;
        foreach(var shade in _shades){shade.Capture=false;shade.Dispose();}
        _bar.Dispose();
    }
    private sealed class EditorWindow(Action save, Action cancel) : Form
    {
        internal bool NoActivate;
        protected override bool ShowWithoutActivation=>NoActivate;
        protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
        {
            if(keyData==Keys.Enter){save();return true;}
            if(keyData==Keys.Escape){cancel();return true;}
            return base.ProcessCmdKey(ref msg,keyData);
        }
        protected override void WndProc(ref Message m)
        {
            // The dimming surface must never activate and cover the completion toolbar.
            if(NoActivate && m.Msg==0x0021){m.Result=new IntPtr(3);return;}
            base.WndProc(ref m);
        }
        protected override CreateParams CreateParams
        {get{var p=base.CreateParams;p.ExStyle|=0x80;if(NoActivate)p.ExStyle|=0x08000000;return p;}}
        protected override void OnFormClosing(FormClosingEventArgs e)
        {if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;cancel();}base.OnFormClosing(e);}
    }
}