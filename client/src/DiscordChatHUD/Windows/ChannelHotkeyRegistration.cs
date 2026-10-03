using System.Runtime.InteropServices;
namespace DiscordChatHUD.Windows;
internal sealed class ChannelHotkeyRegistration : IDisposable
{
 internal const int Id = 0x4324;
 private IntPtr _window;
 private ParsedHotkey? _key;
 internal bool Registered { get; private set; }
 internal int Error { get; private set; }
 internal void Update(IntPtr window, ParsedHotkey? key)
 {
  if(window==_window && key==_key)return;
  Dispose();_window=window;_key=key;Error=0;
  if(window==IntPtr.Zero || key is null || key.IsDisabled)return;
  Registered=RegisterHotKey(window,Id,Modifiers(key),(uint)key.Key);
  if(!Registered){Error=Marshal.GetLastWin32Error();Logging.AppLog.Info($"채널 단축키 Windows 등록 실패 code={Error} · 기존 키 감지 유지");}
 }
 internal static uint Modifiers(ParsedHotkey key)=>0x4000u | (key.Alt?1u:0) | (key.Control?2u:0) | (key.Shift?4u:0);
 public void Dispose(){if(Registered && _window!=IntPtr.Zero)UnregisterHotKey(_window,Id);Registered=false;_window=IntPtr.Zero;_key=null;}
 [DllImport("user32.dll",SetLastError=true)]private static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
 [DllImport("user32.dll")]private static extern bool UnregisterHotKey(IntPtr hwnd,int id);
}
