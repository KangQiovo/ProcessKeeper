namespace ProcessKeeper.Core;

/// <summary>Requests one client shutdown, never kills a child, service, game, or newly observed PID.</summary>
public sealed class SteamShutdownService
{
    private readonly ISteamShutdownHost _host;
    private readonly SteamShutdownTiming _timing;
    private static readonly HashSet<string> ClientNames = new(StringComparer.OrdinalIgnoreCase)
    { "steam.exe", "steamwebhelper.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe", "gameoverlayui.exe", "steam_monitor.exe", "steamxboxutil.exe", "steamxboxutil64.exe" };

    public SteamShutdownService(ISteamShutdownHost? host = null, SteamShutdownTiming? timing = null)
    {
        _host = host ?? new SteamShutdownHost(); _timing = timing ?? SteamShutdownTiming.Default;
        if (_timing.ExitTimeout <= TimeSpan.Zero || _timing.StablePeriod <= TimeSpan.Zero ||
            _timing.PollInterval <= TimeSpan.Zero || _timing.StablePeriod >= _timing.ExitTimeout)
            throw new ArgumentOutOfRangeException(nameof(timing));
    }

    public static bool IsSteam(ProcessRecord process) => process.ApplicationKey == "known:steam" ||
        ClientNames.Contains(process.Name) || process.Name.Equals("steamservice.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool IsClient(ProcessRecord p) => ClientNames.Contains(p.Name) && p.SessionId > 0 &&
        !p.IsSystem && !p.IsSelf && p.Services.Count == 0 && p.Id > 4 && p.StartTimeUtcTicks > 0 &&
        p.OwnerSid.Length > 0 && ProtectionPolicy.TryNormalizePath(p.Path, out _) &&
        Path.GetFileName(p.Path).Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
        !p.Path.Split('\\', '/').Any(segment => segment.Equals("steamapps", StringComparison.OrdinalIgnoreCase));

    public async Task<SteamShutdownResult> CloseAsync(IReadOnlyList<ProcessRecord> frozen,
        Func<ProcessRecord, ProtectionDecision> recheck, CancellationToken token = default, IProgress<string>? progress = null)
    {
        var selected = frozen.DistinctBy(p => (p.Id, p.StartTimeUtcTicks)).ToArray();
        var held = new List<IDisposable>();
        bool requested = false;
        var observed = new Dictionary<(int, long), ProcessRecord>();
        var last = ProcessSnapshot.Empty;
        SteamShutdownResult Finish(string message, bool success, bool pending)
        {
            bool CanOffer(ProcessRecord p)
            {
                try { return IsClient(p) && p.OwnerSid == _host.CurrentUserSid && p.SessionId == _host.CurrentSessionId &&
                    last.Processes.Any(now => ProcessRespawnObserver.SameIdentity(now, p)) && !recheck(p).Protected; }
                catch { return false; }
            }
            var pendingTargets = pending ? selected.Where(CanOffer).ToArray() : [];
            return new(selected.Select(p => new CloseResult(p.Id, p.Name, message, success)).ToArray(),
                pendingTargets, observed.Values.ToArray(), requested);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (selected.Length == 0) return new([], [], [], false);
            last = _host.Capture();
            var initial = last;
            if (selected.Any(p => !IsClient(p) || p.OwnerSid != _host.CurrentUserSid || p.SessionId != _host.CurrentSessionId))
                return Finish(L.T("Steam 服务或其他账户会话不在正常退出范围，未执行操作。"), false, false);
            var roots = initial.Processes.Where(p => IsClient(p) && p.Name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                p.OwnerSid == _host.CurrentUserSid && p.SessionId == _host.CurrentSessionId).ToArray();
            if (roots.Length != 1)
                return Finish(L.T("未找到唯一且可核实的 Steam 主进程，未执行操作。"), false, true);
            var launcher = roots[0];
            var directory = Path.GetDirectoryName(launcher.Path)!;
            bool InClient(ProcessRecord p) => IsClient(p) && p.OwnerSid == launcher.OwnerSid && p.SessionId == launcher.SessionId &&
                ProtectionPolicy.TryNormalizePath(p.Path, out var path) && path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) &&
                !path.Split('\\').Any(segment => segment.Equals("steamapps", StringComparison.OrdinalIgnoreCase));
            var group = initial.Processes.Where(InClient).ToArray();
            if (selected.Any(p => !InClient(p)) || group.Any(p => !selected.Any(s => ProcessRespawnObserver.SameIdentity(s, p))) ||
                selected.Any(p => !group.Any(s => ProcessRespawnObserver.SameIdentity(s, p))))
                return Finish(L.T("仅选择了部分 Steam 进程或清单已变化。正常退出需要确认完整客户端组，未执行操作。"), false, true);
            foreach (var p in group)
            {
                if (recheck(p).Protected) return Finish(L.T("Steam 组内有受保护进程，未发送退出请求。"), false, false);
                held.Add(_host.HoldVerifiedProcess(p));
            }
            held.Add(_host.HoldExecutable(launcher));
            // Validate the entire group again after acquiring all process/file handles.
            last = _host.Capture();
            var before = last.Processes.Where(InClient).ToArray();
            if (before.Length != group.Length || before.Any(p => !group.Any(s => ProcessRespawnObserver.SameIdentity(s, p))))
                return Finish(L.T("Steam 进程组在确认后发生变化，未发送退出请求。"), false, true);
            foreach (var p in group)
            {
                token.ThrowIfCancellationRequested();
                _host.ValidateProcess(p);
                if (recheck(p).Protected) return Finish(L.T("Steam 组内有受保护进程，未发送退出请求。"), false, false);
            }
            token.ThrowIfCancellationRequested();
            var requestProcess = _host.RequestShutdown(launcher);
            requested = true;
            progress?.Report(L.T("已请求 Steam 正常退出，正在等待客户端和辅助进程结束；不会自动强制结束。"));
            var started = _host.Clock;
            TimeSpan? emptySince = null;
            while (_host.Clock - started < _timing.ExitTimeout)
            {
                token.ThrowIfCancellationRequested();
                last = _host.Capture();
                var running = last.Processes.Where(InClient).ToArray();
                foreach (var p in running.Where(p => !initial.Processes.Any(old => ProcessRespawnObserver.SameIdentity(old, p)) &&
                    (requestProcess is null || !ProcessRespawnObserver.SameIdentity(requestProcess, p))))
                    observed[(p.Id, p.StartTimeUtcTicks)] = p;
                if (group.Any(p => !_host.HasExited(p) && recheck(p).Protected))
                    return Finish(L.T("保护规则已变化，已停止等待和后续操作；此前的正常退出请求无法撤销。"), false, false);
                if (running.Length == 0 && group.All(_host.HasExited) && (requestProcess is null || _host.HasExited(requestProcess)))
                {
                    emptySince ??= _host.Clock;
                    if (_host.Clock - emptySince >= _timing.StablePeriod)
                        return observed.Count > 0
                            ? Finish(L.T("原 Steam 进程已退出，但观察到新进程；未自动关闭新进程，请刷新确认。"), false, false)
                            : Finish(L.T("Steam 客户端已退出并通过短时稳定检查；此结果不代表游戏存档或云同步已完成。"), true, false);
                }
                else emptySince = null;
                await _host.Delay(_timing.PollInterval, token).ConfigureAwait(false);
            }
            return Finish(L.T("Steam 仍在运行或退出尚未稳定，可能在处理游戏、下载或同步。未强制结束；需要再次确认才能结束原清单。"), false, true);
        }
        catch (OperationCanceledException)
        { return Finish(requested ? L.T("已取消等待 Steam；已发送的正常退出请求无法撤销，未强制结束。") : L.T("已取消，未发送 Steam 退出请求。"), false, false); }
        catch (Exception exception)
        { return Finish(L.F($"Steam 退出未完成：{exception.Message}"), false, !requested); }
        finally { foreach (var item in held) item.Dispose(); }
    }
}
