using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("ProcessKeeper.Update.Tests")]

namespace ProcessKeeper.Core;

public sealed record AppCacheCleanupResult(bool Success, int RemovedFiles, long RemovedBytes, int RetainedTrees, string Message);

/// <summary>Clears verified old payloads and inactive owned update stages, preserving settings and the active payload.</summary>
public static class AppCacheCleanupService
{
    /// <summary>Retries only receipt-authorized old payloads after the new window is ready.</summary>
    public static Task<AppCacheCleanupResult> ClearPendingPayloadsAfterStartupAsync(LauncherContext context, CancellationToken cancellationToken)
        => Task.Run(() => ClearPendingAfterStartup(context, cancellationToken));

    private static AppCacheCleanupResult ClearPendingAfterStartup(LauncherContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            LauncherContextReader.RequireCurrent(context);
            using var helper = UpdateTrustedFiles.OpenRead(context.HelperPath);
            UpdateTrustedFiles.RequireHash(helper, context.HelperSha256);
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(context.HelperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(context.HelperPath)!, Arguments = "--clear-pending-cache " + context.Id + " " + id
            };
            start.EnvironmentVariables["PROCESSKEEPER_HELPER_LANGUAGE"] = L.Language;
            using var process = Process.Start(start) ?? throw new IOException(L.T("无法启动已验证的缓存清理助手。"));
            WaitForPendingCleanupExit(process.WaitForExit, () =>
            {
                // This marker only stops this request; it cannot authorize any deletion.
                using var marker = UpdateTrustedFiles.Create(Path.Combine(context.DirectoryPath, "cache-" + id + ".cancel"));
                marker.Flush(true);
            }, cancellationToken);
            using var stream = UpdateTrustedFiles.OpenRead(Path.Combine(context.DirectoryPath, "cache-" + id + ".txt"));
            if (process.ExitCode != 0 || stream.Length > 1024) throw new InvalidDataException(L.T("缓存清理结果无效。"));
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return ParseResponse(reader.ReadToEnd());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, 0, 0, 0, ex.Message); }
    }

    internal static void WaitForPendingCleanupExit(Func<int, bool> waitForExit, Action requestCancellation, CancellationToken cancellationToken)
    {
        bool requested = false;
        var deadline = Stopwatch.StartNew();
        while (!waitForExit(250))
        {
            if (!requested && cancellationToken.IsCancellationRequested)
            {
                requested = true;
                try { requestCancellation(); } catch { /* The verified native worker also ends on caller exit or its own deadline. */ }
            }
            if (deadline.ElapsedMilliseconds >= 25000) throw new IOException("The bounded cache cleanup helper did not finish.");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static AppCacheCleanupResult Clear(LauncherContext context)
    {
        try
        {
            LauncherContextReader.RequireCurrent(context);
            using var helper = UpdateTrustedFiles.OpenRead(context.HelperPath);
            UpdateTrustedFiles.RequireHash(helper, context.HelperSha256);
            var id = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(context.HelperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(context.HelperPath)!, Arguments = "--clear-cache " + context.Id + " " + id
            };
            start.EnvironmentVariables["PROCESSKEEPER_HELPER_LANGUAGE"] = L.Language;
            using var process = Process.Start(start) ?? throw new IOException(L.T("无法启动已验证的缓存清理助手。"));
            // Keep the UI operation guard held until the owned helper actually exits.
            // Releasing it on a timeout could race a new/resumed current-session update.
            process.WaitForExit();
            using var stream = UpdateTrustedFiles.OpenRead(Path.Combine(context.DirectoryPath, "cache-" + id + ".txt"));
            if (stream.Length > 1024) throw new InvalidDataException(L.T("缓存清理结果无效。"));
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            return ParseResponse(reader.ReadToEnd());
        }
        catch (Exception ex) { return new(false, 0, 0, 0, ex.Message); }
    }

    internal static AppCacheCleanupResult ParseResponse(string text)
    {
        var fields = text.TrimEnd('\n').Split('\n');
        if (text.Length > 1024 || fields.Length != 5 || fields[0] != "PKCACHESTATUS1" || fields[1] != "completed" ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var files) || files > 1000000 ||
            !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) || bytes > 16L * 1024 * 1024 * 1024 ||
            !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var retained) || retained > 1000000)
            throw new InvalidDataException(L.T("缓存清理结果无效。"));
        var detail = L.F($"已清理 {files} 个缓存文件 | {bytes / 1048576d:0.00} MiB | 保留 {retained} 组缓存。");
        return new(true, files, bytes, retained, files == 0 && retained == 0 ? L.T("没有可清理的应用缓存或更新包。") : detail);
    }
}
