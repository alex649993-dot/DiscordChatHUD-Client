using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — Windows 캡처 도구 감지와 HUD 임시 숨김.
internal sealed partial class OverlayForm
{

    private void UpdateCaptureProtectionRegistration()
    {
        if (!OperatingSystem.IsWindows() || !IsHandleCreated) return;

        // Per-pixel alpha HUDs rendered through UpdateLayeredWindow are not a
        // reliable SetWindowDisplayAffinity target. Some Windows builds reject
        // the call; others stop presenting the layered surface. Protect the
        // built-in capture paths at their input and full-screen selection
        // stages instead, without changing the HUD's display affinity.
        if (_config.ExcludeFromCapture)
        {
            InstallCaptureKeyboardHook();
            return;
        }

        RemoveCaptureKeyboardHook();
        EndCaptureGuard();
    }

    private void InstallCaptureKeyboardHook()
    {
        if (_captureKeyboardHook != IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        _captureKeyboardHookProc = CaptureKeyboardHookCallback;
        var module = NativeMethods.GetModuleHandleW(null);
        _captureKeyboardHook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhKeyboardLl,
            _captureKeyboardHookProc,
            module,
            0);
        if (_captureKeyboardHook == IntPtr.Zero)
            AppLog.Warn($"Windows 캡처 단축키 감시 시작 실패: Win32 {Marshal.GetLastWin32Error()}");
    }

    private void RemoveCaptureKeyboardHook()
    {
        if (_captureKeyboardHook != IntPtr.Zero)
        {
            if (!NativeMethods.UnhookWindowsHookEx(_captureKeyboardHook))
                AppLog.Warn($"Windows 캡처 단축키 감시 종료 실패: Win32 {Marshal.GetLastWin32Error()}");
            _captureKeyboardHook = IntPtr.Zero;
        }
        _captureKeyboardHookProc = null;
        _leftWindowsKeyDown = false;
        _rightWindowsKeyDown = false;
        _leftShiftKeyDown = false;
        _rightShiftKeyDown = false;
        _genericShiftKeyDown = false;
    }

