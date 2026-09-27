using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ProcessKeeper.Launcher;

internal enum BrokerReply : byte { Started = 1, Cancelled = 2, Unavailable = 3, Failed = 4 }

internal sealed class OriginalLauncher : IDisposable
{
    private readonly FileStream _source;
    private readonly byte[] _hash;
    internal string Path { get; }

    private OriginalLauncher(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        CacheSecurity.AssertNoReparseAncestors(System.IO.Path.GetDirectoryName(Path)!);
        if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0 ||
            !System.IO.Path.GetExtension(Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The original launcher path is unsafe.");
        // Disallow write and delete sharing for the broker's entire lifetime,
        // including the interval spent waiting for the UAC consent dialog.
        _source = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { _hash = SHA256.HashData(_source); }
        catch { _source.Dispose(); throw; }
    }

    public static OriginalLauncher OpenCurrent() => new(Environment.ProcessPath
        ?? throw new IOException("The original single-file launcher location cannot be verified."));

    internal static OriginalLauncher? TryOpenCurrent()
    {
        try { return OpenCurrent(); }
        // An unverifiable source has no elevation capability. The independently
        // verified non-elevated payload may still explain how to reopen safely.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return null; }
    }

    internal static OriginalLauncher OpenForTest(string path) => new(path);

    internal void Validate()
    {
        using var current = OpenVerifiedSource();
    }

    private FileStream OpenVerifiedSource()
    {
        CacheSecurity.AssertNoReparseAncestors(System.IO.Path.GetDirectoryName(Path)!);
        if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The original launcher has changed.");
        var current = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), _hash))
                throw new IOException("The original launcher has changed. Reopen the original single-file application.");
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal BrokerReply Elevate(Func<ProcessStartInfo, bool>? start = null)
    {
        try
        {
            using var current = OpenVerifiedSource();
            var info = new ProcessStartInfo(Path)
            {
                UseShellExecute = true,
                Verb = "runas",
                // Never make a user cache the elevated process's working dir.
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            if (start is not null) return start(info) ? BrokerReply.Started : BrokerReply.Failed;
            using var process = Process.Start(info);
            return process is not null ? BrokerReply.Started : BrokerReply.Failed;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { return BrokerReply.Cancelled; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return BrokerReply.Failed; }
    }

    public void Dispose() => _source.Dispose();
}
