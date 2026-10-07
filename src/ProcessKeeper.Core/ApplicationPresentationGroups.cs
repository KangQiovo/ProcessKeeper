using System.Text;
using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

/// <summary>Presentation families retain exact source identities; never use a family key as mutation authority.</summary>
public sealed record AutorunApplicationGroup(string Key, string Name, string IconPath, IReadOnlyList<AutorunEntry> Entries);
public sealed record WhitelistApplicationGroup(string Key, string Name, IReadOnlyList<WhitelistRule> Rules);

public static class ApplicationPresentationGroups
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "msiexec.exe", "svchost.exe", "dllhost.exe", "explorer.exe", "wscript.exe", "cscript.exe", "mshta.exe", "regsvr32.exe", "conhost.exe", "dotnet.exe", "node.exe", "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "electron.exe", "runtimebroker.exe", "msedgewebview2.exe", "QtWebEngineProcess.exe" };
    private static readonly HashSet<string> SharedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "Common Files", "WindowsApps", "ProgramData", "Users", "AppData", "Local", "Roaming", "Temp", "Desktop", "Downloads", "Documents", "Apps", "Applications", "Programs", "Packages", "Microsoft", "bin" };
    private static readonly Regex Architecture = new(@"\s*\(?\b(?:x86|x64|arm64|32-bit|64-bit)\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex VersionSuffix = new(@"\s+v?\d+(?:\.\d+){1,3}(?:[-+]\w+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static string NormalizeName(string name, string version = "") => TextKey(DisplayName(name, version));
    public static string PublisherKey(string publisher) => TextKey(publisher);
    public static bool IsSharedHost(string path) => SharedHosts.Contains(Path.GetFileName(path));
    public static string DisplayName(string name, string version = "")
    {
        var value = name.Length > 512 ? name.Substring(0, 512) : name;
        value = Architecture.Replace(value.Trim(), "").Trim();
        if (version.Length > 0 && value.EndsWith(version, StringComparison.OrdinalIgnoreCase))
        {
            var at = value.Length - version.Length;
            if (at > 0 && char.IsWhiteSpace(value[at - 1])) value = value.Substring(0, at).TrimEnd();
        }
        value = VersionSuffix.Replace(value, "").Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - 4) : value;
    }
    private static string TextKey(string value)
    {
        value = value.Length > 512 ? value.Substring(0, 512) : value;
        try { value = value.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { /* Malformed metadata must not crash application lists. */ }
        var result = new StringBuilder();
        foreach (var character in value.Take(512)) if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }
    private static string FileKey(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();
    private static bool AppDirectory(string path) => path.Length > 3 && !SharedDirectories.Contains(Path.GetFileName(path.TrimEnd('\\', '/')));
    private static string KnownKey(InstalledApplication app)
    {
        if (app.ApplicationKey.StartsWith("package:", StringComparison.OrdinalIgnoreCase) || app.ApplicationKey.StartsWith("known:", StringComparison.OrdinalIgnoreCase)) return app.ApplicationKey.ToLowerInvariant();
        var keys = app.Executables.Select(exe => exe.ApplicationKey).Where(key => key.StartsWith("known:", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return keys.Length == 1 ? keys[0].ToLowerInvariant() : "";
    }

    private static IEnumerable<string> CopiedLauncherKeys(InstalledApplication app, InstalledExecutableIdentity roles)
    {
        var root = app.InstallLocation.TrimEnd('\\', '/');
        if (!AppDirectory(root)) yield break;
        // Launchers sometimes live next to a directly nested, numeric version
        // directory. Arbitrary descendants or a common drive/container are not
        // evidence that two programs belong to the same installation.
        var versionDirectory = Version.TryParse(Path.GetFileName(root).TrimStart('v', 'V'), out _);
        var productRoot = versionDirectory ? Path.GetDirectoryName(root) ?? "" : root;
        if (!AppDirectory(productRoot)) yield break;
        var name = NormalizeName(app.Name);
        if (name.Length == 0) yield break;
        var entries = new HashSet<string>(app.EntryPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var executable in app.Executables)
        {
            if (IsSharedHost(executable.Path) || !ProcessIdentity.IsUnderDirectory(executable.Path, root)
                || !versionDirectory && !entries.Contains(executable.Path)) continue;
            var company = PublisherKey(executable.CompanyName);
            if (company.Length == 0 || NormalizeName(executable.ProductName) != name
                || app.Publisher.Length > 0 && PublisherKey(app.Publisher) != company
                || string.IsNullOrWhiteSpace(executable.FileVersion) || executable.FileVersion.Length > 128) continue;
            var role = roles.Classify(app, executable).Role;
            if (role is not (InstalledExecutableRole.Main or InstalledExecutableRole.PotentialMain)) continue;
            yield return "copy:" + FileKey(productRoot) + "|" + name + "|" + FileKey(Path.GetFileName(executable.Path))
                + "|" + company + "|" + executable.FileVersion.Trim().ToLowerInvariant();
        }
    }

    private static string[] ExternalPackageLinks(InstalledApplication[] originals, string[] publishers)
    {
        var links = new string[originals.Length];
        var desktops = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        string ProductDirectory(string name, string root)
        {
            var product = NormalizeName(name);
            root = root.TrimEnd('\\', '/');
            if (product.Length == 0 || !AppDirectory(root) || !Path.IsPathFullyQualified(root)
                || root.StartsWith(@"\\", StringComparison.Ordinal)) return "";
            return product + "|" + FileKey(root);
        }
        for (var index = 0; index < originals.Length; index++)
        {
            var app = originals[index];
            if (app.ApplicationKey.StartsWith("package:", StringComparison.OrdinalIgnoreCase)) continue;
            var key = ProductDirectory(app.Name, app.InstallLocation);
            if (key.Length == 0) continue;
            if (!desktops.TryGetValue(key, out var bucket)) desktops[key] = bucket = new();
            bucket.Add(index);
        }
        foreach (var family in Enumerable.Range(0, originals.Length)
            .Where(index => originals[index].ApplicationKey.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(index => originals[index].ApplicationKey, StringComparer.OrdinalIgnoreCase))
        {
            var members = family.ToArray();
            if (members.Select(index => publishers[index]).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(2).Count() > 1) continue;
            var matches = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var index in members)
            {
                var app = originals[index];
                if (app.ExternalInstallLocation.Length == 0) continue;
                var key = ProductDirectory(app.Name, app.ExternalInstallLocation);
                if (desktops.TryGetValue(key, out var bucket)) matches[key] = bucket;
            }
            var vendors = matches.Values.SelectMany(bucket => bucket).Select(candidate => publishers[candidate])
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if (matches.Count == 0 || vendors.Length > 1) continue;
            // The OS-registered external content directory plus the same product
            // name links a sparse shell-extension package to its desktop app.
            // Its package publisher may differ from the Win32 registration's
            // publisher. This override is local to presentation grouping only.
            // Plan all versions of one package family together. A local override
            // must not split another version of that same registered identity.
            if (vendors.Length == 1) foreach (var index in members) publishers[index] = vendors[0];
            foreach (var index in members)
            {
                var key = ProductDirectory(originals[index].Name, originals[index].ExternalInstallLocation);
                if (!matches.ContainsKey(key)) continue;
                links[index] = "external-installed:" + key;
            }
            foreach (var match in matches) foreach (var candidate in match.Value) links[candidate] = "external-installed:" + match.Key;
        }
        return links;
    }

    public static IReadOnlyList<InstalledApplication> MergeInstalled(IReadOnlyList<InstalledApplication> source)
    {
        if (source.Count < 2) return source;
        var originals = source.SelectMany(app => app.Installations.Count == 0 ? new[] { app } : app.Installations).DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var publishers = originals.Select(app => PublisherKey(app.Publisher)).ToArray();
        var externalLinks = ExternalPackageLinks(originals, publishers);
        var roles = new InstalledExecutableIdentity();
        var entryProducts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var app in originals)
        foreach (var path in app.EntryPaths.Where(path => !IsSharedHost(path)))
        {
            var key = FileKey(path);
            if (!entryProducts.TryGetValue(key, out var products)) entryProducts[key] = products = new(StringComparer.Ordinal);
            var name = NormalizeName(app.Name); if (name.Length > 0) products.Add(name);
        }
        var strong = originals.Select((app, index) =>
        {
            var keys = new List<string>(); var known = KnownKey(app); if (known.Length > 0) keys.Add("identity:" + known);
            if (!string.IsNullOrEmpty(externalLinks[index])) keys.Add(externalLinks[index]);
            foreach (var path in app.EntryPaths.Where(path => !IsSharedHost(path))) keys.Add("entry:" + FileKey(path));
            if (AppDirectory(app.InstallLocation))
                foreach (var executable in app.Executables.Where(exe => !IsSharedHost(exe.Path) && ProcessIdentity.IsUnderDirectory(exe.Path, app.InstallLocation)))
                {
                    var path = FileKey(executable.Path);
                    keys.Add("component:" + FileKey(app.InstallLocation.TrimEnd('\\', '/')) + "|" + path);
                    // A recursive directory scan may contain another product.
                    // Bridge source/root differences only for a compatible
                    // named product and its main entry, never any shared helper.
                    if (entryProducts.TryGetValue(path, out var products) && products.Contains(NormalizeName(app.Name))
                        && roles.Classify(app, executable).Role is InstalledExecutableRole.Main or InstalledExecutableRole.PotentialMain)
                        keys.Add("entry:" + path);
                }
            keys.AddRange(CopiedLauncherKeys(app, roles));
            return keys.Distinct(StringComparer.Ordinal).ToArray();
        }).ToArray();
        // A package identity needs an actual file or OS external-directory link
        // to join a desktop record. Display-name/publisher equality alone is not
        // installation ownership, especially for unreadable shell-extension packages.
        var weak = originals.Select((app, index) => !app.ApplicationKey.StartsWith("package:", StringComparison.OrdinalIgnoreCase)
            && publishers[index].Length > 0 && NormalizeName(app.Name).Length > 0
            ? "product:" + NormalizeName(app.Name) + "|" + publishers[index] : "").ToArray();
        var groups = Connected(strong, weak, publishers);
        return groups.Select(indices =>
        {
            var members = indices.Select(index => originals[index]).OrderByDescending(app => app.Publisher.Length > 0)
                .ThenByDescending(app => !app.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(app => app.Id, StringComparer.Ordinal).ToArray();
            if (members.Length == 1) return members[0];
            var primary = members[0]; var known = members.Select(KnownKey).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var product = members.Where(app => app.Publisher.Length > 0).Select(app => "product:" + NormalizeName(app.Name) + "|" + PublisherKey(app.Publisher)).Order(StringComparer.Ordinal).FirstOrDefault();
            var effectivePublisher = indices.Select(index => publishers[index]).FirstOrDefault(value => value.Length > 0) ?? "";
            var family = known.Length == 1 ? known[0] + "|" + effectivePublisher : product ??
                "installation:" + indices.SelectMany(index => strong[index]).Order(StringComparer.Ordinal).FirstOrDefault() + "|" + PublisherKey(primary.Publisher);
            return primary with
            {
                Id = "presentation-installed:" + family, Name = DisplayName(primary.Name),
                Installations = Array.AsReadOnly(members),
                Executables = Array.AsReadOnly(members.SelectMany(app => app.Executables).DistinctBy(exe => exe.Path, StringComparer.OrdinalIgnoreCase).ToArray()),
                EntryPaths = Array.AsReadOnly(members.SelectMany(app => app.EntryPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
                IdentityEvidence = string.Join("\n", members.Select(app => app.IdentityEvidence).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal))
            };
        }).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(app => app.Id, StringComparer.Ordinal).ToArray();
    }

    // Strong evidence may connect a blank publisher only when that evidence has
    // exactly one nonblank publisher in the whole snapshot. No blank bridge can
    // silently combine two unrelated publishers.
    private static IReadOnlyList<int[]> Connected(string[][] strong, string[] weak, string[] publishers)
    {
        var parent = Enumerable.Range(0, strong.Length).ToArray();
        var componentPublishers = publishers.ToArray();
        var ambiguous = new bool[strong.Length];
        int Root(int index) { while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; } return index; }
        void Join(int left, int right)
        {
            left = Root(left); right = Root(right); if (left == right) return;
            var leftPublisher = componentPublishers[left]; var rightPublisher = componentPublishers[right];
            if (leftPublisher.Length > 0 && rightPublisher.Length > 0 && leftPublisher != rightPublisher ||
                ambiguous[left] && rightPublisher.Length > 0 || ambiguous[right] && leftPublisher.Length > 0) return;
            parent[right] = left; componentPublishers[left] = leftPublisher.Length > 0 ? leftPublisher : rightPublisher;
            ambiguous[left] |= ambiguous[right];
        }
        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < strong.Length; index++) foreach (var key in strong[index])
        { if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new(); bucket.Add(index); }
        // Missing publishers can form a chain across many buckets. Inspect the
        // whole blank connected component before attaching it to any publisher;
        // inspecting just immediate neighbours makes ownership scan-order dependent.
        var bucketPublishers = buckets.ToDictionary(pair => pair.Key, pair => pair.Value.Select(index => publishers[index]).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToArray(), StringComparer.Ordinal);
        var blankParent = Enumerable.Range(0, strong.Length).ToArray();
        int BlankRoot(int index) { while (blankParent[index] != index) { blankParent[index] = blankParent[blankParent[index]]; index = blankParent[index]; } return index; }
        foreach (var bucket in buckets.Values)
        {
            var first = -1;
            foreach (var index in bucket.Where(index => publishers[index].Length == 0))
            { if (first < 0) first = index; else blankParent[BlankRoot(index)] = BlankRoot(first); }
        }
        var neighbours = new Dictionary<int, HashSet<string>>();
        foreach (var pair in buckets)
        foreach (var index in pair.Value.Where(index => publishers[index].Length == 0))
        {
            var root = BlankRoot(index);
            if (!neighbours.TryGetValue(root, out var vendors)) neighbours[root] = vendors = new(StringComparer.Ordinal);
            foreach (var vendor in bucketPublishers[pair.Key]) if (vendors.Count < 2) vendors.Add(vendor);
        }
        for (var index = 0; index < ambiguous.Length; index++)
            ambiguous[index] = publishers[index].Length == 0 && neighbours.TryGetValue(BlankRoot(index), out var vendors) && vendors.Count > 1;
        foreach (var pair in buckets)
        {
            var known = bucketPublishers[pair.Key];
            foreach (var samePublisher in pair.Value.GroupBy(index => publishers[index].Length == 0 && !ambiguous[index] && known.Length == 1 ? known[0] : publishers[index], StringComparer.Ordinal))
            { var first = samePublisher.First(); foreach (var index in samePublisher.Skip(1)) Join(first, index); }
        }
        foreach (var bucket in Enumerable.Range(0, weak.Length).Where(index => weak[index].Length > 0).GroupBy(index => weak[index], StringComparer.Ordinal))
        { var first = bucket.First(); foreach (var index in bucket.Skip(1)) Join(first, index); }
        return Enumerable.Range(0, parent.Length).GroupBy(Root).Select(group => group.ToArray()).ToArray();
    }

    public static IReadOnlyList<ApplicationGroup> MergeRunning(IReadOnlyList<ApplicationGroup> source)
    {
        var result = new List<ApplicationGroup>();
        foreach (var family in source.GroupBy(app => app.Company.Length > 0 && NormalizeName(app.Name).Length > 0 && app.Processes.Count > 0 && app.Processes.All(process => !process.IsSystem && !IsSharedHost(process.Path))
            && !app.Key.StartsWith("known:", StringComparison.OrdinalIgnoreCase) && !app.Key.StartsWith("package:", StringComparison.OrdinalIgnoreCase)
                ? "presentation-running:" + NormalizeName(app.Name) + "|" + PublisherKey(app.Company) : app.Key, StringComparer.OrdinalIgnoreCase))
        {
            var members = family.OrderBy(app => app.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            if (members.Length == 1) { result.Add(members[0]); continue; }
            result.Add(members[0] with { Key = family.Key, Name = DisplayName(members[0].Name),
                Processes = members.SelectMany(app => app.Processes).DistinctBy(process => (process.Id, process.StartTimeUtcTicks)).ToArray(),
                IdentityEvidence = string.Join("\n", members.Select(app => app.IdentityEvidence).Distinct(StringComparer.Ordinal)) });
        }
        return result.OrderBy(app => app.Category).ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static IReadOnlyList<WhitelistRule> GetRunningRules(ApplicationGroup application)
    {
        var keys = application.Processes.Select(process => process.ApplicationKey).Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0 && !application.Key.StartsWith("presentation-", StringComparison.Ordinal)) keys = new[] { application.Key };
        return keys.Where(key => key.Length > 0 && !key.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase))
            .Select(key => new WhitelistRule { Name = application.Name, Kind = RuleKind.Application, Value = key }).ToArray();
    }

    private sealed class OwnershipIndex
    {
        private readonly Dictionary<string, (string Key, string Name, string Path)?> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Key, string Name, string Path)?> _identities = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Key, string Name, string Path)?> _roots = new(StringComparer.OrdinalIgnoreCase);
        internal OwnershipIndex(IReadOnlyList<InstalledApplication> installed, ProcessSnapshot snapshot)
        {
            var roles = new InstalledExecutableIdentity();
            foreach (var app in MergeInstalled(installed))
            {
                var known = KnownKey(app); var name = app.Name;
                var key = app.Id;
                var icon = roles.ResolveMain(app).ExecutablePath;
                if (icon.Length == 0) icon = app.EntryPaths.FirstOrDefault(path => !IsSharedHost(path)) ?? "";
                var owner = (key, name, icon);
                if (app.ApplicationKey.Length > 0) Add(_identities, app.ApplicationKey, owner);
                foreach (var exe in app.Executables)
                {
                    if (IsSharedHost(exe.Path)) continue;
                    Add(_paths, FileKey(exe.Path), owner);
                    if (exe.ApplicationKey.Length > 0) Add(_identities, exe.ApplicationKey, owner);
                }
                foreach (var member in app.Installations.Count == 0 ? new[] { app } : app.Installations)
                {
                    if (member.ApplicationKey.Length > 0) Add(_identities, member.ApplicationKey, owner);
                    if (AppDirectory(member.InstallLocation)) Add(_roots, FileKey(member.InstallLocation.TrimEnd('\\', '/')), owner);
                    if (member.ApplicationKey.StartsWith("package:", StringComparison.OrdinalIgnoreCase)
                        && AppDirectory(member.ExternalInstallLocation)) Add(_roots, FileKey(member.ExternalInstallLocation.TrimEnd('\\', '/')), owner);
                }
            }
            foreach (var app in snapshot.Applications)
            {
                var owner = (app.Key, app.Name, app.Processes.FirstOrDefault(process => !IsSharedHost(process.Path) && process.HasVisibleWindow)?.Path ?? "");
                foreach (var process in app.Processes)
                {
                    if (IsSharedHost(process.Path)) continue;
                    if (!_paths.ContainsKey(FileKey(process.Path)) && process.Path.Length > 0) Add(_paths, FileKey(process.Path), owner);
                    if (!_identities.ContainsKey(process.ApplicationKey) && process.ApplicationKey.Length > 0) Add(_identities, process.ApplicationKey, owner);
                }
            }
        }
        private static void Add(Dictionary<string, (string Key, string Name, string Path)?> index, string key, (string Key, string Name, string Path) owner)
        { if (!index.TryGetValue(key, out var previous)) index[key] = owner; else if (previous?.Key != owner.Key) index[key] = null; }
        internal (string Key, string Name, string Path)? PathOwner(string path)
        {
            if (path.Length == 0 || IsSharedHost(path)) return null;
            if (_paths.TryGetValue(FileKey(path), out var owner)) return owner;
            (string Key, string Name, string Path)? found = null;
            var directory = System.IO.Path.GetDirectoryName(path); var depth = 0;
            while (!string.IsNullOrEmpty(directory) && ++depth <= 64)
            {
                if (_roots.TryGetValue(FileKey(directory.TrimEnd('\\', '/')), out var parent))
                { if (parent is null || found is not null && found.Value.Key != parent.Value.Key) return null; found = parent; }
                directory = System.IO.Path.GetDirectoryName(directory);
            }
            if (found is not null) return found;
            var identity = ProcessIdentity.Classify(new ProcessRecord { Path = path, Name = System.IO.Path.GetFileName(path) });
            if (_identities.TryGetValue(identity.ApplicationKey, out owner)) return owner;
            return identity.ApplicationKey.StartsWith("known:", StringComparison.OrdinalIgnoreCase) ? (identity.ApplicationKey, identity.ApplicationName, path) : null;
        }
        internal (string Key, string Name, string Path)? RuleOwner(WhitelistRule rule)
        {
            if (rule.Kind == RuleKind.Application && _identities.TryGetValue(rule.Value, out var owner)) return owner;
            if (rule.Kind == RuleKind.Application && rule.Value.StartsWith("known:", StringComparison.OrdinalIgnoreCase))
                return (rule.Value.ToLowerInvariant(), WhitelistStore.GetDisplayName(rule), "");
            if (rule.Kind == RuleKind.ExecutablePath) return PathOwner(rule.Value);
            if (rule.Kind == RuleKind.Application && rule.Value.StartsWith("path:", StringComparison.OrdinalIgnoreCase)) return PathOwner(rule.Value.Substring(5));
            if (rule.Kind == RuleKind.Directory && _roots.TryGetValue(FileKey(rule.Value.TrimEnd('\\', '/')), out owner)) return owner;
            if (rule.Kind == RuleKind.ProcessName)
            {
                var owners = _paths.Where(pair => System.IO.Path.GetFileName(pair.Key).Equals(rule.Value, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).ToArray();
                var keys = owners.Select(value => value?.Key).Distinct(StringComparer.Ordinal).ToArray();
                if (keys.Length == 1 && keys[0] is not null) return owners[0];
            }
            return null;
        }
    }
    public static IReadOnlyList<AutorunApplicationGroup> GroupAutoruns(IReadOnlyList<AutorunEntry> entries, IReadOnlyList<InstalledApplication> installed, ProcessSnapshot snapshot)
    {
        var index = new OwnershipIndex(installed, snapshot);
        return entries.Select(entry => (Entry: entry, Owner: index.PathOwner(entry.TargetPath)))
            .GroupBy(item => item.Owner?.Key ?? (item.Entry.TargetPath.Length > 0 && !IsSharedHost(item.Entry.TargetPath) ? "path:" + FileKey(item.Entry.TargetPath) : "entry:" + item.Entry.Id), StringComparer.Ordinal)
            .Select(group => new AutorunApplicationGroup("autorun-app:" + group.Key, group.First().Owner?.Name ?? group.First().Entry.Name,
                group.First().Owner?.Path is { Length: > 0 } path ? path : group.First().Entry.TargetPath, Array.AsReadOnly(group.Select(item => item.Entry).ToArray())))
            .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static IReadOnlyList<WhitelistApplicationGroup> GroupRules(IReadOnlyList<WhitelistRule> rules, IReadOnlyList<InstalledApplication> installed, ProcessSnapshot snapshot)
    {
        var index = new OwnershipIndex(installed, snapshot);
        return rules.Select(rule => (Rule: rule, Owner: index.RuleOwner(rule))).GroupBy(item => item.Owner?.Key ?? "rule:" + item.Rule.Id, StringComparer.Ordinal)
            .Select(group => new WhitelistApplicationGroup("whitelist-app:" + group.Key, group.First().Owner?.Name ?? WhitelistStore.GetDisplayName(group.First().Rule),
                Array.AsReadOnly(group.Select(item => item.Rule).ToArray()))).ToArray();
    }
}