    private IntPtr CaptureKeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _config.ExcludeFromCapture && !_closing)
        {
            var message = unchecked((int)wParam.ToInt64());
            var keyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var keyUp = message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp;
            if (keyDown || keyUp)
            {
                var input = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardInput>(lParam);
                var key = (Keys)input.VirtualKey;
                switch (key)
                {
                    case Keys.LWin:
                        _leftWindowsKeyDown = keyDown;
                        break;
                    case Keys.RWin:
                        _rightWindowsKeyDown = keyDown;
                        break;
                    case Keys.LShiftKey:
                        _leftShiftKeyDown = keyDown;
                        break;
                    case Keys.RShiftKey:
                        _rightShiftKeyDown = keyDown;
                        break;
                    case Keys.ShiftKey:
                        _genericShiftKeyDown = keyDown;
                        break;
                }

                if (keyDown && key == Keys.PrintScreen)
                    BeginCaptureGuard(awaitSelectionSurface: false);
                else if (keyDown
                         && key == Keys.S
                         && (_leftWindowsKeyDown || _rightWindowsKeyDown)
                         && (_leftShiftKeyDown || _rightShiftKeyDown || _genericShiftKeyDown))
                    BeginCaptureGuard(awaitSelectionSurface: true);
            }
        }

        return NativeMethods.CallNextHookEx(_captureKeyboardHook, code, wParam, lParam);
    }

    private void PollCaptureProtection()
    {
        if (!_config.ExcludeFromCapture)
        {
            if (_captureGuardActive) EndCaptureGuard();
            return;
        }

        // Fallback for machines where a global keyboard hook cannot be installed.
        var captureChordDown = NativeMethods.IsKeyDown(Keys.S)
                               && NativeMethods.IsKeyDown(Keys.ShiftKey)
                               && (NativeMethods.IsKeyDown(Keys.LWin) || NativeMethods.IsKeyDown(Keys.RWin));
        if (captureChordDown && !_captureChordWasDown)
            BeginCaptureGuard(awaitSelectionSurface: true);
        _captureChordWasDown = captureChordDown;

        var printScreenDown = NativeMethods.IsKeyDown(Keys.PrintScreen);
        if (printScreenDown && !_printScreenWasDown)
            BeginCaptureGuard(awaitSelectionSurface: false);
        _printScreenWasDown = printScreenDown;

        var now = DateTimeOffset.UtcNow;
        var selectionSurfaceActive = _captureSelectionSurfaceActive;
        var captureProbeInterval = _captureGuardActive
            ? TimeSpan.FromMilliseconds(50)
            : TimeSpan.FromMilliseconds(250);
        if (now - _lastCaptureSurfaceProbe >= captureProbeInterval)
        {
            _lastCaptureSurfaceProbe = now;
            selectionSurfaceActive = IsWindowsCaptureSelectionSurface();
            _captureSelectionSurfaceActive = selectionSurfaceActive;
        }

        if (selectionSurfaceActive)
        {
            BeginCaptureGuard(awaitSelectionSurface: true);
            _captureSelectionSeen = true;
        }

        if (!_captureGuardActive) return;

        var leftMouseDown = NativeMethods.IsKeyDown(Keys.LButton);
        if (_captureAwaitingSelectionInput && leftMouseDown)
            _captureSelectionMouseDown = true;

        var escapeDown = NativeMethods.IsKeyDown(Keys.Escape);
        var enterDown = NativeMethods.IsKeyDown(Keys.Enter);
        var selectionFinished = _captureAwaitingSelectionInput
                                && ((_captureSelectionSeen && !selectionSurfaceActive)
                                    || (_captureSelectionMouseDown && !leftMouseDown)
                                    || (escapeDown && !_captureEscapeWasDown)
                                    || (enterDown && !_captureEnterWasDown));
        _captureEscapeWasDown = escapeDown;
        _captureEnterWasDown = enterDown;
        if (selectionFinished)
        {
            _captureAwaitingSelectionInput = false;
            _captureGuardReleaseAfter = now.AddMilliseconds(900);
        }

        if (_captureAwaitingSelectionInput && now >= _captureGuardSafetyDeadline)
        {
            _captureAwaitingSelectionInput = false;
            _captureGuardReleaseAfter = now;
        }

        if (!_captureAwaitingSelectionInput && now >= _captureGuardReleaseAfter)
            EndCaptureGuard();
    }

    private void BeginCaptureGuard(bool awaitSelectionSurface)
    {
        EndPositionEdit(false);
        var now = DateTimeOffset.UtcNow;
        if (!_captureGuardActive)
        {
            _captureGuardActive = true;
            _captureGuardWasVisible = _actuallyVisible;
            _captureSelectionSeen = false;
            _captureAwaitingSelectionInput = awaitSelectionSurface;
            _captureSelectionMouseDown = false;
            _captureEscapeWasDown = NativeMethods.IsKeyDown(Keys.Escape);
            _captureEnterWasDown = NativeMethods.IsKeyDown(Keys.Enter);
            // An in-flight render may finish while hidden without presentation.
            // Refresh that scene after restoration, keeping the old surface ready.
            if (_renderLoopActive) _fullRenderRequired = true;
            HideOverlay();
            // Complete the desktop hide before forwarding the capture hotkey.
            // The game may have already hidden us on focus loss; flush that too.
            var flushResult = NativeMethods.DwmFlush();
            if (flushResult < 0) AppLog.Warn($"캡처 전 화면 합성 대기 실패: 0x{flushResult:X8}");
            AppLog.Info(awaitSelectionSurface
                ? "Windows 영역 캡처 감지: HUD 임시 숨김"
                : "Windows 즉시 캡처 감지: HUD 임시 숨김");
        }
        else if (awaitSelectionSurface)
        {
            _captureAwaitingSelectionInput = true;
        }

        if (awaitSelectionSurface)
            _captureGuardSafetyDeadline = now.AddMinutes(1);
        var hold = awaitSelectionSurface ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(1200);
        var requestedRelease = now.Add(hold);
        if (requestedRelease > _captureGuardReleaseAfter)
            _captureGuardReleaseAfter = requestedRelease;
    }

    private void EndCaptureGuard()
    {
        if (!_captureGuardActive) return;
        var shouldRestore = _captureGuardWasVisible && !_hudManuallyHidden;
        _captureGuardActive = false;
        _captureGuardWasVisible = false;
        _captureSelectionSeen = false;
        _captureSelectionSurfaceActive = false;
        _captureAwaitingSelectionInput = false;
        _captureSelectionMouseDown = false;
        _captureGuardReleaseAfter = DateTimeOffset.MinValue;
        _captureGuardSafetyDeadline = DateTimeOffset.MinValue;
        AppLog.Info("Windows 캡처 종료 감지: HUD 표시 조건 복구");
        if (shouldRestore && IsDisplayModeAllowed())
            ShowOverlay(reuseSurface: true);
    }

    private bool IsWindowsCaptureSelectionSurface()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || !NativeMethods.IsWindowVisible(foreground)) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == 0) return false;

        var nowTick = AnimationClock.NowMilliseconds;
        if (processId != _captureForegroundProcessId || nowTick >= _captureProcessRefreshTick)
        {
            // Foreground HWND/PID and capture rectangle are still checked at
            // the original cadence. Cache only the costly process-name query;
            // switching to a different process refreshes it immediately.
            _captureForegroundProcessId = processId;
            _captureForegroundIsHost = false;
            _captureProcessRefreshTick = nowTick + 2000;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                var processName = process.ProcessName;
                _captureForegroundIsHost = processName.Equals("SnippingTool", StringComparison.OrdinalIgnoreCase)
                    || processName.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase)
                    || processName.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase);
            }
            catch { _captureProcessRefreshTick = nowTick; }
        }
        if (!_captureForegroundIsHost) return false;
        if (!NativeMethods.GetWindowRect(foreground, out var rectangle)) return false;
        var screen = Screen.FromHandle(foreground).Bounds;
        return rectangle.Width >= screen.Width * 3 / 4
               && rectangle.Height >= screen.Height * 3 / 4;
    }
}
