using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — 채널 선택 창과 채널 전환.
internal sealed partial class OverlayForm
{

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    private void OpenChannelPicker()
    {
        if(_channelPicker is not null || !_actuallyVisible || _captureGuardActive)return;
        var channels=_config.ChannelPresets.Where(c=>c.Id!=0).DistinctBy(c=>c.Id).ToArray();
        if(channels.Length==0)return;
        _pickerGameWindow=NativeMethods.GetForegroundWindow();
        SetInputEnabled(false);
        _channelPicker=new ChannelPickerForm(channels,_config.TargetChannelId,Bounds,id=>CloseChannelPicker(id,_channelPicker?.RestoreGameFocus==true));
        var picker = _channelPicker;
        picker.Opening = true;
        picker.Show(this); picker.BringToFront(); picker.Activate();
        // GTA reads the wheel through raw input, which the picker's low-level hook
        // cannot block. The game only gets raw input while it is the foreground
        // window, so the picker must really take the foreground. A plain
        // SetForegroundWindow from this background process is usually refused
        // (foreground lock); attaching to the game's input queue lets it through.
        var front = !picker.IsDisposed && ForceForeground(picker.Handle);
        picker.Opening = false;
        if (!front)
        {
            CloseChannelPicker(null, false);
            ShowTrayNotice("채널 선택", "채널 선택창을 앞으로 띄우지 못했습니다. GTA로 돌아온 뒤 단축키를 다시 눌러주세요.", false);
        }
        AppLog.Info(front ? "채널 선택 창 전면 전환 성공" : "채널 선택 창 전면 전환 실패 · 입력 차단 해제");
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static bool ForceForeground(IntPtr window)
    {
        if (SetForegroundWindow(window) && NativeMethods.GetForegroundWindow() == window) return true;
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var current = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally { if (attached) AttachThreadInput(current, foregroundThread, false); }
        return NativeMethods.GetForegroundWindow() == window;
    }
    private void CloseChannelPicker(ulong? selected,bool returnToGame)
    {
        if(_channelPicker is null)return;
        var picker=_channelPicker;_channelPicker=null;picker.Dispose();
        _hotkeysNeedRelease=true;
        if(returnToGame && _pickerGameWindow!=IntPtr.Zero)SetForegroundWindow(_pickerGameWindow);
        _lastVisibilityPoll=DateTimeOffset.MinValue;
        if(selected is {} id && id!=_config.TargetChannelId) _=SwitchChannelAsync(id);
    }
    private async Task SwitchChannelAsync(ulong id)
    {
        if(_channelSwitchBusy || _discord is not SwitchableChatSource source || !_config.ChannelPresets.Any(c=>c.Id==id))return;
        _channelSwitchBusy=true;
        try
        {
            await source.SwitchAsync(id);
            if(_closing)return;
            ConfigStore.UpdateTargetChannel(id);_config.TargetChannelId=id;
            _scrollPixels=0;_manualScrollPinnedUntil=DateTimeOffset.MinValue;
            _manualScrollLiveMessage=null;_manualScrollFrozenMessages=null;
            RequestRender();
        }
        catch(Exception ex){AppLog.Error("채널 전환 실패",ex);}
        finally
        {
            _channelSwitchBusy=false;
            // Reconcile the source with the latest catalog even if its target
            // disappeared while the old connection was being disposed.
            _lastConfigWrite=DateTime.MinValue;
        }
    }
}
