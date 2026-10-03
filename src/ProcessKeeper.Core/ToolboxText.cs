namespace ProcessKeeper.Core;

public static class ToolboxText
{
    public static string MemoryConfirmation(MemoryOptimizationMode mode) =>
        L.T(mode == MemoryOptimizationMode.Deep
            ? "将处理整机工作集、文件缓存、修改页、待机页、注册表缓存与相同内存页。部分步骤使用未公开的 Windows 接口，旧系统可能不支持。"
            : "将收缩整机工作集、写回修改页并清理待机页。") + "\n\n" +
        L.T("所有程序都会受影响，包括白名单程序。可能短暂卡顿或增加磁盘读取，内存占用可能随后回升。此操作不关闭程序。是否继续？");
    public static string MemoryStage(MemoryOptimizationStage stage) => L.T(stage switch
    {
        MemoryOptimizationStage.WorkingSets => "整机工作集", MemoryOptimizationStage.FileCache => "文件缓存",
        MemoryOptimizationStage.ModifiedPages => "修改页写回", MemoryOptimizationStage.StandbyPages => "待机页",
        MemoryOptimizationStage.LowPriorityStandby => "低优先级待机页", MemoryOptimizationStage.RegistryCache => "注册表缓存",
        _ => "相同内存页合并"
    });
    public static string MemoryLine(MemoryOperation step) => MemoryStage(step.Stage) + " | " +
        L.T(step.Status == MemoryOperationStatus.Succeeded ? "已完成" : step.Status == MemoryOperationStatus.Unsupported ? "系统不支持此步骤" : "步骤失败") +
        (step.Detail.Length > 0 ? " | " + step.Detail : "");
    public static string MemoryReport(MemoryOptimizationResult result)
    {
        var lines = new List<string>();
        if (result.Before is not null) lines.Add(L.F($"操作前可用：{GiB(result.Before.AvailableBytes)} GiB / {GiB(result.Before.TotalBytes)} GiB"));
        if (result.After is not null) lines.Add(L.F($"操作后可用：{GiB(result.After.AvailableBytes)} GiB / {GiB(result.After.TotalBytes)} GiB"));
        if (result.AvailableChange is long change)
            lines.Add(L.F($"可用内存变化：{(change >= 0 ? "+" : "")}{(change / 1073741824d).ToString("0.00")} GiB"));
        lines.AddRange(result.Operations.Select(MemoryLine));
        if (result.Cancelled) lines.Add(L.T("已取消尚未执行的步骤。"));
        lines.Add(L.T("内存变化包含其他程序的活动，不代表释放了相同数量的已提交内存。"));
        return string.Join("\n", lines);
    }
    private static string GiB(ulong bytes) => (bytes / 1073741824d).ToString("0.00");
    public static string DownloadProgress(ResourceDownloadProgress progress) =>
        progress.State == "probing" ? L.T("正在检测下载源…") :
        (progress.State == "verifying" ? L.T("正在校验文件…") + " | " : "") +
        (progress.Total > 0 ? Math.Min(progress.State == "completed" ? 100d : 99.99d, Math.Max(0, progress.Bytes * 100d / progress.Total.Value)).ToString("0.00") + "% | " : "") + progress.Source + " | " +
        (progress.Bytes / 1048576d).ToString("0.00") + " MiB" +
        (progress.Total.HasValue ? " / " + (progress.Total.Value / 1048576d).ToString("0.00") + " MiB" : "") +
        " | " + (progress.BytesPerSecond / 1048576d).ToString("0.00") + " MiB/s" +
        (progress.State == "downloading" && progress.ActiveConnections > 0 ? " | " + L.F($"连接 {progress.ActiveConnections} | 分段 {progress.Segments}") : "") +
        (progress.FallbackReason.Length > 0 ? " | " + L.T(progress.FallbackReason switch
        {
            "range-not-supported" => "服务器不支持分段，使用单连接",
            "range-rejected" => "服务器拒绝分段，已切换单连接",
            "length-unknown" => "文件大小未知，使用单连接",
            "single-connection" => "已选择单连接",
            "small-file" => "文件较小，使用单连接",
            _ => progress.FallbackReason
        }) : "");
    public static string DownloadError(Exception error) => L.T(error.Message switch
    {
        "Choose an absolute download directory" => "请选择完整的下载目录。",
        "Invalid Windows file name" => "文件名无效，不能使用保留名称或路径字符。",
        "Connections must be between 1 and 32" => "最大并发连接数必须在 1 到 32 之间。",
        "Invalid User-Agent" => "User-Agent 无效。",
        "SHA256 must contain 64 hexadecimal characters" => "SHA256 必须为 64 位十六进制字符串。",
        "Only HTTP and HTTPS file links without credentials or fragments are supported" => "仅支持不含用户密码或片段的 HTTP 与 HTTPS 文件链接。",
        "Unknown download source" => "下载源无效。",
        "GitHub mirrors require public release/raw links without a query" => "GitHub 镜像仅适用于不带查询参数的公开发布文件与原始文件；其他直链请选择原始直链。",
        "Third-party download sources require explicit consent" => "使用第三方下载源需要先确认。",
        "The target already exists; choose a different file name" => "目标文件已存在，请更换文件名。",
        "No reachable download source" => "没有可连接的下载源。",
        "All sources failed" => "所有下载源均失败。",
        "SHA256 verification failed" => "SHA256 校验失败，文件未保存到目标位置。",
        "Downloaded length does not match the server" => "下载文件长度与服务器不符。",
        "The proxy returned a web page instead of the requested file" => "加速源返回了网页，未返回目标文件。",
        "The server returned an invalid probe Content-Range" => "服务器返回的分段信息无效。",
        "Invalid range response" => "服务器返回的分段数据无效。",
        "Server truncated the file" => "服务器提前结束了传输，可继续重试。",
        "Server sent extra bytes" => "服务器返回了超出预期的数据。",
        "File changed since the source probe" => "远程文件已变化，请重新开始下载。",
        "File metadata changed since the source probe" => "远程文件已变化，请重新开始下载。",
        "Server returned an incomplete range length" => "服务器提前结束了传输，可继续重试。",
        _ => error.Message
    });
}
