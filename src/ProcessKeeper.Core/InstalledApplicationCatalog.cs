using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ProcessKeeper.Core;

/// <summary>
/// Reads installation registrations across local drives. Never starts executables, resolves shell links,
/// follows reparse points, reads uninstall commands, or recursively scans an entire drive.
/// </summary>
public sealed class InstalledApplicationCatalog
{
    public const int MaximumExecutablesPerDirectory = 300;
    public const int MaximumVisitedEntriesPerDirectory = 2000;
    public const int MaximumDirectoryDepth = 2;
    public const int MaximumScanDurationSeconds = 120;
    public const int MaximumApplications = 2500;
    public const int MaximumTotalExecutables = 20000;
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    private static readonly string[] RegistrySources =
    [ @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\Microsoft\Windows\CurrentVersion\App Paths" ];

    public IReadOnlyList<InstalledApplication> Scan(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var result = new List<InstalledApplication>();
        var roots = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, InstalledExecutable>(StringComparer.OrdinalIgnoreCase);
        var componentCount = 0;
        var clock = Stopwatch.StartNew();
        bool BudgetAvailable() => result.Count < MaximumApplications && componentCount < MaximumTotalExecutables && metadata.Count < MaximumTotalExecutables &&
            clock.Elapsed < TimeSpan.FromSeconds(MaximumScanDurationSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var package in InstalledPackageCatalog.Read(warnings, cancellationToken, BudgetAvailable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BudgetAvailable()) break;
            var app = CreateApplication(package.Name, package.Publisher, package.InstallLocation, package.ExecutablePaths,
                L.F($"当前用户的 Windows 包：{package.FullName}；只读取清单入口，不扫描包子目录。{package.Warning}"),
                metadata, package.FamilyName, package.FullName, BudgetAvailable, cancellationToken, MaximumTotalExecutables - componentCount);
            if (app is not null) componentCount += AddDistinct(result, app);
        }
        foreach (var seed in ReadRegistry(warnings, cancellationToken, BudgetAvailable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!BudgetAvailable()) break;
            var entries = new List<string>(seed.Entries);
            if (seed.Root.Length > 0 && !IsBroadDirectory(seed.Root))
            {
                if (!roots.TryGetValue(seed.Root, out var scanned))
                {
                    scanned = ScanPaths(seed.Root, MaximumDirectoryDepth, MaximumExecutablesPerDirectory, warnings, cancellationToken, BudgetAvailable);
                    roots[seed.Root] = scanned;
                }
                entries.AddRange(scanned);
            }
            var app = CreateApplication(seed.Name, seed.Publisher, seed.Root, entries, seed.Evidence, metadata, budgetAvailable: BudgetAvailable,
                cancellationToken: cancellationToken, maximumEntries: MaximumTotalExecutables - componentCount);
            if (app is not null) componentCount += AddDistinct(result, app);
        }

        foreach (var location in new[] { Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonStartMenu })
        {
            if (!BudgetAvailable()) break;
            var directory = Environment.GetFolderPath(location);
            foreach (var shortcut in EnumerateBounded(directory, ".lnk", 5, 2500, 12000, warnings, cancellationToken, BudgetAvailable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!BudgetAvailable()) break;
                var target = ReadShortcutTarget(shortcut);
                if (target is null || result.Any(app => app.Executables.Any(entry => entry.Path.Equals(target, StringComparison.OrdinalIgnoreCase)))) continue;
                var app = CreateApplication(Path.GetFileNameWithoutExtension(shortcut), "", Path.GetDirectoryName(target)!, [target],
                    L.T("开始菜单快捷方式记录的本地 EXE 目标；只读取链接，不解析迁移目标、不读取参数、不启动。"), metadata, budgetAvailable: BudgetAvailable,
                    cancellationToken: cancellationToken, maximumEntries: MaximumTotalExecutables - componentCount);
                if (app is not null) componentCount += AddDistinct(result, app);
            }
        }
        if (!BudgetAvailable()) warnings.Add(L.F($"扫描达到 {MaximumScanDurationSeconds} 秒、{MaximumApplications} 个程序或 {MaximumTotalExecutables} 个文件的上限。可手动添加遗漏的软件；正在执行的系统读取不能立即中断。"));
        Warnings = Array.AsReadOnly(warnings.Distinct(StringComparer.Ordinal).Take(100).ToArray());
        return Array.AsReadOnly(result.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.InstallLocation, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public InstalledApplication? ScanDirectory(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        var root = NormalizeExistingDirectory(path);
        if (root is null || IsBroadDirectory(root))
        {
            Warnings = [L.T("请选择某个软件的安装目录；不扫描盘根、Windows、用户目录或通用程序容器目录。")];
            return null;
        }
        var paths = ScanPaths(root, MaximumDirectoryDepth, MaximumExecutablesPerDirectory, warnings, cancellationToken);
        Warnings = warnings.AsReadOnly();
        return CreateApplication(Path.GetFileName(root), "", root, paths,
            L.T("手动指定的安装目录；") + ScanLimitDescription, new(StringComparer.OrdinalIgnoreCase), cancellationToken: cancellationToken);
    }

    public InstalledApplication? FromExecutable(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Warnings = [];
        var entry = NormalizeExistingExecutable(path);
        return entry is null ? null : CreateApplication(Path.GetFileNameWithoutExtension(entry), "", Path.GetDirectoryName(entry)!, [entry],
            L.T("手动选择的现有 EXE；未执行文件。文件存在不等于可正常运行。"), new(StringComparer.OrdinalIgnoreCase), cancellationToken: cancellationToken);
    }

    /// <summary>Never broadens one installation into a whole directory or a process-name allow rule.</summary>
    public static IReadOnlyList<WhitelistRule> GetAppRules(InstalledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var rules = new List<WhitelistRule>();
        void Add(string key, string path, string name)
        {
            var candidate = new WhitelistRule { Name = name, Kind = RuleKind.Application, Value = key };
            if (!RulePortability.IsPortable(candidate))
            {
                if (NormalizeExistingExecutable(path) is not { } existing) return;
                candidate = candidate with { Kind = RuleKind.ExecutablePath, Value = existing };
            }
            if (rules.Any(rule => rule.Kind == candidate.Kind && rule.Value.Equals(candidate.Value, StringComparison.OrdinalIgnoreCase))) return;
            rules.Add(candidate);
        }
        if (application.ApplicationKey.Length > 0) Add(application.ApplicationKey, "", application.Name);
        foreach (var executable in application.Executables)
            Add(executable.ApplicationKey, executable.Path, application.Name + " | " + executable.Name);
        return rules.AsReadOnly();
    }

    private static string ScanLimitDescription => L.F($"最多扫描安装目录下 {MaximumDirectoryDepth} 层、{MaximumVisitedEntriesPerDirectory} 个文件/目录、{MaximumExecutablesPerDirectory} 个 EXE，跳过链接目录、steamapps 与 node_modules；仅发现文件，不保证能运行或覆盖全部组件。");

    private static InstalledApplication? CreateApplication(string name, string publisher, string root, IEnumerable<string> paths,
        string evidence, Dictionary<string, InstalledExecutable> metadata, string family = "", string identitySuffix = "", Func<bool>? budgetAvailable = null,
        CancellationToken cancellationToken = default, int maximumEntries = MaximumExecutablesPerDirectory)
    {
        var entries = new List<InstalledExecutable>();
        foreach (var candidate in paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Min(MaximumExecutablesPerDirectory, maximumEntries)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budgetAvailable?.Invoke() == false) break;
            var path = NormalizeExistingExecutable(candidate);
            if (path is null) continue;
            if (!metadata.TryGetValue(path, out var entry))
            {
                var description = "";
                try { description = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; }
                catch (Exception ex) when (IsReadFailure(ex)) { }
                var classified = ProcessIdentity.Classify(new ProcessRecord { Id = 1, Name = Path.GetFileName(path), Path = path, PackageFamilyName = family });
                entry = new InstalledExecutable { Path = path, Name = Path.GetFileName(path), Description = description, ApplicationKey = classified.ApplicationKey };
                metadata[path] = entry;
            }
            if (family.Length > 0)
                entry = entry with { ApplicationKey = ProcessIdentity.Classify(new ProcessRecord { Id = 1, Name = entry.Name, Path = path, PackageFamilyName = family }).ApplicationKey };
            entries.Add(entry);
        }
        if (entries.Count == 0 && family.Length == 0) return null;
        if (family.Length == 0 && entries.Count > 0)
        {
            var classified = ProcessIdentity.ClassifyAll(entries.Select((entry, index) => new ProcessRecord
            { Id = index + 100, Name = entry.Name, Path = entry.Path }));
            entries = entries.Select((entry, index) => entry with { ApplicationKey = classified[index].ApplicationKey }).ToList();
            if (entries.Any(entry => entry.Name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) && entry.ApplicationKey == "known:steam"))
                entries = entries.Where(entry => entry.ApplicationKey == "known:steam").ToList();
        }
        var packageKey = family.Length == 0 ? "" : ProcessIdentity.Classify(new ProcessRecord { Id = 1, PackageFamilyName = family }).ApplicationKey;
        var location = root.Length > 0 ? root : entries.Count > 0 ? Path.GetDirectoryName(entries[0].Path)! : "";
        return new InstalledApplication
        {
            Id = "installed:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((name + "|" + location + "|" + identitySuffix).ToLowerInvariant()))),
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(location) : name,
            Publisher = publisher, InstallLocation = location, ApplicationKey = packageKey,
            IdentityEvidence = evidence + (family.Length == 0 ? "\n" + ScanLimitDescription : ""),
            Executables = Array.AsReadOnly(entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray())
        };
    }

    private static int AddDistinct(List<InstalledApplication> applications, InstalledApplication candidate)
    {
        var index = applications.FindIndex(app => app.Id == candidate.Id);
        if (index < 0) { applications.Add(candidate); return candidate.Executables.Count; }
        var current = applications[index];
        applications[index] = current with
        {
            Executables = Array.AsReadOnly(current.Executables.Concat(candidate.Executables).DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).ToArray()),
            IdentityEvidence = string.Join("\n", new[] { current.IdentityEvidence, candidate.IdentityEvidence }.Distinct(StringComparer.Ordinal))
        };
        return applications[index].Executables.Count - current.Executables.Count;
    }

