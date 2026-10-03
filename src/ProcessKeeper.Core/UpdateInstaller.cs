using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ProcessKeeper.Core;

public sealed class PreparedUpdate
{
    public string Version { get; }
    public string StatusPath => Path.Combine(DirectoryPath, "status.txt");
    internal LauncherContext Context { get; }
    internal string Id { get; }
    internal string DirectoryPath { get; }
    internal bool Started { get; set; }
    internal PreparedUpdate(LauncherContext context, string id, string directory, string version)
    { Context = context; Id = id; DirectoryPath = directory; Version = version; }
}
public sealed record UpdateStartResult(bool Success, string Message);

/// <summary>Stages a verified download and starts the trusted native transaction. Never elevates a downloaded executable.</summary>
public static class UpdateInstaller
{
    public static string CreateDownloadStage(LauncherContext context)
    {
        LauncherContextReader.RequireCurrent(context);
        var directory = Path.Combine(context.DirectoryPath, "download-" + Guid.NewGuid().ToString("N"));
        UpdateTrustedFiles.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Only this session's flat download staging directory can be discarded; no recursive deletion.</summary>
    public static void DiscardDownloadStage(LauncherContext context, string directory)
    {
        LauncherContextReader.RequireCurrent(context);
        var full = Path.GetFullPath(directory);
        var name = Path.GetFileName(full);
        if (!UpdateTrustedFiles.SamePath(Path.GetDirectoryName(full)!, context.DirectoryPath) ||
            !name.StartsWith("download-", StringComparison.Ordinal) || !UpdateTrustedFiles.ValidId(name.Substring(9)))
            throw new IOException("This is not the current launcher's download stage.");
        UpdateTrustedFiles.ValidatePath(Path.Combine(full, "check"));
        if (Directory.EnumerateDirectories(full).Any()) throw new IOException("Unexpected directory in download stage.");
        var files = Directory.EnumerateFiles(full).Take(65).ToArray();
        if (files.Length > 64) throw new IOException("Unexpected file count in download stage.");
        foreach (var file in files)
        {
            UpdateTrustedFiles.ValidatePath(file);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked download refused.");
            File.Delete(file);
        }
        Directory.Delete(full, false);
    }

    public static PreparedUpdate Prepare(LauncherContext context, UpdateDownloadResult download,
        CancellationToken cancellationToken = default)
    {
        ValidateDownloadIdentity(download);
        if (context.PackageTarget != download.Asset.PackageTarget) throw new InvalidDataException(L.T("更新文件类型与当前应用包不一致。"));
        return PrepareVerified(context, download.FilePath, download.Sha256, download.Release.Version, download.Asset.Size, cancellationToken);
    }

    // The result is provenance data, not execution authority: the trusted stage, current
    // launcher identity, bytes and native package are independently checked afterwards.
    internal static void ValidateDownloadIdentity(UpdateDownloadResult download)
    {
        if (download is null || download.Release is null || download.Asset is null)
            throw new InvalidDataException(L.T("更新下载身份不完整，请重新检查官方发布。"));
        var release = download.Release; var asset = download.Asset;
        if (!string.Equals(release.Repository, UpdatePolicy.Repository, StringComparison.Ordinal) ||
            !UpdateVersion.TryParse(release.Tag, out var tagVersion) || tagVersion!.Value != release.Version ||
            !UpdateService.PortableVersion(release.Version) || !UpdateService.PortableAssetName(asset.Name) || !asset.CanAutoInstall || asset.Restriction.Length != 0 || asset.Id <= 0 ||
            asset.PackageTarget != UpdatePackagePolicy.Identify(asset.Name, release.Version) || !UpdatePackagePolicy.Supports(asset.PackageTarget, UpdatePackagePolicy.Current()) ||
            asset.Size < 4096 || asset.Size > 536870912 || !UpdateTrustedFiles.ValidHash(download.Sha256) ||
            !UpdateService.ValidDigest(asset.Digest) || !asset.Digest.Substring(7).Equals(download.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !UpdateSources.All.Any(source => source.Id == download.SourceId) || release.Assets is null ||
            release.Assets.Count(item => item.Id == asset.Id) != 1 || release.Assets.Single(item => item.Id == asset.Id) != asset)
            throw new InvalidDataException(L.T("更新下载身份与固定官方发布不一致，已拒绝安装。"));
        var root = "https://github.com/" + UpdatePolicy.Repository + "/releases/";
        if (!UpdateService.ExactUrl(release.HtmlUrl, root + "tag/" + Uri.EscapeDataString(release.Tag)) ||
            !UpdateService.ExactUrl(asset.DownloadUrl, root + "download/" + Uri.EscapeDataString(release.Tag) + "/" + Uri.EscapeDataString(asset.Name)))
            throw new InvalidDataException(L.T("更新下载身份与固定官方发布不一致，已拒绝安装。"));
    }

    private static PreparedUpdate PrepareVerified(LauncherContext context, string filePath, string sha256, string version,
        long expectedLength, CancellationToken cancellationToken)
    {
        LauncherContextReader.RequireCurrent(context);
        if (!UpdateVersion.TryParse(version, out var next) || !UpdateVersion.TryParse(context.OriginalVersion, out var current) || next!.CompareTo(current) <= 0)
            throw new InvalidDataException(L.T("更新版本必须高于当前原始 EXE 的版本。"));
        var full = Path.GetFullPath(filePath);
        var download = Path.GetDirectoryName(full)!;
        if (!UpdateTrustedFiles.SamePath(Path.GetDirectoryName(download)!, context.DirectoryPath) ||
            !Path.GetFileName(download).StartsWith("download-", StringComparison.Ordinal) ||
            !UpdateTrustedFiles.ValidId(Path.GetFileName(download).Substring(9))) throw new IOException("Download did not use this launcher's protected stage.");
        cancellationToken.ThrowIfCancellationRequested();
        using var input = UpdateTrustedFiles.OpenRead(full, requireProtected: false);
        if (input.Length is < 4096 or > 536870912 || input.Length != expectedLength) throw new InvalidDataException("Invalid update file size.");
        UpdateTrustedFiles.RequireHash(input, sha256);
        var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(context.DirectoryPath, "job-" + id);
        UpdateTrustedFiles.CreateDirectory(directory);
        var prepared = new PreparedUpdate(context, id, directory, next!.ToString());
        try
        {
        using (var output = UpdateTrustedFiles.Create(Path.Combine(directory, "update.exe")))
        {
            var buffer = new byte[65536]; int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            { cancellationToken.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
            output.Flush(true);
        }
        using (var copied = UpdateTrustedFiles.OpenRead(Path.Combine(directory, "update.exe"))) UpdateTrustedFiles.RequireHash(copied, sha256);
        cancellationToken.ThrowIfCancellationRequested();
        using (var job = UpdateTrustedFiles.Create(Path.Combine(directory, "job.txt")))
        using (var writer = new StreamWriter(job, new UTF8Encoding(false), 4096, true))
        {
            writer.Write("PKUP2\n" + context.Id + "\n" + id + "\n" + sha256.ToLowerInvariant() + "\n" + next + "\n" + UpdatePolicy.Repository + "\n");
            writer.Flush(); job.Flush(true);
        }
            return prepared;
        }
        catch
        {
            try { DiscardPrepared(prepared); } catch { /* Preserve the original failure; bounded session files are never executed. */ }
            throw;
        }
    }

    public static void DiscardPrepared(PreparedUpdate prepared)
    {
        LauncherContextReader.RequireCurrent(prepared.Context);
        if (prepared.Started || File.Exists(Path.Combine(prepared.DirectoryPath, "execute.txt")))
            throw new IOException("A committed update is owned by its native helper and cannot be discarded.");
        if (!UpdateTrustedFiles.SamePath(prepared.DirectoryPath, Path.Combine(prepared.Context.DirectoryPath, "job-" + prepared.Id)) ||
            !UpdateTrustedFiles.ValidId(prepared.Id)) throw new IOException("Invalid prepared update directory.");
        UpdateTrustedFiles.ValidatePath(Path.Combine(prepared.DirectoryPath, "check"));
        if (Directory.EnumerateDirectories(prepared.DirectoryPath).Any()) throw new IOException("Unexpected update directory.");
        var files = Directory.EnumerateFiles(prepared.DirectoryPath).Take(16).ToArray();
        if (files.Length >= 16) throw new IOException("Unexpected file count in prepared update.");
        foreach (var file in files)
        {
            if (Path.GetFileName(file) is not "update.exe" and not "job.txt" and not "ready.txt" and not "status.txt" and not "commit.pending" and not "commit.txt" and not "accepted.txt" and not "execute.pending")
                throw new IOException("Unexpected file in prepared update.");
            UpdateTrustedFiles.ValidatePath(file);
        }
        foreach (var file in files) File.Delete(file);
        Directory.Delete(prepared.DirectoryPath, false);
    }

    /// <summary>Call on a worker after explicit exit/restart consent. Success means helper ready; only then may UI exit.</summary>
    public static UpdateStartResult Start(PreparedUpdate prepared)
    {
        try
        {
            LauncherContextReader.RequireCurrent(prepared.Context);
            using var helper = UpdateTrustedFiles.OpenRead(prepared.Context.HelperPath);
            UpdateTrustedFiles.RequireHash(helper, prepared.Context.HelperSha256);
            var start = new ProcessStartInfo(prepared.Context.HelperPath)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(prepared.Context.HelperPath)!,
                Arguments = "--install " + prepared.Context.Id + " " + prepared.Id
            };
            start.EnvironmentVariables["PROCESSKEEPER_HELPER_LANGUAGE"] = L.Language;
            using var process = Process.Start(start) ?? throw new IOException("The update helper could not start.");
            var clock = Stopwatch.StartNew();
            var ready = Path.Combine(prepared.DirectoryPath, "ready.txt");
            while (clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (process.HasExited) throw new IOException(L.T("更新助手在就绪前退出，原始 EXE 未被替换。"));
                if (File.Exists(ready))
                {
                    using var stream = UpdateTrustedFiles.OpenRead(ready);
                    if (stream.Length > 128) throw new IOException("Invalid update readiness response.");
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    if (reader.ReadToEnd() != "PKREADY1\n" + process.Id.ToString(CultureInfo.InvariantCulture) + "\n") throw new IOException("Invalid update helper identity.");
                    var pendingCommit = Path.Combine(prepared.DirectoryPath, "commit.pending");
                    using (var commit = UpdateTrustedFiles.Create(pendingCommit))
                    {
                        var bytes = Encoding.ASCII.GetBytes("PKCOMMIT1\n" + process.Id.ToString(CultureInfo.InvariantCulture) + "\n");
                        commit.Write(bytes, 0, bytes.Length); commit.Flush(true);
                    }
                    // The helper must never observe a half-written consent record or a share-locked file.
                    File.Move(pendingCommit, Path.Combine(prepared.DirectoryPath, "commit.txt"));
                    var acknowledgment = Path.Combine(prepared.DirectoryPath, "accepted.txt");
                    var acknowledgeClock = Stopwatch.StartNew();
                    while (!File.Exists(acknowledgment))
                    {
                        if (process.HasExited || acknowledgeClock.Elapsed > TimeSpan.FromSeconds(5))
                            throw new IOException(L.T("更新助手未确认接管，应用将保持运行，原始 EXE 不会被替换。"));
                        Thread.Sleep(50);
                    }
                    using (var accepted = UpdateTrustedFiles.OpenRead(acknowledgment))
                    using (var acceptedReader = new StreamReader(accepted, Encoding.UTF8))
                    {
                        if (accepted.Length > 128 || acceptedReader.ReadToEnd() != "PKACCEPT1\n" + process.Id.ToString(CultureInfo.InvariantCulture) + "\n")
                            throw new IOException("Invalid update acknowledgment.");
                    }
                    var pendingExecute = Path.Combine(prepared.DirectoryPath, "execute.pending");
                    using (var execute = UpdateTrustedFiles.Create(pendingExecute))
                    {
                        var bytes = Encoding.ASCII.GetBytes("PKEXECUTE1\n" + process.Id.ToString(CultureInfo.InvariantCulture) + "\n");
                        execute.Write(bytes, 0, bytes.Length); execute.Flush(true);
                    }
                    File.Move(pendingExecute, Path.Combine(prepared.DirectoryPath, "execute.txt"));
                    prepared.Started = true;
                    return new(true, L.T("已验证的更新助手已就绪，退出应用后完成更新。"));
                }
                Thread.Sleep(50);
            }
            // The helper requires a separate commit file; timeout cannot replace the original after a later app exit.
            throw new IOException(L.T("更新助手未能就绪，原始 EXE 未被替换。"));
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
}
