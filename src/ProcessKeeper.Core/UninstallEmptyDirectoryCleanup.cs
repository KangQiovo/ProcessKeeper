using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

public interface IUninstallEmptyDirectoryCleanup
{
    IUninstallEmptyDirectoryLease? Capture(UninstallEntry entry, Func<UninstallSnapshot> inventory);
}
public interface IUninstallEmptyDirectoryLease : IDisposable
{
    bool RemoveIfStillOwned(UninstallSnapshot inventory);
}

/// <summary>Removes at most one explicitly registered, corroborated, empty installation leaf.
/// Never guesses a root from an executable, removes files, follows links, or walks upward.</summary>
public sealed class UninstallEmptyDirectoryCleanup : IUninstallEmptyDirectoryCleanup
{
    private static readonly HashSet<string> SharedNames = new(StringComparer.OrdinalIgnoreCase)
    { "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "Common Files", "ProgramData", "WindowsApps", "Users", "AppData", "Local", "Roaming", "Temp", "Tmp", "Desktop", "Downloads", "Documents", "App", "Apps", "Applications", "Programs", "Packages", "Installer", "Cache", "Microsoft", "Vendor", "Vendors", "Software", "Company", "Products", "bin", "x86", "x64", "arm64" };

    public IUninstallEmptyDirectoryLease? Capture(UninstallEntry entry, Func<UninstallSnapshot> inventory)
    {
        var root = CanonicalRoot(entry.InstallLocation);
        if (root is null || Protected(root) || entry.IsSystem || !entry.CanUninstall) return null;
        try
        {
            var snapshot = inventory();
            if (!InventoryClear(entry, root, snapshot, before: true)) return null;
            var current = snapshot.Entries.Single(item => item.Locator == entry.Locator);
            if (!Corroborated(current, root)) return null;
            return new Lease(entry, root);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string? CanonicalRoot(string value)
    {
        try
        {
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\');
            if (value.Length < 4 || value.Length >= 248 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' || value.IndexOf(':', 2) >= 0 || value.IndexOf('/') >= 0 || value.IndexOf('"') >= 0 || value.Any(char.IsControl) ||
                value.Split('\\').Skip(1).Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))) return null;
            var path = Path.GetFullPath(value);
            if (!path.Equals(value, StringComparison.OrdinalIgnoreCase)) return null;
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType is DriveType.Fixed or DriveType.Removable ? path : null;
        }
        catch { return null; }
    }
    private static bool Protected(string root)
    {
        if (SharedNames.Contains(Path.GetFileName(root))) return true;
        var parts = root.Split('\\').Skip(1).ToArray();
        if (parts.Any(part => part.Equals("Windows", StringComparison.OrdinalIgnoreCase) || part.Equals("System32", StringComparison.OrdinalIgnoreCase) || part.Equals("SysWOW64", StringComparison.OrdinalIgnoreCase) || part.Equals("Common Files", StringComparison.OrdinalIgnoreCase)) ||
            parts.Length == 2 && parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Within(root, path)) return true;
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && root.Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static bool Corroborated(UninstallEntry entry, string root)
    {
        if (!string.Equals(CanonicalRoot(entry.InstallLocation), root, StringComparison.OrdinalIgnoreCase)) return false;
        var leaf = NameKey(Path.GetFileName(root)); var name = NameKey(entry.Name);
        var publisher = NameKey(entry.Publisher);
        if (publisher.Length > 0 && (leaf == publisher || publisher.StartsWith(leaf, StringComparison.Ordinal) &&
            new[] { "inc", "incorporated", "ltd", "limited", "corp", "corporation", "company", "llc", "gmbh", "group", "team", "software", "technologies", "technology", "公司", "有限公司" }.Contains(publisher.Substring(leaf.Length)))) return false;
        bool VersionSuffix(string value) => value.Length > 0 && value.TrimStart('v').Length > 0 && value.TrimStart('v').All(char.IsDigit);
        var productLeaf = leaf.Length >= 3 && name.Length >= 3 && (leaf == name ||
            leaf.StartsWith(name, StringComparison.Ordinal) && VersionSuffix(leaf.Substring(name.Length)) ||
            name.StartsWith(leaf, StringComparison.Ordinal) && VersionSuffix(name.Substring(leaf.Length)));
        if (!productLeaf) return false;
        // A fresh, resolved main file directly in the declared installation is strong ownership evidence.
        if (entry.ApplicationIdentity is { IsResolved: true } main && (main.MatchedFileName || main.MatchedProductMetadata) &&
            entry.ApplicationPaths.Count <= UninstallApplicationIdentityResolver.MaximumExecutables && entry.ApplicationPaths.Contains(main.ExecutablePath, StringComparer.OrdinalIgnoreCase) &&
            UninstallPolicy.IsLocalExecutable(main.ExecutablePath) && Path.GetDirectoryName(main.ExecutablePath)!.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        // For products without a resolved main file, require both a direct registered native
        // uninstall route and a product-specific leaf name. Vendor containers are not inferred.
        return new[] { entry.Command, entry.QuietCommand }.Any(command => command is { IsMsi: false } && UninstallPolicy.IsLocalExecutable(command.Executable) &&
                Path.GetDirectoryName(command.Executable)!.Equals(root, StringComparison.OrdinalIgnoreCase));
    }
    private static string NameKey(string value)
    {
        var text = new StringBuilder(); foreach (var c in value) if (char.IsLetterOrDigit(c)) text.Append(char.ToLowerInvariant(c)); return text.ToString();
    }
    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    // Ownership hints have a different purpose from the deletion candidate. A noncanonical
    // registered hint may still name the same root; normalize it for comparison, never delete it.
    private static string? OwnershipPath(string value)
    {
        try
        {
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
            if (value.Length < 3 || value.Length >= 32700 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\' || value.IndexOf(':', 2) >= 0 || value.IndexOf('"') >= 0 || value.Any(char.IsControl)) return null;
            // Win32 trailing-dot/space and DOS short-name routes can alias a different
            // spelling of the same installation. Uncertain ownership keeps the directory.
            if (value.IndexOf('~') >= 0 || value.Split('\\').Skip(1).Any(part => part is not ("." or "..") && (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)))) return null;
            var path = Path.GetFullPath(value);
            if (path.Length > 3) path = path.TrimEnd('\\');
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType is DriveType.Fixed or DriveType.Removable ? path : null;
        }
        catch { return null; }
    }
    private static bool InventoryClear(UninstallEntry entry, string root, UninstallSnapshot snapshot, bool before)
    {
        if (snapshot.Truncated || snapshot.Warnings.Count != 0 || snapshot.Entries.Count > 10000) return false;
        var matching = snapshot.Entries.Where(item => item.Locator == entry.Locator).ToArray();
        if (before ? matching.Length != 1 || !UninstallPolicy.Same(entry, matching[0]) : matching.Length != 0) return false;
        var inspected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in snapshot.Entries)
        {
            if (before && other.Locator == entry.Locator) continue;
            var declared = OwnershipPath(other.InstallLocation);
            if (other.InstallLocation.Length > 0 && declared is null) return false;
            if (declared is not null && (Within(root, declared) || Within(declared, root))) return false;
            // Another registration may refer to this installation through a junction alias.
            // Do not follow the alias or infer exclusive ownership when its route is uncertain.
            if (declared is not null && !RegularRoute(declared, inspected)) return false;
            var paths = new[] { other.IconPath, other.ApplicationIdentity?.ExecutablePath ?? "", other.Command is { IsMsi: false } ? other.Command.Executable : "", other.QuietCommand is { IsMsi: false } ? other.QuietCommand.Executable : "" }
                .Concat(other.ApplicationPaths.Take(UninstallApplicationIdentityResolver.MaximumExecutables + 1));
            if (other.ApplicationPaths.Count > UninstallApplicationIdentityResolver.MaximumExecutables) return false;
            foreach (var path in paths)
            {
                if (path.Length == 0) continue;
                var owned = OwnershipPath(path);
                if (owned is null || Within(owned, root) || !RegularRoute(owned, inspected)) return false;
            }
        }
        return true;
    }
    private static bool RegularRoute(string path, Dictionary<string, bool> inspected)
    {
        var route = new Stack<string>();
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        { if (route.Count >= 64) return false; route.Push(current); }
        foreach (var item in route)
        {
            if (inspected.TryGetValue(item, out var safe)) { if (!safe) return false; continue; }
            if (inspected.Count >= 4096) return false;
            try { safe = (File.GetAttributes(item) & FileAttributes.ReparsePoint) == 0; }
            catch (FileNotFoundException) { safe = true; }
            catch (DirectoryNotFoundException) { safe = true; }
            catch { safe = false; }
            inspected[item] = safe;
            if (!safe) return false;
        }
        return true;
    }

    private sealed class Lease : IUninstallEmptyDirectoryLease
    {
        private readonly UninstallEntry _entry; private readonly string _root;
        private readonly List<SafeFileHandle> _parents = new();
        private SafeFileHandle? _original;
        private readonly FileInformation _identity;
        internal Lease(UninstallEntry entry, string root)
        {
            _entry = entry; _root = root;
            try
            {
                var parents = new Stack<string>();
                for (var current = Path.GetDirectoryName(root); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)) parents.Push(current!);
                var drive = Path.GetPathRoot(root)!; if (!parents.Contains(drive, StringComparer.OrdinalIgnoreCase)) parents.Push(drive);
                foreach (var parent in parents)
                {
                    var handle = OpenDirectory(parent, 0x80, 3); _parents.Add(handle); RequireRegular(handle, parent);
                }
                // Allow the vendor to remove/rename its own directory. Keep the original file ID
                // alive, and later reopen the current path without DELETE sharing to reject swaps.
                _original = OpenDirectory(root, 0x80, 7); _identity = RequireRegular(_original, root);
            }
            catch { Dispose(); throw; }
        }
        public bool RemoveIfStillOwned(UninstallSnapshot inventory)
        {
            try
            {
                if (_original is null || !InventoryClear(_entry, _root, inventory, before: false)) return false;
                var original = RequireRegular(_original, _root);
                using var current = OpenDirectory(_root, 0x80 | 0x10000 /* DELETE */, 3);
                var now = RequireRegular(current, _root);
                if (!Same(_identity, original) || !Same(original, now)) return false;
                using (var items = Directory.EnumerateFileSystemEntries(_root).GetEnumerator()) if (items.MoveNext()) return false;
                // Windows rejects FileDispositionInfo for a nonempty directory, including a
                // file created after our empty check. This operation targets the pinned handle.
                byte delete = 1;
                if (!SetFileInformationByHandle(current, 4, ref delete, 1)) return false;
                _original.Dispose(); _original = null;
                return true;
            }
            catch { return false; }
        }
        public void Dispose()
        {
            _original?.Dispose(); _original = null;
            for (int i = _parents.Count - 1; i >= 0; i--) _parents[i].Dispose(); _parents.Clear();
        }
        private static bool Same(FileInformation first, FileInformation second) => first.Volume == second.Volume && first.IndexHigh == second.IndexHigh && first.IndexLow == second.IndexLow && first.CreatedHigh == second.CreatedHigh && first.CreatedLow == second.CreatedLow;
        private static SafeFileHandle OpenDirectory(string path, uint access, uint sharing) => CreateFile(path, access, sharing, IntPtr.Zero, 3, 0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
        private static FileInformation RequireRegular(SafeFileHandle handle, string expected)
        {
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0x10) throw new IOException();
            var text = new StringBuilder(32768); uint length = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
            var canonical = expected.Length == 3 ? expected : expected.TrimEnd('\\');
            if (length == 0 || length >= text.Capacity || !text.ToString().Equals("\\\\?\\" + canonical, StringComparison.OrdinalIgnoreCase)) throw new IOException();
            return info;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
