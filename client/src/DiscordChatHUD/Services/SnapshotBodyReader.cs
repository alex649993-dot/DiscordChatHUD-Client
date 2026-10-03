using System.Diagnostics;
namespace DiscordChatHUD.Services;
internal sealed class SnapshotBodyStalledException : IOException
{
    internal SnapshotBodyStalledException() : base("Snapshot body made no progress") { }
}
internal sealed class SnapshotBodySlowException : IOException
{
    internal SnapshotBodySlowException() : base("Snapshot body exceeded freshness budget") { }
}
internal static class SnapshotBodyReader
{
    internal static async Task Read(Stream stream, MemoryStream body, TimeSpan idle, CancellationToken ct,
        Action<string>? diagnostic = null, int limit = 8 * 1024 * 1024, TimeSpan? freshness = null)
    {
        using var fresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if(freshness is {} budget)fresh.CancelAfter(budget);
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(fresh.Token);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        var started = Stopwatch.GetTimestamp();
        double first = -1, maxGap = 0;
        try
        {
            while (true)
            {
                var waiting = Stopwatch.GetTimestamp();
                progress.CancelAfter(idle);
                int read;
                try { read = await stream.ReadAsync(buffer.AsMemory(), progress.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && fresh.IsCancellationRequested)
                {
                    diagnostic?.Invoke(FormattableString.Invariant($"result=slow bytes={body.Length} bodyMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} firstByteMs={first:F1}"));
                    throw new SnapshotBodySlowException();
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && progress.IsCancellationRequested)
                {
                    diagnostic?.Invoke(FormattableString.Invariant($"result=stalled bytes={body.Length} bodyMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} idleMs={Stopwatch.GetElapsedTime(waiting).TotalMilliseconds:F1} firstByteMs={first:F1}"));
                    throw new SnapshotBodyStalledException();
                }
                finally { progress.CancelAfter(Timeout.InfiniteTimeSpan); }
                maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(waiting).TotalMilliseconds);
                if (read == 0) break;
                if (first < 0) first = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (body.Length + read > limit) throw new InvalidDataException("Snapshot body too large");
                body.Write(buffer, 0, read);
            }
            diagnostic?.Invoke(FormattableString.Invariant($"result=complete bytes={body.Length} bodyMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} maxReadGapMs={maxGap:F1} firstByteMs={first:F1}"));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}
