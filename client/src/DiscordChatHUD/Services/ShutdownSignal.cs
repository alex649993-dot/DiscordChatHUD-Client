namespace DiscordChatHUD.Services;

internal sealed class ShutdownSignal : IDisposable
{
    private const string SignalName = @"Local\DiscordChatHUD.CSharp.Beta.Shutdown.v1";
    private readonly EventWaitHandle _event;

    private ShutdownSignal(EventWaitHandle @event) => _event = @event;

    public WaitHandle WaitHandle => _event;

    public static ShutdownSignal CreateListener()
        => new(new EventWaitHandle(false, EventResetMode.AutoReset, SignalName));

    public static bool RequestShutdown()
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(SignalName);
            return existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose() => _event.Dispose();
}
