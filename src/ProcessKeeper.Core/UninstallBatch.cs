namespace ProcessKeeper.Core;

public enum UninstallBatchState { Completed, Unconfirmed, NotStarted }
public sealed record UninstallBatchItem(UninstallEntry Entry, UninstallBatchState State, string Message);
public sealed record UninstallBatchResult(IReadOnlyList<UninstallBatchItem> Items, string StopReason)
{
    public string Summary => L.F($"已完成：{Items.Count(i => i.State == UninstallBatchState.Completed)} | 未确认：{Items.Count(i => i.State == UninstallBatchState.Unconfirmed)} | 未执行：{Items.Count(i => i.State == UninstallBatchState.NotStarted)}");
    public string Details
    {
        get
        {
            if (Items.Count > UninstallBatch.MaximumPreviewEntries || UninstallBatch.ExceedsPreview(Items.SelectMany(i => new[] { i.Entry.Name, i.Message }).Concat(new[] { StopReason }), 4096))
                return Summary + "\n" + (StopReason.Length > 1024 ? StopReason.Substring(0, 1024) : StopReason) + "\n" + L.T("详细结果超过显示上限，仅显示汇总。");
            return Summary + (StopReason.Length > 0 ? "\n" + StopReason : "") + "\n\n" + string.Join("\n", Items.Select(i =>
                L.T(i.State == UninstallBatchState.Completed ? "已完成" : i.State == UninstallBatchState.Unconfirmed ? "未确认" : "未执行") + " | " + i.Entry.Name + (i.Message.Length > 0 ? " | " + i.Message : "")));
        }
    }
}

