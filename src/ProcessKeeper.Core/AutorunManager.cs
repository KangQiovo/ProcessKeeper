namespace ProcessKeeper.Core;

/// <summary>Changes only a saved startup configuration; never starts or stops its target.</summary>
public sealed class AutorunManager
{
    private readonly IAutorunBackend _backend;
    private readonly IAutorunBackupStore _backups;
    private readonly object _gate = new();

    public AutorunManager(string? backupDirectory = null) : this(new AutorunWindowsBackend(), new AutorunBackupStore(backupDirectory)) { }
    public AutorunManager(IAutorunBackend backend, IAutorunBackupStore backups)
    { _backend = backend; _backups = backups; }

    public AutorunChangeResult ChangeEnabled(AutorunEntry entry, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.CanChange || entry.IsSystem || entry.SourceKind is AutorunSourceKind.PolicyRun or AutorunSourceKind.Driver or
                AutorunSourceKind.AdvancedRegistry or AutorunSourceKind.WmiSubscription or AutorunSourceKind.PackagedStartup)
                return new(false, L.T("此入口仅供查看，未修改任何配置。"));
            var backupPath = "";
            try
            {
                var current = _backend.Read(entry, cancellationToken);
                if (!current.Fingerprint.Equals(entry.Fingerprint, StringComparison.Ordinal))
                    return new(false, L.T("入口已发生变化，请刷新列表后重新确认。"));
                if (current.Enabled == enabled)
                    return new(true, L.T("入口已经处于所选状态。"));
                AutorunState desired;
                if (AutorunStartupApproval.IsManagedState(current))
                {
                    // A recognized StartupApproved record has an explicit reversible state.
                    // Keep the Run command or startup file intact, including external disables.
                    desired = AutorunStartupApproval.ChangeState(current, enabled);
                    backupPath = _backups.Save(new(entry, current, desired));
                }
                else if (entry.SourceKind == AutorunSourceKind.ScheduledTask)
                {
                    if (!current.Exists || !current.Enabled.HasValue)
                        return new(false, L.T("计划任务状态无法核实，未修改配置。"));
                    // Task Scheduler exposes an explicit reversible Enabled property.
                    // Enabling an externally disabled task does not guess a service start mode.
                    desired = current with { Enabled = enabled };
                    backupPath = _backups.Save(new(entry, current, desired));
                }
                else if (enabled)
                {
                    var backup = _backups.Load(entry.Id);
                    if (backup is null || backup.Entry.Id != entry.Id || backup.Entry.SourceKind != entry.SourceKind ||
                        backup.Entry.Locator != entry.Locator || !backup.Original.Exists || backup.Original.Enabled != true ||
                        backup.Disabled.Enabled != false || backup.Entry.Fingerprint != backup.Original.Fingerprint ||
                        current.Fingerprint != backup.Disabled.Fingerprint)
                        return new(false, L.T("没有与当前禁用状态相符的原始备份，不能猜测恢复配置。"));
                    desired = backup.Original;
                }
                else
                {
                    if (current.Enabled != true || !current.Exists)
                        return new(false, L.T("无法核实入口当前已启用，未修改配置。"));
                    desired = _backend.CreateDisabledState(entry, current);
                    if (desired.Enabled != false)
                        return new(false, L.T("无法生成可恢复的禁用方案。"));
                    // The exact original value/type/definition is durable before the first write.
                    backupPath = _backups.Save(new(entry, current, desired));
                }
                cancellationToken.ThrowIfCancellationRequested();
                var fresh = _backend.Read(entry, cancellationToken);
                if (fresh.Fingerprint != current.Fingerprint)
                    return new(false, L.T("入口在备份期间发生变化，已取消修改。"), backupPath);
                cancellationToken.ThrowIfCancellationRequested();

                // Once a native write starts, finish read-back even if cancellation arrives.
                // A cancellation must never disguise an already applied system change.
                Exception? writeError = null;
                try { _backend.Apply(entry, current, desired); }
                catch (Exception ex) { writeError = ex; }
                var actual = _backend.Read(entry, CancellationToken.None);
                if (actual.Fingerprint == desired.Fingerprint)
                    return new(true, enabled ? entry.SourceKind == AutorunSourceKind.ScheduledTask ? L.T("已启用计划任务；不会立即运行任务。") :
                        L.T("已恢复原始自启动配置；不会立即运行程序。") : L.T("已禁用此入口；当前运行的程序或服务不会被停止。"), backupPath,
                        entry.SourceKind != AutorunSourceKind.ScheduledTask);
                if (actual.Fingerprint == current.Fingerprint)
                    return new(false, L.T("配置未改变。") + (writeError is null ? "" : " " + writeError.Message), backupPath);
                // Do not overwrite a concurrent external edit in an attempt to roll back.
                return new(false, L.T("修改后状态与预期不符，请刷新检查；原始备份已保留，未覆盖其他更改。"), backupPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new(false, L.T("未完成自启动配置修改：") + ex.Message, backupPath); }
        }
    }
}
