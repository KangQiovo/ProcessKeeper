using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ProcessKeeper.Launcher;

internal static class CacheStore
{
    public static void EnsurePlainDirectory(string path, bool create = true)
    {
        if (!Directory.Exists(path))
        {
            if (!create || File.Exists(path)) throw new IOException("Cache directory is missing or occupied by a file: " + path);
            Directory.CreateDirectory(path);
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Startup stopped because the cache path contains a symbolic link or junction: " + path);
    }

    public static FileStream AcquireLock(string versionRoot, TimeSpan timeout, bool restrictedSecurity = true)
    {
        EnsurePlainDirectory(versionRoot, create: false);
        var lockPath = Path.Combine(versionRoot, ".cache.lock");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The cache lock path is unsafe.");
            try
            {
                return restrictedSecurity
                    ? CacheSecurity.CreateFile(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                    : new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33 && watch.Elapsed < timeout) { Thread.Sleep(200); }
        }
    }

    public static string Prepare(string versionRoot, Stream payload, PayloadContract contract, bool restrictedSecurity = true)
    {
        EnsurePlainDirectory(versionRoot, create: false);
        var appDirectory = Path.Combine(versionRoot, "App");
        if (restrictedSecurity) CacheSecurity.ValidateDirectory(versionRoot);
        if (ValidateCache(appDirectory, contract, restrictedSecurity)) return appDirectory;
        var staging = Path.Combine(versionRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (restrictedSecurity) CacheSecurity.CreateDirectory(staging); else EnsurePlainDirectory(staging);
            Extract(payload, staging, contract, restrictedSecurity);
            if (!ValidateCache(staging, contract, restrictedSecurity)) throw new InvalidDataException("Extracted files failed integrity verification.");
            if (Directory.Exists(appDirectory) || File.Exists(appDirectory)) IsolateOwnedEntry(appDirectory, versionRoot, ".invalid-");
            Directory.Move(staging, appDirectory);
            if (!ValidateCache(appDirectory, contract, restrictedSecurity)) throw new InvalidDataException("The application cache changed. Startup stopped.");
            return appDirectory;
        }
        finally
        {
            // Preserve failed or suspicious trees without recursively touching them.
            if (Directory.Exists(staging) || File.Exists(staging)) IsolateOwnedEntry(staging, versionRoot, ".failed-");
        }
    }

    public static bool ValidateCache(string appDirectory, PayloadContract contract, bool restrictedSecurity = true)
    {
        try
        {
            using var files = OpenVerifiedFiles(appDirectory, contract, restrictedSecurity);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return false;
        }
    }

    public static VerifiedFiles OpenVerifiedFiles(string appDirectory, PayloadContract contract, bool restrictedSecurity = true)
    {
        EnsurePlainDirectory(appDirectory, create: false);
        var opened = new VerifiedFiles();
        try
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new Stack<string>();
            directories.Push(appDirectory);
            while (directories.TryPop(out var directory))
            {
                EnsurePlainDirectory(directory, create: false);
                if (restrictedSecurity) CacheSecurity.ValidateDirectory(directory);
                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The application cache contains an unsafe link.");
                    if ((item.Attributes & FileAttributes.Directory) != 0) { directories.Push(item.FullName); continue; }
                    var relative = Path.GetRelativePath(appDirectory, item.FullName).Replace('\\', '/');
                    if (!contract.Files.TryGetValue(relative, out var expected) || !found.Add(relative))
                        throw new IOException("The application cache contains an unlisted file.");
                    var stream = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    opened.Streams.Add(stream);
                    if (restrictedSecurity) CacheSecurity.ValidateFile(stream);
                    if (stream.Length != expected.Length ||
                        !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Cache file verification failed: " + relative);
                }
            }
            if (found.Count != contract.Files.Count) throw new IOException("The application cache is missing a required file.");
            return opened;
        }
        catch { opened.Dispose(); throw; }
    }

    private static void Extract(Stream payload, string staging, PayloadContract contract, bool restrictedSecurity)
    {
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != contract.Files.Count) throw new InvalidDataException("The embedded payload does not match its manifest.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate every ZIP path and entry before creating any extracted file.
        foreach (var entry in archive.Entries)
        {
            PayloadContract.ValidateRelativePath(entry.FullName);
            if (!seen.Add(entry.FullName) || !contract.Files.TryGetValue(entry.FullName, out var expected) || entry.Length != expected.Length ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The embedded ZIP contains unlisted, duplicate, or unsafe files.");
            _ = contract.ResolvePath(staging, entry.FullName);
        }
        foreach (var entry in archive.Entries)
        {
            var path = contract.ResolvePath(staging, entry.FullName);
            CreateParents(staging, Path.GetDirectoryName(path)!, restrictedSecurity);
            using var input = entry.Open();
            using var output = restrictedSecurity
                ? CacheSecurity.CreateFile(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            if (output.Length != contract.Files[entry.FullName].Length) throw new InvalidDataException("An extracted file has an unexpected length.");
        }
    }

    private static void CreateParents(string root, string target, bool restrictedSecurity)
    {
        EnsurePlainDirectory(root, create: false);
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".") return;
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (restrictedSecurity) CacheSecurity.CreateDirectory(current); else EnsurePlainDirectory(current);
        }
    }

    private static void IsolateOwnedEntry(string target, string versionRoot, string prefix)
    {
        EnsurePlainDirectory(versionRoot, create: false);
        var root = Path.GetFullPath(versionRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to quarantine a path outside the version cache.");
        var destination = Path.Combine(versionRoot, prefix + Guid.NewGuid().ToString("N"));
        var attributes = File.GetAttributes(fullTarget);
        // Move the directory entry itself, including a junction if encountered.
        // No descendant is enumerated, modified, or recursively deleted here.
        if ((attributes & FileAttributes.Directory) != 0) Directory.Move(fullTarget, destination);
        else File.Move(fullTarget, destination);
    }
}

internal sealed class VerifiedFiles : IDisposable
{
    internal List<FileStream> Streams { get; } = [];
    public void Dispose()
    {
        foreach (var stream in Streams) stream.Dispose();
        Streams.Clear();
    }
}
