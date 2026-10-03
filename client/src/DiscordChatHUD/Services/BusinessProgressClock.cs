using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiscordChatHUD.Services;

// Windows awake time: UI stalls count in full; sleep and wall-clock changes do not.
internal sealed class BusinessProgressClock
{
    private readonly Func<ulong> _read;
    private ulong? _last;
    public BusinessProgressClock() : this(ReadAwakeTime) { }
    internal BusinessProgressClock(Func<ulong> read) => _read = read;
    public void Reset() => _last = _read();
    public double TakeElapsedSeconds()
    {
        var now = _read();
        var elapsed = _last is { } last && now >= last ? (now - last) / 10_000_000d : 0d;
        _last = now;
        return elapsed;
    }
    private static ulong ReadAwakeTime()
    {
        if (!QueryUnbiasedInterruptTime(out var value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return value;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
