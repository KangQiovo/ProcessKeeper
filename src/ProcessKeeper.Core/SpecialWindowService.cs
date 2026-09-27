using System.Diagnostics;
using System.Security.Principal;

namespace ProcessKeeper.Core;

public sealed class SpecialWindowService
{
    private readonly ISpecialWindowHost _host;
    private readonly SpecialWindowTiming _timing;
    private int _running;
    public SpecialWindowService(ISpecialWindowHost? host = null, SpecialWindowTiming? timing = null)
    {
        _host = host ?? new SpecialWindowHost();
        _timing = timing ?? SpecialWindowTiming.Default;
        if (_timing.StableSamples < 3 || _timing.WindowTimeout <= TimeSpan.Zero || _timing.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timing));
    }
    public Task<SpecialWindowDiscovery> DiscoverAsync(IReadOnlyList<ProcessRecord> scope, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var plans = new List<SpecialWindowPlan>();
        var notices = new List<string>();
        var snapshot = _host.Capture();
        foreach (var selected in scope.Where(p => SpecialWindowPolicy.IsSupportedName(p.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var source = snapshot.Processes.SingleOrDefault(p => SameIdentity(p, selected));
                if (source is null) continue;
                bool hyperV = source.Name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase);
                _host.ValidateSource(source, hyperV);
                var arguments = hyperV ? Array.Empty<string>() : _host.ReadArguments(source);
                var plan = SpecialWindowPolicy.Prepare(source, arguments, _host);
                if (plan is not null && !plans.Any(p => p.Kind == plan.Kind && p.ExecutablePath == plan.ExecutablePath && p.TargetId == plan.TargetId))
                    plans.Add(plan);
            }
            catch (Exception exception) { notices.Add($"PID {selected.Id} | {exception.Message}"); }
        }
        return new SpecialWindowDiscovery(plans, notices);
    }, cancellationToken);

    public async Task<SpecialWindowResult> ExecuteAsync(SpecialWindowPlan plan, bool confirmed,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!confirmed) return new(false, L.T("已取消，未发送任何启动或连接请求。"), false);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return new(false, L.T("已有窗口操作正在进行。"), false);
        bool requested = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(L.T("正在复核进程和界面入口…"));
            bool hyperV = plan.Kind == SpecialWindowKind.HyperV;
            _host.ValidateSource(plan.Source, hyperV);
            var arguments = hyperV ? Array.Empty<string>() : _host.ReadArguments(plan.Source);
            if (!arguments.SequenceEqual(plan.SourceArguments, StringComparer.Ordinal))
                throw new InvalidOperationException(L.T("进程启动参数已变化，请重新选择。"));
            var fresh = SpecialWindowPolicy.Prepare(plan.Source, arguments, _host);
            if (fresh is null || fresh.Kind != plan.Kind || fresh.TargetId != plan.TargetId || fresh.TargetTitle != plan.TargetTitle ||
                !SpecialWindowPolicy.SamePath(fresh.ExecutablePath, plan.ExecutablePath) || fresh.ExecutableHash != plan.ExecutableHash ||
                !fresh.Arguments.SequenceEqual(plan.Arguments, StringComparer.Ordinal))
                throw new InvalidOperationException(L.T("界面入口或目标设备已变化，未启动。"));
            var before = _host.Capture();
            if (!before.Processes.Any(p => SameIdentity(p, plan.Source)))
                throw new InvalidOperationException(L.T("原进程已经退出或变化，未发送启动请求。"));
            _host.ValidateSource(plan.Source, hyperV);
            if (!hyperV && !_host.ReadArguments(plan.Source).SequenceEqual(plan.SourceArguments, StringComparer.Ordinal))
                throw new InvalidOperationException(L.T("原进程参数已变化，未发送启动请求。"));
            if (plan.Kind != SpecialWindowKind.TrayApplication)
            {
                var liveVm = _host.ReadVirtualMachine(plan.Source, plan.Kind, plan.TargetId);
                if (liveVm is null || liveVm.ProcessId != plan.Source.Id || !SameTarget(plan, liveVm.Id) || liveVm.Name != plan.TargetTitle)
                    throw new InvalidOperationException(L.T("设备映射已经变化，未发送连接请求。"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            requested = true;
            _host.Launch(plan);
            progress?.Report(L.T("已发送请求，正在等待对应的真实窗口…"));
            var watch = Stopwatch.StartNew();
            (int Pid, long Started, nint Handle)? previous = null;
            int stable = 0;
            while (watch.Elapsed < _timing.WindowTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = _host.Capture();
                var source = snapshot.Processes.SingleOrDefault(p => SameIdentity(p, plan.Source));
                if (source is null) return new(false, L.T("原后台进程已退出；无法确认当前界面仍对应所选实例。"), true);
                (ProcessRecord Process, WindowRecord Window)? match = null;
                foreach (var process in snapshot.Processes.Where(p => SpecialWindowPolicy.SamePath(p.Path, plan.ExecutablePath)))
                {
                    if (process.StartTimeUtcTicks <= 0 || process.SessionId <= 0 ||
                        (plan.Kind != SpecialWindowKind.HyperV && (process.SessionId != source.SessionId || process.OwnerSid != source.OwnerSid))) continue;
                    try
                    {
                        _host.ValidateSource(process, false);
                        var guiArguments = _host.ReadArguments(process);
                        if (!MatchesTarget(plan, guiArguments)) continue;
                        foreach (var window in process.Windows.Where(SpecialWindowPolicy.IsRealGuiWindow))
                        {
                            if (!MatchesWindowTitle(plan, window.Title)) continue;
                            if (plan.Kind == SpecialWindowKind.VirtualBox && (!window.ClassName.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) ||
                                !window.ClassName.Contains("QWindow", StringComparison.OrdinalIgnoreCase))) continue;
                            // An already-visible unrelated application window is not evidence of this request.
                            bool alreadyVisible = before.Processes.Where(p => SameIdentity(p, process)).SelectMany(p => p.Windows)
                                .Any(w => w.Handle == window.Handle && SpecialWindowPolicy.IsRealGuiWindow(w));
                            if (alreadyVisible && plan.Kind == SpecialWindowKind.TrayApplication && !window.IsForeground) continue;
                            match = (process, window); break;
                        }
                    }
                    catch { /* Inaccessible or changing GUI candidates cannot establish success. */ }
                    if (match is not null) break;
                }
                if (match is { } found)
                {
                    var key = (found.Process.Id, found.Process.StartTimeUtcTicks, found.Window.Handle);
                    stable = previous == key ? stable + 1 : 1; previous = key;
                    if (stable >= _timing.StableSamples)
                    {
                        _host.ValidateSource(found.Process, false);
                        _host.ValidateSource(plan.Source, hyperV);
                        var finalSnapshot = _host.Capture();
                        var finalProcess = finalSnapshot.Processes.SingleOrDefault(p => SameIdentity(p, found.Process));
                        if (finalProcess is null || !finalProcess.Windows.Any(w => w.Handle == found.Window.Handle &&
                            SpecialWindowPolicy.IsRealGuiWindow(w) && w.Title == found.Window.Title) ||
                            !finalSnapshot.Processes.Any(p => SameIdentity(p, plan.Source)) ||
                            !MatchesTarget(plan, _host.ReadArguments(finalProcess)))
                        { previous = null; stable = 0; continue; }
                        if (plan.Kind != SpecialWindowKind.TrayApplication)
                        {
                            var finalVm = _host.ReadVirtualMachine(plan.Source, plan.Kind, plan.TargetId);
                            if (finalVm is null || finalVm.ProcessId != plan.Source.Id || !SameTarget(plan, finalVm.Id) || finalVm.Name != plan.TargetTitle)
                                return new(false, L.T("窗口出现后设备映射发生变化，未报告显示成功。"), true);
                            var afterProvider = _host.Capture();
                            var latest = afterProvider.Processes.SingleOrDefault(p => SameIdentity(p, found.Process));
                            if (latest is null || !afterProvider.Processes.Any(p => SameIdentity(p, plan.Source)) ||
                                !latest.Windows.Any(w => w.Handle == found.Window.Handle && SpecialWindowPolicy.IsRealGuiWindow(w) && w.Title == found.Window.Title) ||
                                !MatchesTarget(plan, _host.ReadArguments(latest)))
                            { previous = null; stable = 0; continue; }
                        }
                        return new(true, plan.Kind == SpecialWindowKind.TrayApplication
                            ? L.T("已确认程序的主窗口持续可见。") : L.T("已确认对应虚拟机的图形连接窗口持续可见；来宾系统的就绪状态请以窗口内显示为准。"),
                            true, found.Process, found.Window);
                    }
                }
                else { previous = null; stable = 0; }
                await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            return new(false, L.T("请求已发送，但未核实对应的可见主窗口；没有把命令行、管理器首页或进程启动当成成功。"), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(false, requested ? L.T("已停止等待；已打开的界面保留。") : L.T("已取消，未发送启动请求。"), requested); }
        catch (Exception exception) { return new(false, L.T("窗口操作未完成 | ") + exception.Message, requested); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
    public static bool MatchesTarget(SpecialWindowPlan plan, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || !SpecialWindowPolicy.SamePath(arguments[0], plan.ExecutablePath)) return false;
        if (arguments.Skip(1).Any(a => a.StartsWith("--type", StringComparison.OrdinalIgnoreCase))) return false;
        if (plan.Kind == SpecialWindowKind.TrayApplication) return true;
        if (plan.Kind == SpecialWindowKind.VMware)
        {
            var configs = arguments.Skip(1).Where(a => a.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase)).ToArray();
            return configs.Length == 1 && SpecialWindowPolicy.SamePath(configs[0], plan.TargetId);
        }
        var options = plan.Kind == SpecialWindowKind.HyperV ? new[] { "-G" } : ["--startvm", "-startvm", "-s"];
        var indices = Enumerable.Range(1, arguments.Count - 1).Where(i => options.Contains(arguments[i], StringComparer.OrdinalIgnoreCase)).ToArray();
        return indices.Length == 1 && indices[0] + 1 < arguments.Count && Guid.TryParse(arguments[indices[0] + 1], out var id) &&
            Guid.TryParse(plan.TargetId, out var expected) && id == expected;
    }
    public static bool MatchesWindowTitle(SpecialWindowPlan plan, string title)
    {
        if (plan.Kind == SpecialWindowKind.TrayApplication) return true;
        var name = plan.TargetTitle;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (title.Equals(name, StringComparison.Ordinal)) return true;
        if (plan.Kind == SpecialWindowKind.VMware)
            return title.Equals(name + " - VMware Workstation", StringComparison.Ordinal) ||
                title.StartsWith(name + " - VMware Workstation ", StringComparison.Ordinal) ||
                title.Equals(name + " - VMware Player", StringComparison.Ordinal) ||
                title.StartsWith(name + " - VMware Player ", StringComparison.Ordinal);
        if (plan.Kind == SpecialWindowKind.VirtualBox)
            return title.StartsWith(name + " [", StringComparison.Ordinal) && title.Contains("] - ", StringComparison.Ordinal) &&
                title.EndsWith("VirtualBox", StringComparison.Ordinal) ||
                title.Equals(name + " - Oracle VirtualBox", StringComparison.Ordinal) ||
                title.Equals(name + " - Oracle VM VirtualBox", StringComparison.Ordinal);
        // VMConnect binds one GUID. Include the actual local host to keep a
        // similarly named VM from passing a loose substring title check.
        return title.StartsWith(name + " on " + Environment.MachineName + " - ", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith(name + " 在 " + Environment.MachineName + " 上 - ", StringComparison.OrdinalIgnoreCase) ||
            title.Equals(name + " - Virtual Machine Connection", StringComparison.Ordinal) ||
            title.Equals(name + " - 虚拟机连接", StringComparison.Ordinal);
    }
    private static bool SameIdentity(ProcessRecord left, ProcessRecord right) => left.Id == right.Id &&
        left.StartTimeUtcTicks > 0 && left.StartTimeUtcTicks == right.StartTimeUtcTicks && SpecialWindowPolicy.SamePath(left.Path, right.Path);
    private static bool SameTarget(SpecialWindowPlan plan, string actual) => plan.Kind == SpecialWindowKind.VMware
        ? SpecialWindowPolicy.SamePath(actual, plan.TargetId) : Guid.TryParse(actual, out var id) && Guid.TryParse(plan.TargetId, out var expected) && id == expected;
}
