using System.Diagnostics;
namespace DiscordChatHUD.Services;
internal sealed class AutomaticOnlineTracker
{
    private bool? _candidate;
    private long _since;
    private long _lastScan = long.MinValue;
    private bool _detected;
    public bool? Poll(bool enabled, bool current)
    {
        var now = Environment.TickCount64;
        if (!enabled) { Reset(); return null; }
        if (_lastScan == long.MinValue || now - _lastScan >= 1000)
        {
            _lastScan = now;
            Process[] processes = [];
            try
            {
                processes = Process.GetProcessesByName("gta5_Enhanced");
                _detected = processes.Any(p => !p.HasExited);
            }
            catch { Reset(); return null; } // Unreadable process state is not proof of exit.
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return Observe(_detected, current, now);
    }
    internal bool? Observe(bool detected, bool current, long milliseconds)
    {
        if (!detected) { _candidate = false; _since = milliseconds; return current ? false : null; }
        if (_candidate != detected) { _candidate = detected; _since = milliseconds; }
        return detected != current && milliseconds - _since >= 60_000 ? detected : null;
    }
    public void Reset() { _candidate = null; _lastScan = long.MinValue; }
}
