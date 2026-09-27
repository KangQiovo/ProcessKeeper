using System.Collections.ObjectModel;
using System.Diagnostics;

namespace ProcessKeeper.Core;

/// <summary>Read-only preparation followed by an explicit, confirmed restart transaction.</summary>
public sealed class AvdRestartService
{
    private readonly IAvdRestartHost _host;
    private readonly AvdRestartTiming _timing;
    private int _executing;

    public AvdRestartService(IAvdRestartHost? host = null, AvdRestartTiming? timing = null)
    {
        _host = host ?? new AvdRestartHost();
        _timing = timing ?? AvdRestartTiming.Default;
        if (_timing.StableSamples < 3 || _timing.PollInterval <= TimeSpan.Zero ||
            _timing.ExitTimeout <= TimeSpan.Zero || _timing.WindowTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timing));
    }

    public async Task<AvdDiscoveryResult> DiscoverAsync(IReadOnlyList<ProcessRecord> scope, CancellationToken cancellationToken = default)
    {
        var plans = new List<AvdRestartPlan>();
        var notices = new List<string>();
        var snapshot = _host.Capture();
        var launchers = snapshot.Processes.Where(process => process.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase) &&
            scope.Any(selected => SameIdentity(selected, process))).ToArray();
        foreach (var launcher in launchers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _host.ValidateIdentity(launcher);
                var engines = snapshot.Processes.Where(process => process.ParentId == launcher.Id && IsSdkEngine(launcher, process)).ToArray();
                if (engines.Length != 1) throw new InvalidOperationException(L.T("无法唯一确定这个启动器的虚拟机核心，请等待启动完成后刷新。"));
                var engine = engines[0];
                EnsureTopology(launcher, engine);
                _host.ValidateIdentity(engine);
                var sourceArguments = _host.ReadArguments(launcher).ToArray();
                var prepared = AvdArgumentPolicy.Prepare(sourceArguments, launcher.Path);
                var environment = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(_host.ReadEnvironment(launcher), StringComparer.OrdinalIgnoreCase));
                ValidateEnvironmentPaths(environment);
                var profile = environment["USERPROFILE"];
                var possiblePorts = _host.ReadListeners().Where(listener => listener.ProcessId == engine.Id &&
                    listener.Port is >= 5554 and <= 5682 && listener.Port % 2 == 0 &&
                    (prepared.ConsolePort is null || prepared.ConsolePort == listener.Port)).Select(listener => listener.Port).Distinct().ToArray();
                var ports = new List<int>();
                foreach (var port in possiblePorts)
                {
                    if (!PortOwnedBy(port, engine.Id)) continue;
                    try
                    {
                        if (await _host.GetAvdNameAsync(engine, port, profile, cancellationToken).ConfigureAwait(false) == prepared.AvdName) ports.Add(port);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { /* A listener is not automatically an emulator console. */ }
                }
                if (ports.Count != 1) throw new InvalidOperationException(L.T("无法通过模拟器控制接口唯一核实设备名称与端口；原实例未被修改。"));
                var arguments = WithPort(prepared, ports[0]);
                var plan = new AvdRestartPlan(prepared.AvdName, launcher, engine, ports[0], Array.AsReadOnly(sourceArguments),
                    Array.AsReadOnly(arguments), environment, prepared.RemovedOptions, _host.FileHash(launcher.Path));
                if (!_host.FileExists(plan.GuiEnginePath) || plan.GuiEnginePath.Contains("-headless", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(L.T("这个 SDK 没有对应的原生窗口版模拟器核心，未提供重启操作。"));
                plans.Add(plan);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { notices.Add($"PID {launcher.Id} | {exception.Message}"); }
        }
        if (launchers.Length == 0) notices.Add(L.T("没有找到可核实的 emulator.exe 启动器；仅有孤立核心时无法可靠重建原启动环境。"));
        return new AvdDiscoveryResult(plans, notices);
    }

    public async Task<AvdRestartResult> ExecuteAsync(AvdRestartPlan plan, IProgress<AvdRestartProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            return new(false, L.T("已有一个 AVD 重启操作正在进行，请等待完成。"), false, false);
        bool shutdownRequested = false, launchStarted = false;
        IAvdStartedProcess? launched = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(L.T("核实目标"), L.F($"正在复核 {plan.AvdName} 的进程、端口和启动参数。")));
            await RevalidateAsync(plan, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(L.T("退出原实例"), L.F($"正在请求 {plan.AvdName} 通过自身控制接口退出，最多等待 {_timing.ExitTimeout.TotalSeconds:0} 秒。")));
            // Once the request starts, failure/cancellation cannot promise that the old VM is untouched.
            shutdownRequested = true;
            await _host.ShutdownAsync(plan.Engine, plan.ConsolePort, plan.AvdName, plan.UserProfile, cancellationToken).ConfigureAwait(false);
            var exitWatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = _host.Capture();
                if (DefinitelyGone(plan.Engine, snapshot) && DefinitelyGone(plan.Launcher, snapshot) &&
                    !_host.ReadListeners().Any(listener => listener.Port == plan.ConsolePort || listener.Port == plan.AdbPort)) break;
                if (exitWatch.Elapsed >= _timing.ExitTimeout)
                    return new(false, L.T("旧实例尚未完全退出，或原端口被其他进程占用；没有启动第二份模拟器，也没有强制结束进程。"), true, false);
                await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!_host.FileHash(plan.Launcher.Path).Equals(plan.LauncherSha256, StringComparison.OrdinalIgnoreCase) ||
                !_host.FileExists(plan.GuiEnginePath))
                return new(false, L.T("原实例已退出，但 SDK 文件已经变化或原生核心缺失，已停止后续启动。"), true, false);
            progress?.Report(new(L.T("启动原生窗口"), L.F($"正在以带窗口模式启动 {plan.AvdName}；进程创建本身不代表页面已打开。")));
            launchStarted = true;
            launched = _host.Launch(plan);
            if (!AvdGuiMatcher.SamePath(launched.Identity.Path, plan.Launcher.Path) || launched.Identity.StartTimeUtcTicks <= 0)
                return new(false, L.T("启动后无法核实新进程身份，未报告页面启动成功。"), true, true);

            var windowWatch = Stopwatch.StartNew();
            (int Pid, long Started, nint Window)? stable = null;
            int stableSamples = 0;
            progress?.Report(new(L.T("等待真实窗口"), L.F($"正在等待 {plan.AvdName} 的原生页面，最多 {_timing.WindowTimeout.TotalSeconds:0} 秒；控制台与命令行窗口不计入。")));
            while (windowWatch.Elapsed < _timing.WindowTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = FindGuiCandidate(plan, launched, _host.Capture());
                if (candidate is { } found)
                {
                    var key = (found.Process.Id, found.Process.StartTimeUtcTicks, found.Window.Handle);
                    stableSamples = stable == key ? stableSamples + 1 : 1;
                    stable = key;
                    if (stableSamples >= _timing.StableSamples)
                    {
                        _host.ValidateIdentity(found.Process);
                        if (await _host.GetAvdNameAsync(found.Process, plan.ConsolePort, plan.UserProfile, cancellationToken).ConfigureAwait(false) != plan.AvdName)
                            return new(false, L.T("新窗口对应的设备名称发生变化，未报告页面启动成功，请检查运行列表。"), true, true);
                        // Querying the console takes time. Require a fresh matching window after it returns.
                        var final = FindGuiCandidate(plan, launched, _host.Capture());
                        if (final is { } confirmed && confirmed.Process.Id == key.Id &&
                            confirmed.Process.StartTimeUtcTicks == key.StartTimeUtcTicks && confirmed.Window.Handle == key.Handle)
                            return new(true, L.F($"已确认 {plan.AvdName} 的原生窗口持续可见。Android 系统可能仍在启动；未将控制台或启动器进程视为页面。"),
                                true, true, confirmed.Process, confirmed.Window);
                        stable = null; stableSamples = 0;
                    }
                }
                else { stable = null; stableSamples = 0; }
                await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            return new(false, L.T("已发出启动请求，但等待超时仍未确认对应 AVD 的原生可见窗口。请检查 SDK、显卡或模拟器提示；新进程保留，未重复启动。"), true, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, launchStarted ? L.T("已停止等待窗口；启动请求已经发送，新模拟器进程保留。") : shutdownRequested
                ? L.T("已取消后续启动；原实例已收到退出请求，请刷新确认其状态。") : L.T("已取消，尚未请求退出或启动模拟器。"), shutdownRequested, launchStarted);
        }
        catch (Exception exception)
        {
            return new(false, (launchStarted ? L.T("启动后验证未完成：") : shutdownRequested ? L.T("重启未完成，原实例可能已退出：") : L.T("目标核实失败，尚未修改原实例：")) +
                exception.Message, shutdownRequested, launchStarted);
        }
        finally { launched?.Dispose(); Interlocked.Exchange(ref _executing, 0); }
    }

    private async Task RevalidateAsync(AvdRestartPlan plan, CancellationToken token)
    {
        EnsureTopology(plan.Launcher, plan.Engine);
        _host.ValidateIdentity(plan.Launcher);
        _host.ValidateIdentity(plan.Engine);
        if (!_host.ReadArguments(plan.Launcher).SequenceEqual(plan.SourceArguments, StringComparer.Ordinal))
            throw new InvalidOperationException(L.T("原启动参数已经变化，请重新选择设备。"));
        var prepared = AvdArgumentPolicy.Prepare(plan.SourceArguments, plan.Launcher.Path);
        if (prepared.AvdName != plan.AvdName || !WithPort(prepared, plan.ConsolePort).SequenceEqual(plan.LaunchArguments, StringComparer.Ordinal))
            throw new InvalidOperationException(L.T("重启计划与原设备参数不一致。"));
        var environment = _host.ReadEnvironment(plan.Launcher);
        ValidateEnvironmentPaths(environment);
        if (environment.Count != plan.Environment.Count || environment.Any(pair => !plan.Environment.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidOperationException(L.T("原启动环境已经变化，未按新环境启动其他 AVD。"));
        if (!_host.FileHash(plan.Launcher.Path).Equals(plan.LauncherSha256, StringComparison.OrdinalIgnoreCase) || !_host.FileExists(plan.GuiEnginePath))
            throw new InvalidOperationException(L.T("模拟器 SDK 文件已经变化或缺失，请重新准备。"));
        if (!PortOwnedBy(plan.ConsolePort, plan.Engine.Id) ||
            await _host.GetAvdNameAsync(plan.Engine, plan.ConsolePort, plan.UserProfile, token).ConfigureAwait(false) != plan.AvdName)
            throw new InvalidOperationException(L.T("控制端口或设备名称已经变化，请刷新列表。"));
    }

    private (ProcessRecord Process, WindowRecord Window)? FindGuiCandidate(AvdRestartPlan plan, IAvdStartedProcess launched, ProcessSnapshot snapshot)
    {
        foreach (var process in snapshot.Processes.Where(process => AvdGuiMatcher.IsLaunchMember(process, plan, launched)))
        {
            if (!PortOwnedBy(plan.ConsolePort, process.Id)) continue;
            var window = process.Windows.FirstOrDefault(window => AvdGuiMatcher.IsGuiWindow(window, plan.AvdName, plan.ConsolePort));
            if (window is null) continue;
            try
            {
                _host.ValidateIdentity(process);
                if (AvdGuiMatcher.IdentifiesAvd(_host.ReadArguments(process), plan.AvdName, plan.ConsolePort)) return (process, window);
            }
            catch (Exception) { /* A stale snapshot is not window evidence. */ }
        }
        return null;
    }

    private bool PortOwnedBy(int port, int processId)
    {
        var owners = _host.ReadListeners().Where(listener => listener.Port == port).Select(listener => listener.ProcessId).Distinct().ToArray();
        return owners.Length == 1 && owners[0] == processId;
    }

    private static string[] WithPort(AvdLaunchArguments prepared, int port)
    {
        if (port is < 5554 or > 5682 || port % 2 != 0 || prepared.ConsolePort is { } expected && expected != port)
            throw new InvalidOperationException(L.T("控制端口与原启动参数不一致。"));
        return prepared.ConsolePort is null ? [.. prepared.Arguments, "-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)] : prepared.Arguments.ToArray();
    }

    private static bool SameIdentity(ProcessRecord left, ProcessRecord right) => left.Id == right.Id &&
        left.StartTimeUtcTicks > 0 && left.StartTimeUtcTicks == right.StartTimeUtcTicks && AvdGuiMatcher.SamePath(left.Path, right.Path);

    private static bool DefinitelyGone(ProcessRecord expected, ProcessSnapshot snapshot)
    {
        var current = snapshot.Processes.FirstOrDefault(process => process.Id == expected.Id);
        return current is null || current.StartTimeUtcTicks > 0 && current.StartTimeUtcTicks != expected.StartTimeUtcTicks;
    }

    private static void ValidateEnvironmentPaths(IReadOnlyDictionary<string, string> environment)
    {
        if (!environment.TryGetValue("USERPROFILE", out var profile) || !Path.IsPathFullyQualified(profile))
            throw new InvalidOperationException(L.T("无法读取原模拟器的用户目录，未按当前环境猜测 AVD 位置。"));
        // The retained environment keys all contain paths. The original working
        // directory is unknown, so a relative value could select another AVD
        // when the new process starts in the SDK directory. Reject before exit.
        foreach (var (key, value) in environment)
            if (value.Length != 0 && !Path.IsPathFullyQualified(value))
                throw new InvalidOperationException(L.F($"原模拟器的 {key} 使用相对路径，无法可靠还原设备目录；未请求退出。"));
    }

    private static bool IsSdkEngine(ProcessRecord launcher, ProcessRecord engine)
    {
        var engineRoot = Path.Combine(Path.GetDirectoryName(launcher.Path) ?? "", "qemu") + Path.DirectorySeparatorChar;
        return Path.IsPathFullyQualified(engine.Path) && engine.Path.StartsWith(engineRoot, StringComparison.OrdinalIgnoreCase) &&
            engine.Name.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase) && engine.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureTopology(ProcessRecord launcher, ProcessRecord engine)
    {
        if (!Path.GetFileName(launcher.Path).Equals("emulator.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(launcher.Path) || !IsSdkEngine(launcher, engine) || engine.ParentId != launcher.Id ||
            launcher.StartTimeUtcTicks <= 0 || engine.StartTimeUtcTicks < launcher.StartTimeUtcTicks ||
            launcher.SessionId <= 0 || launcher.SessionId != engine.SessionId || launcher.OwnerSid.Length == 0 || launcher.OwnerSid != engine.OwnerSid)
            throw new InvalidOperationException(L.T("无法核实同一 SDK、账户与会话下的启动器和虚拟机核心关系。"));
    }
}
