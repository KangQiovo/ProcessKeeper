using System.Diagnostics;

namespace ProcessKeeper.Core;

public sealed record PerformanceMemory(long WorkingSetBytes, long PrivateBytes);
public sealed record PerformanceSample(double FramesPerSecond, long? WorkingSetBytes, long? PrivateBytes,
    double IntervalSeconds, SystemPerformanceSample? System = null);

public static class CompactPerformanceText
{
    public static string Format(PerformanceSample sample, bool detailed, PerformanceDisplay display = PerformanceDisplay.InApp)
    {
        if (display == PerformanceDisplay.Overlay)
            return L.T("桌面") + " " + PerformanceMetricText.Fps(sample.System?.DesktopFramesPerSecond) + " FPS | CPU " + PerformanceMetricText.Percent(sample.System?.CpuPercent) + " | RAM " +
                PerformanceMetricText.GiB(sample.System?.UsedMemoryBytes) + " / " +
                PerformanceMetricText.GiB(sample.System?.TotalMemoryBytes) + " GiB" +
                (detailed ? " | " + L.T("可用") + " " + PerformanceMetricText.GiB(sample.System?.AvailableMemoryBytes) + " GiB" : "");
        return sample.FramesPerSecond.ToString("0") + " FPS | " + L.T("应用") + " " +
            PerformanceMetricText.MiB(sample.WorkingSetBytes) + " MiB | " + L.T("总内存") + " " +
            PerformanceMetricText.GiB(sample.System?.TotalMemoryBytes) + " GiB";
    }
}

public static class PerformanceMetricText
{
    public static string MiB(long? bytes) => bytes.HasValue ? (bytes.Value / (1024d * 1024)).ToString("0.#") : "—";
    public static string GiB(long? bytes) => bytes.HasValue ? (bytes.Value / (1024d * 1024 * 1024)).ToString("0.#") : "—";
    public static string Percent(double? value) => value.HasValue ? value.Value.ToString("0.#") + "%" : "—";
    public static string Fps(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0 ? value.Value.ToString("0.#") : "—";
    public static string Explanation(PerformanceSample sample, PerformanceDisplay display)
    {
        if (display == PerformanceDisplay.Overlay)
            return L.T("电脑：桌面 FPS | CPU | 已用 / 总内存 | 可用内存。") + "\n" +
                L.T("桌面 FPS 测量 Windows 桌面合成帧率；游戏独立呈现的帧率不在此测量范围内。") +
                (sample.System?.MultipleProcessorGroups == true ? "\n" + L.T("此电脑有多个处理器组，暂不显示不完整的 CPU 占用。") : "");
        return L.T("本应用：UI FPS | 工作集 / 专用内存 | 电脑总内存。") + "\n" +
            L.T("工作集") + " " + MiB(sample.WorkingSetBytes) + " MiB | " +
            L.T("专用内存") + " " + MiB(sample.PrivateBytes) + " MiB | " +
            L.T("总内存") + " " + GiB(sample.System?.TotalMemoryBytes) + " GiB";
    }
}

/// <summary>Counts this app's UI rendering callbacks, not monitor/game/GPU-present FPS.
/// The UI owns a one-second timer and unsubscribes frame callbacks when disabled.</summary>
public sealed class PerformanceSampler : IDisposable
{
    private readonly Func<double> _seconds;
    private readonly Func<PerformanceMemory> _memory;
    private readonly Process? _process;
    private long _frames;
    private double _last;
    private volatile bool _started, _disposed;
    private readonly object _gate = new();
    private long _generation;
    public PerformanceSampler(Func<double>? seconds = null, Func<PerformanceMemory>? memory = null)
    {
        _seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        if (memory is null)
        {
            _process = Process.GetCurrentProcess();
            _memory = () => { _process.Refresh(); return new PerformanceMemory(_process.WorkingSet64, _process.PrivateMemorySize64); };
        }
        else _memory = memory;
    }
    public bool IsRunning => _started && !_disposed;
    public void Start() { lock(_gate){if (_disposed) throw new ObjectDisposedException(nameof(PerformanceSampler)); if (_started) return; _generation++;_frames = 0; _last = _seconds(); _started = true;} }
    public void Stop() { lock(_gate){_generation++;_started = false; Interlocked.Exchange(ref _frames, 0);} }
    public void RecordFrame() { if (_started && !_disposed) Interlocked.Increment(ref _frames); }
    public PerformanceSample? Sample()
    {
        double elapsed;long frames,generation;
        lock(_gate)
        {
            if (!IsRunning) return null;
            var now = _seconds(); elapsed = now - _last;
            if (elapsed < 0.25) return null;
            _last = now; frames = Interlocked.Exchange(ref _frames, 0);generation=_generation;
        }
        PerformanceMemory? memory = null;
        try { memory = _memory(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or NotSupportedException) { }
        lock(_gate) return generation==_generation&&IsRunning?new PerformanceSample(frames / elapsed, memory?.WorkingSetBytes, memory?.PrivateBytes, elapsed):null;
    }
    public void Dispose() { lock(_gate){if (_disposed) return; Stop(); _disposed = true;}_process?.Dispose(); }
}
