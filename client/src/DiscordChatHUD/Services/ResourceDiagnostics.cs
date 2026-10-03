using System.Diagnostics;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

internal sealed class ResourceDiagnostics
{
    private long _lastTick = Environment.TickCount64;
    private double _lastCpu = ReadCpu();
    private long _lastTrimTick;
    private long _sampleTick = Environment.TickCount64;
    private double _sampleCpu = ReadCpu(), _peakCpu;
    private readonly object _renderGate = new();
    private int _fullRenders, _animationRenders, _sourceEvents, _mediaEvents;
    private double _renderMs, _renderMaxMs, _presentMs;

    internal void SourceChanged() => _sourceEvents++;
    internal void MediaReady() => _mediaEvents++;
    private long _presentBytes;
    private int _partialPresents, _fullPresents, _skippedPresents;
    internal void Presented(double ms, long bytes = 0, bool partial = false, bool submitted = true)
    {
        _presentMs += ms; _presentBytes += bytes;
        if (!submitted) _skippedPresents++;
        else if (partial) _partialPresents++;
        else _fullPresents++;
    }
    internal void Rendered(bool animation, double ms)
    {
        lock (_renderGate)
        {
            if (animation) _animationRenders++; else _fullRenders++;
            _renderMs += ms; _renderMaxMs = Math.Max(_renderMaxMs, ms);
        }
    }

    private static double ReadCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime.TotalMilliseconds;
    }

    public void Poll(MediaCache media, bool visible, bool rendering)
    {
        var now = Environment.TickCount64;
        // Reuse the HUD tick; do not race an unfinished visibility pass.
        if (!rendering && now - _lastTrimTick >= 1_000)
        {
            media.TrimIdle(visible);
            _lastTrimTick = now;
        }
        if (now - _sampleTick >= 1000)
        {
            try
            {
                var cpuNow = ReadCpu();
                var span = now - _sampleTick;
                var loadNow = (cpuNow - _sampleCpu) / span / Environment.ProcessorCount * 100;
                _peakCpu = Math.Max(_peakCpu, loadNow);
                if (loadNow >= 1.0)
                {
                    var current = media.GetDiagnostics();
                    AppLog.Info(FormattableString.Invariant($"PERF_SPIKE cpu={loadNow:F3}% sampleMs={span} pending={current.Pending} animated={current.Animated} rendering={rendering}"));
                }
                _sampleCpu = cpuNow; _sampleTick = now;
            }
            catch { _sampleTick = now; }
        }
        if (now - _lastTick < 30_000) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime.TotalMilliseconds;
            var load = (cpu - _lastCpu) / (now - _lastTick) / Environment.ProcessorCount * 100;
            _lastCpu = cpu;
            var stats = media.GetDiagnostics();
            var heap = GC.GetGCMemoryInfo();
            lock (_renderGate)
            {
                AppLog.Info(FormattableString.Invariant($"PERF_RENDER full={_fullRenders} animation={_animationRenders} sourceEvents={_sourceEvents} mediaEvents={_mediaEvents} renderWallMs={_renderMs:F1} maxRenderWallMs={_renderMaxMs:F1} presentWallMs={_presentMs:F1} copiedBytes={_presentBytes} partialPresents={_partialPresents} fullPresents={_fullPresents} skippedPresents={_skippedPresents}"));
                _fullRenders = _animationRenders = _sourceEvents = _mediaEvents = 0;
                _renderMs = _renderMaxMs = _presentMs = 0;
                _presentBytes = 0; _partialPresents = _fullPresents = _skippedPresents = 0;
            }
            AppLog.Info(FormattableString.Invariant($"PERF cpu={load:F3}% peakSample={_peakCpu:F3}% working={process.WorkingSet64 / 1048576d:F1}MiB private={process.PrivateMemorySize64 / 1048576d:F1}MiB managed={GC.GetTotalMemory(false) / 1048576d:F1}MiB committed={heap.TotalCommittedBytes / 1048576d:F1}MiB lastGcLive={(heap.HeapSizeBytes - heap.FragmentedBytes) / 1048576d:F1}MiB gen2={GC.CollectionCount(2)} finalizable={heap.FinalizationPendingCount} cache={stats.CacheBytes / 1048576d:F1}MiB items={stats.Items} animated={stats.Animated} pending={stats.Pending} handles={process.HandleCount} visible={visible} rendering={rendering}"));
        }
        catch (Exception ex) { AppLog.Warn($"성능 기록 실패: {ex.GetType().Name}"); }
        finally { _lastTick = now; _peakCpu = 0; }
    }
}
