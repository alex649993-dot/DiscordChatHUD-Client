using System.Diagnostics;
using DiscordChatHUD.Interop;

namespace DiscordChatHUD.Services;

internal sealed class GameWindowTracker : IDisposable
{
    private static readonly string[] ProcessNames = ["GTA5_Enhanced", "GTA5Enhanced", "GTA5"];
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private IntPtr _window;
    private Process? _process;

    public IntPtr Window => _window;
    public bool SeenOnce { get; private set; }

    public bool IsGameAlive
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    public bool Refresh(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _lastScan < TimeSpan.FromMilliseconds(1500))
            return _window != IntPtr.Zero;
        _lastScan = DateTimeOffset.UtcNow;

        if (_process is not null && !IsGameAlive)
        {
            _process.Dispose();
            _process = null;
        }

        if (_window != IntPtr.Zero && IsTrackedWindowValid(out var pid))
        {
            TrackProcess(pid);
            SeenOnce = true;
            return true;
        }

        _window = IntPtr.Zero;
        // Discover a supported process first. Do not open every desktop
        // application's process just to discover that GTA is not running.
        if (!IsGameAlive)
        {
            foreach (var name in ProcessNames)
            {
                var candidates = Process.GetProcessesByName(name);
                try
                {
                    foreach (var candidate in candidates)
                    {
                        try { if (!candidate.HasExited) { TrackProcess((uint)candidate.Id); break; } }
                        catch { }
                    }
                }
                finally { foreach (var candidate in candidates) candidate.Dispose(); }
                if (IsGameAlive) break;
            }
        }
        if (!IsGameAlive) return false;
        uint targetPid;
        try { targetPid = (uint)_process!.Id; } catch { return false; }
        var bestScore = int.MinValue;
        uint bestPid = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != targetPid) return true;
            if (!IsCandidateWindow(hwnd, out var pid, out var score)) return true;
            if (score <= bestScore) return true;
            bestScore = score;
            bestPid = pid;
            _window = hwnd;
            return true;
        }, IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            try { _process!.Refresh(); _window = _process.MainWindowHandle; } catch { }
        }
        else
        {
            TrackProcess(bestPid);
        }

        if (_window != IntPtr.Zero)
        {
            SeenOnce = true;
            return true;
        }
        return false;
    }

    public bool IsForeground()
    {
        if (_window == IntPtr.Zero || !NativeMethods.IsWindow(_window)) return false;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || NativeMethods.IsCloaked(foreground)) return false;
        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        uint trackedPid;
        try { trackedPid = _process is { HasExited: false } ? (uint)_process.Id : 0; }
        catch { return false; }

        // Never infer foreground state from a title such as a Discord channel
        // containing "GTA5 Enhanced". The active window must belong to the
        // exact GTA process currently being tracked.
        if (foregroundPid == 0 || trackedPid == 0 || foregroundPid != trackedPid) return false;
        // The window and process were fully validated when first discovered.
        // A matching tracked PID is enough here; repeating title/class/process
        // validation several times per second only burns CPU.
        return foregroundPid == trackedPid;
    }

    public Rectangle GetWorkArea()
    {
        if (_window != IntPtr.Zero)
        {
            var monitor = NativeMethods.MonitorFromWindow(_window, NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(monitor, ref info)) return info.Work.ToRectangle();
        }
        return Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
    }

    private static bool IsCandidateWindow(IntPtr hwnd, out uint processId, out int score)
    {
        processId = 0;
        score = 0;
        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd)
                || NativeMethods.IsIconic(hwnd) || NativeMethods.IsCloaked(hwnd)) return false;
            if (!NativeMethods.GetWindowRect(hwnd, out var rectangle) || rectangle.Width * (long)rectangle.Height <= 50_000)
                return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            var title = NativeMethods.GetWindowText(hwnd).ToLowerInvariant();
            var className = NativeMethods.GetClassName(hwnd).ToLowerInvariant();
            var classMatch = className.Contains("grcwindow", StringComparison.Ordinal);
            var titleMatch = title.Contains("grand theft auto", StringComparison.Ordinal)
                             || title.Contains("gta v", StringComparison.Ordinal)
                             || title.Contains("gta5", StringComparison.Ordinal);
            var processMatch = false;
            var enhancedMatch = title.Contains("enhanced", StringComparison.Ordinal);
            if (processId > 0)
            {
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    var name = process.ProcessName;
                    processMatch = IsSupportedGameProcess(name);
                    name = name.ToLowerInvariant();
                    enhancedMatch |= name.Contains("enhanced", StringComparison.Ordinal);
                }
                catch { }
            }
            if (classMatch) score += 120;
            if (titleMatch) score += 80;
            if (processMatch) score += 90;
            if (enhancedMatch) score += 120;
            score += Math.Min(60, (int)(rectangle.Width * (long)rectangle.Height / 180_000));
            // Window titles and classes only rank windows inside the verified
            // game process. They must never make Discord or a browser a game.
            return processMatch;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedGameProcess(string processName)
        => ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));

    private bool IsTrackedWindowValid(out uint processId)
    {
        processId = 0;
        if (_window == IntPtr.Zero || !NativeMethods.IsWindow(_window) || !IsGameAlive) return false;
        NativeMethods.GetWindowThreadProcessId(_window, out processId);
        if (processId == 0) return false;
        try { return _process is { HasExited: false } && (uint)_process.Id == processId; }
        catch { return false; }
    }

    private void TrackProcess(uint processId)
    {
        if (processId == 0) return;
        try
        {
            if (_process is { Id: var existingId } && existingId == processId && !_process.HasExited) return;
        }
        catch { }
        _process?.Dispose();
        _process = null;
        try { _process = Process.GetProcessById((int)processId); } catch { }
    }

    public void Dispose()
    {
        _process?.Dispose();
        _process = null;
    }
}
