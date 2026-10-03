using System.Runtime.InteropServices;
namespace DiscordChatHUD.Windows;
// Installed only for the lifetime of the channel picker. No game process access.
internal sealed class ChannelSelectionInput : IDisposable
{
    private delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
    private readonly Hook _mouseCallback,_keyCallback;
    private readonly Func<bool> _active;
    private readonly Action<int> _move;
    private readonly Action<bool> _finish;
    private IntPtr _mouse,_keyboard;
    private int _wheel;
    private bool _disposed;
    private readonly HashSet<int> _pressed=new();
    internal ChannelSelectionInput(Func<bool> active,Action<int> move,Action<bool> finish)
    {_active=active;_move=move;_finish=finish;_mouseCallback=Mouse;_keyCallback=Keyboard;}
    internal void Start()
    {
        _mouse=SetWindowsHookEx(14,_mouseCallback,GetModuleHandle(null),0);
        _keyboard=SetWindowsHookEx(13,_keyCallback,GetModuleHandle(null),0);
        if(_mouse==IntPtr.Zero || _keyboard==IntPtr.Zero)
        {var error=Marshal.GetLastWin32Error();Dispose();throw new System.ComponentModel.Win32Exception(error);}
    }
    internal bool Wheel(int delta)
    {
        if(_disposed || !_active())return false;
        _wheel+=delta;
        while(Math.Abs(_wheel)>=120){int step=_wheel>0?-1:1;_wheel+=step*120;_move(step);}
        return true;
    }
    internal bool Key(int key,bool down)
    {
        if(_disposed || !_active() || key is not (0x26 or 0x28 or 0x0D or 0x1B))return false;
        if(down){_pressed.Add(key);if(key==0x26)_move(-1);else if(key==0x28)_move(1);}
        else if(_pressed.Remove(key) && key is 0x0D or 0x1B)_finish(key==0x0D);
        return true;
    }
    private IntPtr Mouse(int code,IntPtr message,IntPtr data)
    {
        if(code>=0 && message.ToInt32()==0x20A && Wheel(unchecked((short)(Marshal.ReadInt32(data,8)>>16))))return new IntPtr(1);
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    }
    private IntPtr Keyboard(int code,IntPtr message,IntPtr data)
    {
        int msg=message.ToInt32();
        if(code>=0 && msg is 0x100 or 0x101 or 0x104 or 0x105 && Key(Marshal.ReadInt32(data),msg is 0x100 or 0x104))return new IntPtr(1);
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        if(_mouse!=IntPtr.Zero){UnhookWindowsHookEx(_mouse);_mouse=IntPtr.Zero;}
        if(_keyboard!=IntPtr.Zero){UnhookWindowsHookEx(_keyboard);_keyboard=IntPtr.Zero;}
        _pressed.Clear();
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int id,Hook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? name);
}