/// <summary>Three mandatory confirmations bind a fixed scan snapshot to reviewed normal uninstall routes.</summary>
public sealed class UninstallBatch
{
    public const int MaximumPreviewEntries = 100;
    public const int MaximumPreviewCharacters = 64000;
    private readonly UninstallManager _manager;
    public UninstallBatch(IUninstallBackend? backend = null) => _manager = new(backend);
    public static bool IsEligible(UninstallEntry entry) => entry.IsRecommended && entry.CanUninstall && !entry.IsSystem && entry.Command is not null;
    public Task<UninstallBatchResult> RunAsync(UninstallSnapshot snapshot, Func<string, string, Task<bool>> confirm,
        Func<bool> canProceed, Action executing, CancellationToken token = default, Func<UninstallEntry, bool>? canUninstall = null) =>
        RunPlanAsync(snapshot, true, confirm, canProceed, executing, token, canUninstall);
    public Task<UninstallBatchResult> RunSelectedAsync(UninstallSnapshot snapshot, Func<string, string, Task<bool>> confirm,
        Func<bool> canProceed, Action executing, CancellationToken token = default, Func<UninstallEntry, bool>? canUninstall = null) =>
        RunPlanAsync(snapshot, false, confirm, canProceed, executing, token, canUninstall);
    private async Task<UninstallBatchResult> RunPlanAsync(UninstallSnapshot snapshot, bool recommendedOnly, Func<string, string, Task<bool>> confirm,
        Func<bool> canProceed, Action executing, CancellationToken token, Func<UninstallEntry, bool>? canUninstall)
    {
        // Copy before the first await. Search filters, rescans and later registrations cannot enlarge this plan.
        var recommended = snapshot.Entries.Where(e => !recommendedOnly || e.IsRecommended).GroupBy(e => e.Locator).Select(g => g.First()).ToArray();
        bool Eligible(UninstallEntry entry) => (!recommendedOnly || entry.IsRecommended) && entry.CanUninstall && !entry.IsSystem && entry.Command is not null && canUninstall?.Invoke(entry) != false;
        var targets = recommended.Where(Eligible).ToArray();
        var excluded = recommended.Where(e => !Eligible(e)).ToArray();
        UninstallBatchResult Stopped(string reason) => new(targets.Select(e => new UninstallBatchItem(e, UninstallBatchState.NotStarted, "")).ToArray(), reason);
        bool Allowed() => !token.IsCancellationRequested && canProceed() && targets.All(entry => canUninstall?.Invoke(entry) != false);
        if (targets.Length == 0) return Stopped(L.T("当前扫描中没有可批量卸载的建议项目。"));
        try
        {
            if (recommended.Length > MaximumPreviewEntries)
                return Stopped(L.F($"建议项目超过单次核对上限 {MaximumPreviewEntries} 项，整批未执行。请先单独卸载部分项目后重新扫描。"));
            // Check component lengths before joining; registrations may contain unusually large text.
            EnsurePreview(recommended.SelectMany(e => new[] { e.Name, e.Locator.Hive, e.Locator.Key }).Concat(snapshot.Warnings));
            var inventory = L.T(recommendedOnly ? "范围为当前完整扫描中的建议卸载项目，不受搜索或筛选影响。新增项目不会加入本次操作。" : "范围为当前选中的卸载项目。新增项目不会加入本次操作。") + "\n\n" +
                L.F($"将卸载 {targets.Length} 项：") + "\n" + Names(targets) + "\n\n" +
                L.F($"已排除 {excluded.Length} 个只读或系统项目：") + "\n" + Names(excluded);
            if (snapshot.Truncated || snapshot.Warnings.Count > 0) inventory += "\n\n" + L.T("扫描结果可能不完整，仅处理下面明确列出的项目。") + "\n" + string.Join("\n", snapshot.Warnings);
            if (!Allowed() || !await confirm(L.T("第 1 / 3 步 | 核对卸载清单"), inventory) || !Allowed()) return Stopped(L.T("批量卸载已取消，未启动任何卸载程序。"));
            if (!await confirm(L.T("第 2 / 3 步 | 风险与免责声明"), L.T("建议标签来自用户关注名单或历史公开报告，不是当前文件的病毒鉴定。请自行核对来源、版本及是否仍需使用，由你决定是否卸载。\n\n卸载可能永久移除文件、设置或关联功能，影响其他软件，要求重启，并可能保留残留文件。请先保存工作并备份重要数据。Process Keeper 不能保证厂商卸载器完整或安全地完成操作。\n\n本次只逐个运行已注册的普通卸载器，需要你完成软件自身的页面。不会强删文件、注册表或驱动。结果无法确认时停止后续项目；停止等待或关闭本应用不会终止已经启动的卸载器。\n\n即使处于无视风险模式，这三次确认也不会跳过。确认即表示你已阅读以上说明，并自主决定继续。") + "\n\n" + UninstallPresentation.EmptyDirectoryNotice) || !Allowed())
                return Stopped(L.T("批量卸载已取消，未启动任何卸载程序。"));
            var reviews = new List<UninstallReview>();
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();
                var review = await _manager.ReviewAsync(target, UninstallMode.Normal, token);
                if (!UninstallPolicy.Same(target, review.Entry) || review.Entry.ReviewedMode != UninstallMode.Normal ||
                    review.Entry.ReviewedExecutableSha256.Length != 64 || !review.Entry.ReviewedExecutableSha256.All(Uri.IsHexDigit))
                    return Stopped(L.T("卸载注册信息或程序文件已变化，请重新扫描。"));
                reviews.Add(review);
            }
            EnsurePreview(reviews.SelectMany(r => new[] { r.Entry.Name, r.Entry.Locator.Hive, r.Entry.Locator.Key,
                r.Entry.Command!.Executable, r.Entry.Command.Arguments, r.Entry.ReviewedExecutableSha256, r.SignatureInformation }));
            var final = L.T("以下清单、注册位置、卸载命令和 SHA-256 已固定。确认后逐个打开普通卸载器；每次启动前再次核验，任何变化或未确认结果都会停止后续。") + "\n\n" + UninstallPresentation.EmptyDirectoryNotice + "\n\n" +
                string.Join("\n\n", reviews.Select(r => r.Entry.Name + "\n" + r.Entry.Registration + "\n" + r.Entry.Command!.Executable + "\n" + r.Entry.Command.Arguments + "\nSHA-256 | " + r.Entry.ReviewedExecutableSha256 + "\n" + r.SignatureInformation));
            if (!Allowed() || !await confirm(L.T("第 3 / 3 步 | 最终确认卸载"), final) || !Allowed()) return Stopped(L.T("批量卸载已取消，未启动任何卸载程序。"));
            executing();
            return await _manager.RunBatchAsync(reviews.Select(r => r.Entry).ToArray(), token, canUninstall);
        }
        catch (OperationCanceledException) { return Stopped(L.T("批量卸载已取消，未启动任何卸载程序。")); }
        catch (Exception ex) { return Stopped(L.T("批量核验失败，未启动任何卸载程序。") + "\n" + ex.Message); }
    }
    internal static bool ExceedsPreview(IEnumerable<string> parts, int reservedCharacters)
    {
        long length = reservedCharacters;
        foreach (var part in parts) { length += part.Length; if (length > MaximumPreviewCharacters) return true; }
        return false;
    }
    private static void EnsurePreview(IEnumerable<string> parts)
    {
        // Reserve room for localized headings, separators and each registration's view number.
        if (ExceedsPreview(parts, 4096)) throw new InvalidDataException(L.F($"确认内容超过 {MaximumPreviewCharacters} 字符上限，整批未执行。请先单独核对相关项目。"));
    }
    private static string Names(IEnumerable<UninstallEntry> entries) => string.Join("\n", entries.Select(e => e.Name + " | " + e.Registration));
}
