using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — HUD 위치·크기 조정, 모니터 배치, 위치 저장.
internal sealed partial class OverlayForm
{
    private void BeginPositionEdit()
    {
        if (_closing || _positionEditor is not null || _captureGuardActive) return;
        CloseChannelPicker(null,false);
        _positionBeforeEdit=Bounds; _positionWasVisible=_actuallyVisible;
        _positionEditor=new HudPositionEditor(this,EndPositionEdit);
        try
        {
            SetInputEnabled(false);
            NativeMethods.ShowWindow(Handle,NativeMethods.SwShowNoActivate); _actuallyVisible=true;
            RequestRender(); _positionEditor.Show();
        }
        catch(Exception ex) { EndPositionEdit(false); AppLog.Error("HUD 위치 조정 시작 실패",ex); }
    }
    private void EndPositionEdit(bool save)
    {
        if(_positionEditor is null)return;
        var editor=_positionEditor; _positionEditor=null;
        try
        {
            if(save)
            {
                var work=Screen.FromRectangle(Bounds).WorkingArea;
                Location=new Point(Math.Clamp(Left,work.Left,Math.Max(work.Left,work.Right-Width)),
                    Math.Clamp(Top,work.Top,Math.Max(work.Top,work.Bottom-Height)));
                _geometryDirty=true;SaveGeometry();
            }
            else {Bounds=_positionBeforeEdit;_geometryDirty=false;}
        }
        finally
        {
            editor.Dispose(); SetInputEnabled(false);
            if(!_positionWasVisible)HideOverlay();
            _lastVisibilityPoll=DateTimeOffset.MinValue;
            RequestRender();
        }
    }
    private void SetInputEnabled(bool enabled)
    {
        if (!IsHandleCreated) return;
        var style = NativeMethods.GetWindowLongPtrW(Handle, NativeMethods.GwlExStyle).ToInt64();
        if (enabled) style &= ~NativeMethods.WsExTransparent;
        else style |= NativeMethods.WsExTransparent;
        style = NormalizeTaskbarStyle(style);
        NativeMethods.SetWindowLongPtrW(Handle, NativeMethods.GwlExStyle, new IntPtr(style));
        NativeMethods.SetWindowPos(
            Handle,
            NativeMethods.HwndTopMost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        _inputEnabled = enabled;
    }

    private int HitTest(IntPtr lParam)
    {
        var packed = lParam.ToInt64();
        var screenX = unchecked((short)(packed & 0xffff));
        var screenY = unchecked((short)((packed >> 16) & 0xffff));
        var x = screenX - Left;
        var y = screenY - Top;
        const int border = 12;
        var tintTop = Math.Clamp(_mainTintTop > 0 ? _mainTintTop : 1, 1, Math.Max(1, Height - border));
        var tintBottom = Math.Clamp(_mainTintBottom > 0 ? _mainTintBottom : Height, border, Height);
        if (y < tintTop - border || y > tintBottom + border) return NativeMethods.HtTransparent;
        var left = x < border;
        var right = x >= Width - border;
        var top = y >= tintTop - border && y < tintTop + border;
        var bottom = y >= tintBottom - border && y <= tintBottom + border;
        if (left && top) return NativeMethods.HtTopLeft;
        if (right && top) return NativeMethods.HtTopRight;
        if (left && bottom) return NativeMethods.HtBottomLeft;
        if (right && bottom) return NativeMethods.HtBottomRight;
        if (left) return NativeMethods.HtLeft;
        if (right) return NativeMethods.HtRight;
        if (top) return NativeMethods.HtTop;
        if (bottom) return NativeMethods.HtBottom;
        if (y >= tintTop && y <= tintTop + PaddingHeaderHeight) return NativeMethods.HtCaption;
        return NativeMethods.HtTransparent;
    }

    private const int PaddingHeaderHeight = 48;

    private Rectangle CalculateInitialBounds()
    {
        var preset = _config.ActivePreset;
        var screen = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        var size = CalculatePresetSize(preset, screen);
        var width = size.Width;
        var height = size.Height;
        var x = preset.PositionX is not null && preset.PositionY is not null && preset.PositionSpace == "work_area"
            ? screen.Left + preset.PositionX.Value
            : screen.Right - width - 8;
        var y = preset.PositionX is not null && preset.PositionY is not null && preset.PositionSpace == "work_area"
            ? screen.Top + preset.PositionY.Value
            : screen.Top + (int)Math.Round(screen.Height * 0.16) + 10;
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private void RepositionForGameMonitor()
    {
        if (_positionEditor is not null) return;
        if (_preview || !_game.Refresh()) return;
        var work = _game.GetWorkArea();
        var preset = _config.ActivePreset;
        // Automatic presets must be sized from the monitor GTA actually uses.
        // Otherwise a HUD opened from an FHD primary display stayed FHD-sized
        // after moving to a 2560x1440 or 3440x1440 game display.
        var size = CalculatePresetSize(preset, work);
        var x = preset.PositionX is not null && preset.PositionSpace == "work_area"
            ? work.Left + preset.PositionX.Value
            : work.Right - size.Width - 8;
        var y = preset.PositionY is not null && preset.PositionSpace == "work_area"
            ? work.Top + preset.PositionY.Value
            : work.Top + (int)(work.Height * 0.16) + 10;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));
        var bounds = new Rectangle(x, y, size.Width, size.Height + _liveMessageExtraHeight);
        if (Bounds != bounds) Bounds = bounds;
    }

    private static Size CalculatePresetSize(HudLayoutPreset preset, Rectangle workArea)
    {
        var width = preset.Width > 0
            ? preset.Width
            : Math.Clamp((int)Math.Round(workArea.Width * (430d / 2560d)), 300, 430);
        var height = preset.Height > 0
            ? preset.Height
            : Math.Clamp((int)Math.Round(workArea.Height * 0.52d), 320, 1400);
        return new Size(
            Math.Clamp(width, HudLayoutPreset.MinimumWidth, Math.Max(HudLayoutPreset.MinimumWidth, Math.Min(HudLayoutPreset.MaximumWidth, workArea.Width))),
            Math.Clamp(height, HudLayoutPreset.MinimumHeight, Math.Max(HudLayoutPreset.MinimumHeight, Math.Min(HudLayoutPreset.MaximumHeight, workArea.Height))));
    }

    private void SaveGeometry()
    {
        if (_closing && !IsHandleCreated) return;
        // Geometry writes reload the JSON. Flush the authoritative live clock
        // first so the write cannot reintroduce a previous save's countdown.
        if (!IsConfigBusinessTrackerActive()) SaveBusinessProgress();
        ConfigStore.UpdateGeometry(_config.ActiveLayoutPreset, new Rectangle(Left, Top, Width, Height - _liveMessageExtraHeight));
        var preset = _config.ActivePreset;
        var work = Screen.FromRectangle(Bounds).WorkingArea;
        preset.Width = Width;
        preset.Height = Height - _liveMessageExtraHeight;
        preset.PositionX = Left - work.Left;
        preset.PositionY = Top - work.Top;
        preset.PositionSpace = "work_area";
        preset.PositionWorkWidth = work.Width;
        preset.PositionWorkHeight = work.Height;
        _geometryDirty = false;
        // This process already applied its own geometry; do not reload older
        // business fractions from this same write on the next settings poll.
        _lastConfigWrite = File.Exists(AppPaths.ConfigPath)
            ? File.GetLastWriteTimeUtc(AppPaths.ConfigPath)
            : DateTime.MinValue;
    }
}
