namespace ProcessKeeper.Core;

public sealed class UninstallCatalog
{
    private readonly IUninstallBackend _backend;
    public UninstallCatalog(IUninstallBackend? backend = null) => _backend = backend ?? new UninstallWindowsBackend();
    public UninstallSnapshot Scan(CancellationToken token = default) => _backend.Scan(token);
}

public sealed class UninstallManager
{
    private readonly IUninstallBackend _backend;
    private static readonly SemaphoreSlim ExecutionGate = new(1, 1);
    public UninstallManager(IUninstallBackend? backend = null) => _backend = backend ?? new UninstallWindowsBackend();
    public Task<UninstallReview> ReviewAsync(UninstallEntry entry, UninstallMode mode, CancellationToken token = default) =>
        Task.Run(() => _backend.Review(entry, mode, token), token);
    public async Task<UninstallResult> RunAsync(UninstallEntry entry, UninstallMode mode, CancellationToken token = default, Func<UninstallEntry, bool>? canUninstall = null)
    {
        token.ThrowIfCancellationRequested();
        if (mode != UninstallMode.Normal && mode != UninstallMode.RegisteredQuiet) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!await ExecutionGate.WaitAsync(0, token).ConfigureAwait(false)) return new(UninstallOutcome.NotStarted, L.T("请等待当前卸载操作完成。"));
        try { return await RunCoreAsync(entry, mode, token, canUninstall).ConfigureAwait(false); }
        finally { ExecutionGate.Release(); }
    }
    internal async Task<UninstallBatchResult> RunBatchAsync(IReadOnlyList<UninstallEntry> entries, CancellationToken token, Func<UninstallEntry, bool>? canUninstall = null)
    {
        var items = entries.Select(e => new UninstallBatchItem(e, UninstallBatchState.NotStarted, "")).ToArray();
        if (!await ExecutionGate.WaitAsync(0, token).ConfigureAwait(false)) return new(items, L.T("请等待当前卸载操作完成。"));
        string reason = "";
        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (token.IsCancellationRequested) { reason = L.T("批量卸载已停止，未执行后续项目。"); break; }
                try
                {
                    var result = await RunCoreAsync(entries[index], UninstallMode.Normal, token, canUninstall).ConfigureAwait(false);
                    bool complete = result.Outcome == UninstallOutcome.RegistrationRemoved && result.ExitCode == 0;
                    var state = complete ? UninstallBatchState.Completed : result.Started ? UninstallBatchState.Unconfirmed : UninstallBatchState.NotStarted;
                    var message = result.Message;
                    if (result.ExitCode.HasValue && result.ExitCode.Value != 0) message += "\n" + L.F($"卸载器退出代码：{result.ExitCode.Value}。批量操作已停止，请检查是否需要重启或手动处理。");
                    items[index] = new(entries[index], state, message);
                    if (!complete) { reason = entries[index].Name + " | " + message; break; }
                }
                catch (OperationCanceledException) { reason = L.T("批量卸载已停止，未执行后续项目。"); break; }
                catch (Exception ex) { items[index] = new(entries[index], UninstallBatchState.Unconfirmed, ex.Message); reason = entries[index].Name + " | " + ex.Message; break; }
            }
        }
        finally { ExecutionGate.Release(); }
        return new(items, reason);
    }
    private async Task<UninstallResult> RunCoreAsync(UninstallEntry entry, UninstallMode mode, CancellationToken token, Func<UninstallEntry, bool>? canUninstall)
    {
        UninstallExecution? execution = null;
        try
        {
            if (canUninstall?.Invoke(entry) == false) return new(UninstallOutcome.NotStarted, L.T("白名单保护"));
            var current = await Task.Run(() => _backend.Read(entry.Locator, token), token).ConfigureAwait(false);
            if (!UninstallPolicy.Same(entry, current)) return new(UninstallOutcome.NotStarted, L.T("卸载注册信息或程序文件已变化，请重新扫描。"));
            if (entry.ReviewedMode != mode || entry.ReviewedExecutableSha256.Length != 64 || !entry.ReviewedExecutableSha256.All(Uri.IsHexDigit))
                return new(UninstallOutcome.NotStarted, L.T("请先核对卸载器信息并确认。"));
            if (current!.IsSystem || !current.CanUninstall || mode == UninstallMode.RegisteredQuiet && !current.CanQuietUninstall)
                return new(UninstallOutcome.NotStarted, current.ReadOnlyReason.Length > 0 ? current.ReadOnlyReason : L.T("没有可验证的注册卸载命令。"));
            token.ThrowIfCancellationRequested();
            if (canUninstall?.Invoke(entry) == false || canUninstall?.Invoke(current) == false) return new(UninstallOutcome.NotStarted, L.T("白名单保护"));
            execution = await _backend.ExecuteAsync(entry, mode, token).ConfigureAwait(false);
            if (!execution.Started) return new(UninstallOutcome.NotStarted, L.T("卸载程序未启动。"));
            // Once started, cancellation stops observation only. Never kill an installer or claim rollback.
            if (!execution.Exited || execution.MonitoringStopped || token.IsCancellationRequested)
                return new(UninstallOutcome.MonitoringStopped, L.T("已停止等待。卸载程序可能仍在运行，请稍后重新扫描确认。"), true, execution.ExitCode);
            UninstallEntry? after;
            try { after = await Task.Run(() => _backend.Read(entry.Locator, CancellationToken.None)).ConfigureAwait(false); }
            catch { return new(UninstallOutcome.VerificationUnavailable, L.T("卸载程序已运行，但无法核实注册项状态。请重新扫描。"), true, execution.ExitCode); }
            if (after is null) return new(UninstallOutcome.RegistrationRemoved, L.T("原卸载注册项已移除。此结果不代表所有残留文件均已清理。"), true, execution.ExitCode);
            return new(UninstallOutcome.StillRegistered, L.T("卸载注册项仍存在。请完成软件卸载页面，或重新扫描确认；未执行额外清理。"), true, execution.ExitCode);
        }
        catch (OperationCanceledException) when (execution is null) { throw; }
    }
}
