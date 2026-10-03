using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace DiscordChatHUD.Services;

// One-shot high-resolution Windows deadline; never changes global timer resolution.
// Subscribers run on the UI thread, and stopped/replaced queued ticks are ignored.
internal sealed class AnimationDeadlineTimer : IDisposable
{
    private readonly Control owner;
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle wait;
    private int interval = 33, generation;
    private long due;
    private bool disposed;
    public bool Enabled { get; private set; }
    public event EventHandler? Tick;
    public AnimationDeadlineTimer(Control owner)
    {
        this.owner = owner;
        var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
        }
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        signal = new EventWaitHandle(false, EventResetMode.AutoReset);
        var originalHandle = signal.SafeWaitHandle;
        signal.SafeWaitHandle = handle;
        originalHandle.Dispose();
        wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => OnDeadline(), null, Timeout.Infinite, false);
    }
    public int Interval
    {
        get => interval;
        set { if (value < 1) throw new ArgumentOutOfRangeException(nameof(value)); interval = value; if (Enabled) Arm(); }
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Enabled) return;
        _ = owner.Handle;
        Enabled = true; Arm();
    }
    private void SetDeadline(int milliseconds)
    {
        long relative = -milliseconds * 10_000L;
        if (!SetWaitableTimer(signal.SafeWaitHandle, ref relative, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private void Arm()
    {
        Interlocked.Increment(ref generation);
        due = AnimationClock.NowMilliseconds + interval;
        SetDeadline(interval);
    }
    private void OnDeadline()
    {
        var version = Volatile.Read(ref generation);
        try { owner.BeginInvoke(() => Deliver(version)); }
        catch (InvalidOperationException) { }
    }
    private void Deliver(int version)
    {
        if (disposed || !Enabled || version != generation) return;
        var remaining = due - AnimationClock.NowMilliseconds;
        if (remaining > 0) { SetDeadline((int)Math.Min(remaining, int.MaxValue)); return; }
        Tick?.Invoke(this, EventArgs.Empty);
        if (!disposed && Enabled && version == generation) Arm();
    }
    public void Stop()
    {
        if (disposed) return;
        Enabled = false; Interlocked.Increment(ref generation);
        CancelWaitableTimer(signal.SafeWaitHandle);
    }
    public void Dispose()
    {
        if (disposed) return;
        Stop(); disposed = true; wait.Unregister(null); signal.Dispose();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
