using System.Diagnostics;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

// One bounded warm-up after GTA starts. F7 and HUD shutdown cancel it and release its helper.
internal sealed class BusinessOcrWarmup : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private Task? task;
    private NightclubScreenReader.Session? session;
    private bool consumed;
    internal Task Completion { get { lock (gate) return task ?? Task.CompletedTask; } }
    internal int? ProcessId { get { lock (gate) return session?.ProcessId; } }

    internal void TryStart(bool eligible, TimeSpan? delay = null)
    {
        lock (gate)
        {
            if (!eligible || consumed) return;
            consumed = true;
            task = Task.Run(() => RunAsync(delay ?? TimeSpan.FromSeconds(5)));
        }
    }

    private async Task RunAsync(TimeSpan delay)
    {
        var elapsed = Stopwatch.StartNew();
        var completed = false;
        try
        {
            await Task.Delay(delay, stop.Token).ConfigureAwait(false);
            NightclubScreenReader.Session worker;
            lock (gate)
            {
                stop.Token.ThrowIfCancellationRequested();
                session = worker = new NightclubScreenReader.Session();
            }
            elapsed.Restart();
            await worker.WarmUpAsync(stop.Token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLog.Warn($"사업장 문자 인식 사전 준비 생략: {ex.GetType().Name}"); }
        finally
        {
            lock (gate) { session?.Dispose(); session = null; }
            if (completed) AppLog.Info($"사업장 문자 인식 사전 준비 완료: {elapsed.ElapsedMilliseconds}ms · 보조 프로그램 종료");
        }
    }

    internal async Task StopAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (gate)
        {
            consumed = true;
            stop.Cancel();
            session?.Dispose();
            session = null;
        }
    }
}