    private sealed record RegistrySeed(string Name, string Publisher, string Root, IReadOnlyList<string> Entries, string Evidence);

    private static IReadOnlyList<RegistrySeed> ReadRegistry(List<string> warnings, CancellationToken token, Func<bool> budgetAvailable)
    {
        var result = new List<RegistrySeed>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var source in RegistrySources)
        {
            token.ThrowIfCancellationRequested();
            if (!budgetAvailable()) break;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var parent = baseKey.OpenSubKey(source, writable: false);
                if (parent is null) continue;
                foreach (var childName in parent.GetSubKeyNames().Take(4000))
                {
                    token.ThrowIfCancellationRequested();
                    if (!budgetAvailable()) break;
                    try
                    {
                        using var child = parent.OpenSubKey(childName, writable: false);
                        if (child is null) continue;
                        var isAppPath = source.EndsWith("App Paths", StringComparison.Ordinal);
                        var name = isAppPath ? Path.GetFileNameWithoutExtension(childName) : child.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var declaredRoot = isAppPath ? null : child.GetValue("InstallLocation") as string;
                        var root = NormalizeExistingDirectory(declaredRoot ?? "");
                        if (!string.IsNullOrWhiteSpace(declaredRoot) && root is null) continue;
                        var path = ParseExecutableValue(child.GetValue(isAppPath ? "" : "DisplayIcon") as string);
                        if (root is null && path is null) continue;
                        root ??= Path.GetDirectoryName(path!)!;
                        result.Add(new RegistrySeed(name, child.GetValue("Publisher") as string ?? "", root,
                            path is null ? [] : [path], L.F($"{hive} {view} 的 {(isAppPath ? "App Paths" : L.T("卸载信息"))}注册项；未读取卸载命令。")));
                    }
                    catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add(L.T("部分软件注册项无法读取：") + ex.Message); }
                }
            }
            catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add(L.F($"{hive} {view} 安装记录无法读取：{ex.Message}")); }
        }
        return result;
    }

    private static IReadOnlyList<string> ScanPaths(string root, int depth, int maximum, List<string> warnings, CancellationToken token, Func<bool>? budgetAvailable = null) =>
        EnumerateBounded(root, ".exe", depth, maximum, MaximumVisitedEntriesPerDirectory, warnings, token, budgetAvailable);

    private static IReadOnlyList<string> EnumerateBounded(string directory, string extension, int depth, int maximum,
        int visitMaximum, List<string> warnings, CancellationToken token, Func<bool>? budgetAvailable = null)
    {
        var root = NormalizeExistingDirectory(directory);
        if (root is null) return [];
        var result = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && result.Count < maximum && visited < visitMaximum)
        {
            token.ThrowIfCancellationRequested();
            if (budgetAvailable?.Invoke() == false) break;
            var current = queue.Dequeue();
            try
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(current.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (budgetAvailable?.Invoke() == false) break;
                    if (++visited > visitMaximum || result.Count >= maximum) break;
                    try
                    {
                        var attributes = File.GetAttributes(item);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var leaf = Path.GetFileName(item);
                            if (current.Depth < depth && !leaf.Equals("steamapps", StringComparison.OrdinalIgnoreCase) && !leaf.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                                queue.Enqueue((item, current.Depth + 1));
                        }
                        else if (Path.GetExtension(item).Equals(extension, StringComparison.OrdinalIgnoreCase)) result.Add(item);
                    }
                    catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add(L.T("部分安装文件无法读取：") + ex.Message); }
                }
            }
            catch (Exception ex) when (IsReadFailure(ex)) { warnings.Add(L.T("部分安装目录无法读取：") + ex.Message); }
        }
        if (result.Count >= maximum || visited >= visitMaximum) warnings.Add(L.T("扫描已截断：") + root + L.T("；达到文件或遍历数量上限。"));
        return result.AsReadOnly();
    }

    private static string? ParseExecutableValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing < 0) return null;
            var suffix = value[(closing + 1)..].Trim();
            if (suffix.Length > 0 && !(suffix.StartsWith(',') && int.TryParse(suffix[1..].Trim(), out _))) return null;
            value = value[1..closing];
        }
        else
        {
            var comma = value.LastIndexOf(',');
            if (comma >= 0 && int.TryParse(value[(comma + 1)..].Trim(), out _)) value = value[..comma].TrimEnd();
        }
        return NormalizeExistingExecutable(value);
    }

    private static string? NormalizeExistingDirectory(string value)
    {
        var path = NormalizeLocalPath(value);
        return path is not null && Directory.Exists(path) && !HasReparseAncestor(path) ?
            path.Length == 3 ? path : path.TrimEnd(Path.DirectorySeparatorChar) : null;
    }

    private static string? NormalizeExistingExecutable(string value)
    {
        var path = NormalizeLocalPath(value);
        return path is not null && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !HasReparseAncestor(path) ? path : null;
    }

    private static string? NormalizeLocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var path = Path.GetFullPath(value);
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' ? path : null;
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return null; }
    }

    private static bool HasReparseAncestor(string path)
    {
        try
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return true; }
    }

    private static bool IsBroadDirectory(string path)
    {
        var canonical = path.TrimEnd('\\');
        if (canonical.Equals(Path.GetPathRoot(path)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        var leaf = Path.GetFileName(canonical);
        if (new[] { "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "ProgramData", "WindowsApps", "Users", "AppData", "Local", "Roaming", "Temp", "Desktop", "Downloads", "Documents" }.Contains(leaf, StringComparer.OrdinalIgnoreCase)) return true;
        return new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData }.Select(Environment.GetFolderPath).Where(value => value.Length > 0)
            .Any(value => canonical.Equals(value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadShortcutTarget(string path)
    {
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            ((IPersistFile)instance!).Load(path, 0); // STGM_READ. Do not call Resolve or invoke its target.
            var target = new StringBuilder(32768);
            ((IShellLinkRead)instance).GetPath(target, target.Capacity, nint.Zero, 4); // SLGP_RAWPATH
            return NormalizeExistingExecutable(target.ToString());
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return null; }
        finally { if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
    }

    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException or COMException or System.ComponentModel.Win32Exception;

    // GetPath is the first IShellLinkW slot. No mutation, resolution, arguments, or launch methods are exposed.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkRead
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maximum, nint findData, uint flags);
    }
}
