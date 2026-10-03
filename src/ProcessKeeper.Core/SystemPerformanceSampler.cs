using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

public sealed record SystemCpuTimes(ulong Idle, ulong Kernel, ulong User);
public sealed record SystemPhysicalMemory(long TotalBytes, long AvailableBytes);
public sealed record SystemDesktopComposition(ulong ComposedFrames, double Seconds);
public sealed record SystemPerformanceSample(double? CpuPercent, long? TotalMemoryBytes,
    long? AvailableMemoryBytes, bool MultipleProcessorGroups = false, double? DesktopFramesPerSecond = null)
{
    public long? UsedMemoryBytes => TotalMemoryBytes.HasValue && AvailableMemoryBytes.HasValue
        ? TotalMemoryBytes.Value - AvailableMemoryBytes.Value : null;
}

/// <summary>Constant-cost native reads, independent of the number of processes.
/// GetSystemTimes includes idle time in kernel time. On multi-group machines it
/// measures only the caller's group, so that partial result is deliberately unknown.</summary>
public sealed class SystemPerformanceSampler
{
    private readonly Func<SystemCpuTimes> _cpu;
    private readonly Func<SystemPhysicalMemory> _memory;
    private readonly Func<ushort> _groups;
    private readonly Func<SystemDesktopComposition> _desktop;
    private readonly object _gate = new();
    private SystemCpuTimes? _previous;
    private SystemDesktopComposition? _previousDesktop;
    private int _resetGeneration, _sampleGeneration;

    public SystemPerformanceSampler(Func<SystemCpuTimes>? cpu = null,
        Func<SystemPhysicalMemory>? memory = null, Func<ushort>? groups = null,
        Func<SystemDesktopComposition>? desktop = null)
    {
        _cpu = cpu ?? ReadCpu;
        _memory = memory ?? ReadMemory;
        _groups = groups ?? GetActiveProcessorGroupCount;
        _desktop = desktop ?? ReadDesktopComposition;
    }

    // Reset can be called by the UI while a background read is in flight.
    public void Reset() => Interlocked.Increment(ref _resetGeneration);

    public SystemPerformanceSample Sample()
    {
        lock (_gate)
        {
            var generation = Volatile.Read(ref _resetGeneration);
            if (_sampleGeneration != generation) { _previous = null; _previousDesktop = null; _sampleGeneration = generation; }
            double? percent = null;
            double? desktopFps = null;
            var multipleGroups = false;
            long? total = null, available = null;
            try
            {
                var groups = _groups();
                multipleGroups = groups > 1;
                if (groups == 1)
                {
                    var current = _cpu();
                    percent = CpuPercent(_previous, current);
                    _previous = current;
                }
                else _previous = null;
            }
            catch (Exception ex) when (Unreadable(ex)) { _previous = null; }
            try
            {
                var memory = _memory();
                if (memory.TotalBytes > 0 && memory.AvailableBytes >= 0 && memory.AvailableBytes <= memory.TotalBytes)
                { total = memory.TotalBytes; available = memory.AvailableBytes; }
            }
            catch (Exception ex) when (Unreadable(ex)) { }
            try
            {
                var desktop = _desktop();
                desktopFps = CompositionFramesPerSecond(_previousDesktop, desktop);
                _previousDesktop = desktop;
            }
            catch (Exception ex) when (Unreadable(ex)) { _previousDesktop = null; }
            if (generation != Volatile.Read(ref _resetGeneration))
            { _previous = null; _previousDesktop = null; percent = null; desktopFps = null; }
            return new(percent, total, available, multipleGroups, desktopFps);
        }
    }

    public static double? CpuPercent(SystemCpuTimes? previous, SystemCpuTimes current)
    {
        if (previous is null || current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
            return null;
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        if (ulong.MaxValue - kernel < user) return null;
        var total = kernel + user;
        if (total == 0 || idle > total) return null;
        return 100d * (total - idle) / total;
    }

    /// <summary>Measured DWM composition frames, independent of monitor refresh rate
    /// and this app's rendering callbacks. A suspended/reset interval is unknown.</summary>
    public static double? CompositionFramesPerSecond(SystemDesktopComposition? previous, SystemDesktopComposition current)
    {
        if (previous is null || current.ComposedFrames < previous.ComposedFrames) return null;
        var elapsed = current.Seconds - previous.Seconds;
        if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0.25 || elapsed > 5) return null;
        return (current.ComposedFrames - previous.ComposedFrames) / elapsed;
    }

    private static bool Unreadable(Exception error) => error is Win32Exception or UnauthorizedAccessException
        or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException;

    private static SystemCpuTimes ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(idle.Value, kernel.Value, user.Value);
    }
    private static SystemPhysicalMemory ReadMemory()
    {
        var value = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (!GlobalMemoryStatusEx(ref value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (value.TotalPhysical > long.MaxValue || value.AvailablePhysical > long.MaxValue)
            throw new InvalidOperationException("Physical memory exceeds the supported measurement range.");
        return new((long)value.TotalPhysical, (long)value.AvailablePhysical);
    }
    private static SystemDesktopComposition ReadDesktopComposition()
    {
        var timing = new DwmTimingInfo { Size = (uint)Marshal.SizeOf(typeof(DwmTimingInfo)) };
        // NULL requests desktop timing and is mandatory on Windows 8.1 onward.
        // cFrame is the frame actually composed at qpcCompose. Do not use
        // rateRefresh/rateCompose: these rates cannot establish observed FPS.
        var result = DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing);
        if (result < 0) throw new Win32Exception(result);
        if (timing.ComposeTime == 0) throw new InvalidOperationException("Desktop composition timing is unavailable.");
        return new(timing.ComposedFrame, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct UnsignedRatio { public uint Numerator, Denominator; }
    // dwmapi.h uses pshpack1.h; all QPC_TIME and DWM_FRAME_COUNT fields are ULONGLONG.
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct DwmTimingInfo
    {
        public uint Size;
        public UnsignedRatio RefreshRate;
        public ulong RefreshPeriod;
        public UnsignedRatio ComposeRate;
        public ulong VBlankTime, Refresh;
        public uint DxRefresh;
        public ulong ComposeTime, ComposedFrame;
        public uint DxPresent;
        public ulong RefreshFrame, FrameSubmitted;
        public uint DxPresentSubmitted;
        public ulong FrameConfirmed;
        public uint DxPresentConfirmed;
        public ulong RefreshConfirmed;
        public uint DxRefreshConfirmed;
        public ulong FramesLate;
        public uint FramesOutstanding;
        public ulong FrameDisplayed, FrameDisplayedTime, RefreshFrameDisplayed, FrameComplete, FrameCompleteTime,
            FramePending, FramePendingTime, FramesDisplayed, FramesComplete, FramesPending, FramesAvailable,
            FramesDropped, FramesMissed, RefreshNextDisplayed, RefreshNextPresented, RefreshesDisplayed,
            RefreshesPresented, RefreshStarted, PixelsReceived, PixelsDrawn, BuffersEmpty;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr window, ref DwmTimingInfo timing);
    [StructLayout(LayoutKind.Sequential)] private struct NativeFileTime
    {
        public uint Low, High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
}
