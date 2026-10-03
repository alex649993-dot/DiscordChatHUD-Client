using DiscordChatHUD.Models;
namespace DiscordChatHUD.Windows;
internal sealed class ChannelPickerForm : Form, IMessageFilter
{
    private readonly PickerListBox _list=new(){Dock=DockStyle.Fill,IntegralHeight=false,BorderStyle=BorderStyle.None};
    private readonly Action<ulong?> _done;
    private bool _finished, _filterInstalled;
    internal bool Opening;
    private ChannelSelectionInput? _selectionInput;
    // The global input capture must never outlive a forgotten picker.
    private readonly System.Windows.Forms.Timer _idleCancel=new(){Interval=30_000};
    internal bool RestoreGameFocus { get; private set; } = true;
    internal ChannelPickerForm(IReadOnlyList<ChannelPreset> channels,ulong selected,Rectangle hud,Action<ulong?> done)
    {
        _done=done;Text="HUD 채널 선택";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        StartPosition=FormStartPosition.Manual;AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("맑은 고딕",10);
        BackColor=Color.FromArgb(25,26,32);ForeColor=Color.White;Padding=new Padding(8);
        var work=Screen.FromRectangle(hud).WorkingArea;
        var width=Math.Min(Math.Max(200,Math.Min(hud.Width,340)),work.Width);
        var height=Math.Min(work.Height,Math.Min(320,90+channels.Count*30));
        Bounds=new Rectangle(Math.Clamp(hud.Left,work.Left,Math.Max(work.Left,work.Right-width)),Math.Clamp(hud.Top+8,work.Top,Math.Max(work.Top,work.Bottom-height)),width,height);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
        layout.RowStyles.Add(new(SizeType.Absolute,30));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,44));
        layout.Controls.Add(new Label{Text="채널 선택",AutoSize=true,ForeColor=Color.FromArgb(255,45,85)},0,0);
        _list.BackColor=BackColor;_list.ForeColor=ForeColor;_list.ItemHeight=30;_list.DrawMode=DrawMode.OwnerDrawFixed;
        _list.DrawItem+=(_,e)=>{if(e.Index<0)return;bool chosen=(e.State&DrawItemState.Selected)!=0;using var b=new SolidBrush(chosen?Color.FromArgb(133,30,56):BackColor);e.Graphics.FillRectangle(b,e.Bounds);TextRenderer.DrawText(e.Graphics,_list.Items[e.Index].ToString(),Font,Rectangle.Inflate(e.Bounds,-7,-3),Color.White,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        foreach(var c in channels)_list.Items.Add(c);
        _list.SelectedIndex=Math.Max(0,channels.ToList().FindIndex(c=>c.Id==selected));
        _list.MouseUp+=(_,e)=>{if(e.Button!=MouseButtons.Left)return;var index=_list.IndexFromPoint(e.Location);if(index>=0){_list.SelectedIndex=index;Confirm();}};
        _list.MouseWheel+=(_,e)=>{MoveSelection(e.Delta>0?-1:1);if(e is HandledMouseEventArgs h)h.Handled=true;};
        layout.Controls.Add(_list,0,1);
        layout.Controls.Add(new Label{Text="커서 위치 무관: ↑↓ / 휠 선택 · Enter 확정\nEsc 취소",Dock=DockStyle.Fill,ForeColor=Color.LightGray},0,2);
        Controls.Add(layout);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Application.AddMessageFilter(this);_filterInstalled=true;
        ActiveControl=_list;_list.Select();_list.Focus();
        // GTA keeps the foreground, so this window usually cannot take focus.
        // Gating on focus let the wheel reach GTA (raw input) while the list
        // under the cursor also scrolled. Capture while the picker is open.
        _selectionInput=new ChannelSelectionInput(
            ()=>!_finished && !IsDisposed && Visible,
            step=>BeginInvoke(new Action(()=>{if(!_finished)MoveSelection(step);})),
            save=>BeginInvoke(new Action(()=>{if(!_finished){if(save)Confirm();else Cancel();}})));
        _idleCancel.Tick+=(_,_)=>{_idleCancel.Stop();Cancel();};
        _idleCancel.Start();
        try{_selectionInput.Start();}
        catch(Exception ex){Logging.AppLog.Error("채널 선택 입력 연결 실패",ex);_selectionInput.Dispose();_selectionInput=null;}
    }
    public bool PreFilterMessage(ref Message message)
    {
        if(_finished || !IsHandleCreated)return false;
        var target=Control.FromHandle(message.HWnd);
        if(message.HWnd!=Handle && (target is null || !Contains(target)))return false;
        if(message.Msg is 0x100 or 0x104)
        {
            var key=(Keys)message.WParam.ToInt32() & Keys.KeyCode;
            if(key is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape)
            {
                if(key==Keys.Up)MoveSelection(-1);
                else if(key==Keys.Down)MoveSelection(1);
                else BeginInvoke(new Action(()=>{if(!_finished){if(key==Keys.Enter)Confirm();else Cancel();}}));
                return true;
            }
        }
        if(message.Msg is 0x101 or 0x105)
        {
            var key=(Keys)message.WParam.ToInt32() & Keys.KeyCode;
            if(key is Keys.Up or Keys.Down or Keys.Enter or Keys.Escape)return true;
        }
        return false;
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){_selectionInput?.Dispose();_selectionInput=null;_idleCancel.Dispose();}
        if(disposing && _filterInstalled){Application.RemoveMessageFilter(this);_filterInstalled=false;}
        base.Dispose(disposing);
    }
    protected override void OnDeactivate(EventArgs e){base.OnDeactivate(e);if(Opening)return;RestoreGameFocus=false;Cancel();}
    protected override void OnFormClosing(FormClosingEventArgs e){Cancel();base.OnFormClosing(e);}
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        switch(keyData & Keys.KeyCode){case Keys.Up:MoveSelection(-1);return true;case Keys.Down:MoveSelection(1);return true;case Keys.Enter:Confirm();return true;case Keys.Escape:Cancel();return true;}
        return base.ProcessCmdKey(ref msg,keyData);
    }
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x00000080;return p;}}
    internal void MoveSelection(int delta)
    {
        if(_idleCancel.Enabled){_idleCancel.Stop();_idleCancel.Start();}
        if(_list.Items.Count>0)_list.SelectedIndex=Wrap(_list.SelectedIndex,delta,_list.Items.Count);
    }
    internal static int Wrap(int index,int delta,int count)=>count<=0?-1:((index+delta)%count+count)%count;
    internal void Confirm()=>Finish((_list.SelectedItem as ChannelPreset)?.Id);
    internal void Cancel()=>Finish(null);
    private sealed class PickerListBox:ListBox
    {
        protected override void WndProc(ref Message m)
        {
            if(m.Msg==0x020A){var delta=unchecked((short)((m.WParam.ToInt64()>>16)&0xffff));OnMouseWheel(new HandledMouseEventArgs(MouseButtons.None,0,0,0,delta));return;}
            base.WndProc(ref m);
        }
    }
    private void Finish(ulong? id){if(_finished)return;_finished=true;_idleCancel.Stop();_selectionInput?.Dispose();_selectionInput=null;_done(id);}
}
