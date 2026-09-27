using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

public sealed record AutorunOwnershipFile(long Length, long WrittenUtcTicks, long CreatedUtcTicks);

/// <summary>Read-only evidence seam. Tests provide synthetic identities without inspecting startup configuration.</summary>
public interface IAutorunOwnershipProbe
{
    AutorunOwnershipFile? ReadIdentity(string path, CancellationToken token);
    bool IsWindowsProtected(string path, CancellationToken token);
}

/// <summary>
/// Snapshot-time display attribution, deliberately independent of IsSystem/CanChange.
/// WRP identifies files installed with Windows, including catalog-signed files, without
/// an online signature check. An arbitrary command running a Windows host is not thereby
/// a Windows startup entry. Ambiguous commands, tasks, links and extension points stay visible.
/// </summary>
public sealed class AutorunOwnershipClassifier
{
    public const int MaximumFileChecks = 1024, MaximumCacheEntries = 2048;
    public const int MaximumClassificationMilliseconds = 2000;
    internal static readonly AutorunOwnershipClassifier Shared = new();
    private readonly IAutorunOwnershipProbe _probe;
    private readonly string _windowsDirectory;
    private readonly Func<DateTime> _utcNow;
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedEvidence> _cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record CachedEvidence(AutorunOwnershipFile Identity, bool Protected, DateTime Expires);
    // These executables load other programs, scripts, DLLs or service implementations.
    // Even a WRP-protected, argument-free host is not sufficient ownership evidence.
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "svchost.exe", "rundll32.exe",
        "dllhost.exe", "regsvr32.exe", "wscript.exe", "cscript.exe", "mshta.exe", "msiexec.exe",
        "taskhost.exe", "taskhostw.exe", "taskeng.exe", "wmiprvse.exe", "wsl.exe", "wslhost.exe",
        "bash.exe", "conhost.exe", "openconsole.exe", "explorer.exe", "mmc.exe", "regasm.exe",
        "regsvcs.exe", "installutil.exe", "msbuild.exe", "presentationhost.exe", "dfsvc.exe"
    };

    public AutorunOwnershipClassifier(IAutorunOwnershipProbe? probe = null, string? windowsDirectory = null,
        Func<DateTime>? utcNow = null)
    {
        _probe = probe ?? new WindowsOwnershipProbe();
        _windowsDirectory = (windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public IReadOnlyList<AutorunEntry> Classify(IReadOnlyList<AutorunEntry> entries,
        CancellationToken token = default, Func<bool>? budgetAvailable = null)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var checkedFiles = 0;
        var result = new AutorunEntry[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var entry = entries[index];
            // Package SignatureKind.System is populated only by the live package scanner.
            // Ownership is JsonIgnore so an old backup cannot supply stale display attribution.
            var ownership = entry.SourceKind == AutorunSourceKind.PackagedStartup ? entry.Ownership : AutorunOwnership.Unknown;
            if (entry.SourceKind != AutorunSourceKind.PackagedStartup &&
                checkedFiles < MaximumFileChecks && clock.ElapsedMilliseconds < MaximumClassificationMilliseconds &&
                (budgetAvailable?.Invoke() ?? true))
            {
                var path = DirectComponentPath(entry);
                if (path.Length > 0)
                {
                    checkedFiles++;
                    if (IsProtected(path, token)) ownership = AutorunOwnership.Windows;
                }
            }
            result[index] = entry.Ownership == ownership ? entry : entry with { Ownership = ownership };
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private string DirectComponentPath(AutorunEntry entry)
    {
        // Tasks may have several Exec/COM actions; TargetPath alone omits that information.
        // A shortcut omits its arguments, and WMI/advanced entries may inject another payload.
        if (entry.SourceKind is not AutorunSourceKind.Service and not AutorunSourceKind.Driver and
            not AutorunSourceKind.RegistryRun and not AutorunSourceKind.RegistryRunOnce and not AutorunSourceKind.PolicyRun)
            return "";
        var command = entry.Command.Trim();
        if (command.Length == 0 || command.Length > 32767 || command.IndexOfAny(['\r', '\n', '\0']) >= 0) return "";
        if (command[0] == '"')
        {
            if (command.Length < 3 || command[command.Length - 1] != '"' || command.IndexOf('"', 1) != command.Length - 1) return "";
            command = command.Substring(1, command.Length - 2);
        }
        if (command.IndexOf('"') >= 0) return "";
        try
        {
            command = Environment.ExpandEnvironmentVariables(command);
            if (command.StartsWith(@"\??\", StringComparison.Ordinal)) command = command.Substring(4);
            if (command.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                command = Path.Combine(_windowsDirectory, command.Substring(12));
            // Driver ImagePath may legitimately be relative to the Windows directory.
            if (entry.SourceKind == AutorunSourceKind.Driver && command.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                command = Path.Combine(_windowsDirectory, command);
            if (!Path.IsPathFullyQualified(command) || command.StartsWith(@"\\", StringComparison.Ordinal) || command.IndexOf(':', 2) >= 0) return "";
            var path = Path.GetFullPath(command);
            if (_windowsDirectory.Length == 0 || !path.StartsWith(_windowsDirectory + "\\", StringComparison.OrdinalIgnoreCase)) return "";
            var extension = Path.GetExtension(path);
            if (!extension.Equals(entry.SourceKind == AutorunSourceKind.Driver ? ".sys" : ".exe", StringComparison.OrdinalIgnoreCase)) return "";
            if (SharedHosts.Contains(Path.GetFileName(path))) return "";
            // This also rejects unquoted/quoted executable paths followed by arguments.
            if (entry.TargetPath.Length > 0 && !Path.GetFullPath(entry.TargetPath).Equals(path, StringComparison.OrdinalIgnoreCase)) return "";
            return path;
        }
        catch { return ""; }
    }

    private bool IsProtected(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var identity = _probe.ReadIdentity(path, token);
            if (identity is null)
            {
                lock (_cacheGate) _cache.Remove(path); // Missing/inaccessible files are never permanently cached.
                return false;
            }
            var now = _utcNow();
            lock (_cacheGate)
                if (_cache.TryGetValue(path, out var cached) && cached.Identity == identity && now < cached.Expires)
                    return cached.Protected;
            token.ThrowIfCancellationRequested();
            var protectedFile = _probe.IsWindowsProtected(path, token);
            token.ThrowIfCancellationRequested();
            // An identity changed during evidence collection is left visible.
            if (_probe.ReadIdentity(path, token) != identity) return false;
            lock (_cacheGate)
            {
                if (_cache.Count >= MaximumCacheEntries) _cache.Clear();
                _cache[path] = new(identity, protectedFile, now.AddMinutes(5));
            }
            return protectedFile;
        }
        catch (OperationCanceledException) { throw; }
        catch { lock (_cacheGate) _cache.Remove(path); return false; }
    }

    private sealed class WindowsOwnershipProbe : IAutorunOwnershipProbe
    {
        public AutorunOwnershipFile? ReadIdentity(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            AutorunPathSafety.RejectReparseAncestors(path);
            return new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
        }
        public bool IsWindowsProtected(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return SfcIsFileProtected(nint.Zero, path);
        }
        // Windows XP and later; WRP is a local OS-membership query, not a publisher-string guess.
        // https://learn.microsoft.com/windows/win32/api/sfc/nf-sfc-sfcisfileprotected
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("sfc.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SfcIsFileProtected(nint rpcHandle, string path);
    }
}
