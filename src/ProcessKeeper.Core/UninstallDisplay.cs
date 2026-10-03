namespace ProcessKeeper.Core;

public enum UninstallAppCategory { SystemApplication, ThirdPartyApplication, Driver, Runtime }
public sealed record UninstallApplicationGroup(string Key, string Name, IReadOnlyList<UninstallEntry> Entries);

/// <summary>Presentation categories describe registration metadata, never trust or malware status.</summary>
public static class UninstallDisplay
{
    /// <summary>Groups application presentation evidence. Every child retains its original execution identity.</summary>
    public static IReadOnlyList<UninstallApplicationGroup> Group(IEnumerable<UninstallEntry> entries)
    {
        var source = new List<UninstallEntry>();
        var seen = new Dictionary<string, List<UninstallEntry>>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Id.Length > 0)
            {
                if (!seen.TryGetValue(entry.Id, out var sameId)) seen.Add(entry.Id, sameId = new List<UninstallEntry>());
                if (sameId.Any(previous => UninstallPolicy.Same(previous, entry) && previous.Name == entry.Name
                    && previous.Publisher == entry.Publisher && previous.Version == entry.Version)) continue;
                sameId.Add(entry);
            }
            source.Add(entry);
        }
        var names = source.Select(entry => DisplayNameKey(entry).Length == 0 ? "" : ApplicationPresentationGroups.NormalizeName(entry.Name, entry.Version)).ToArray();
        var publishers = source.Select(entry => ApplicationPresentationGroups.PublisherKey(entry.Publisher)).ToArray();
        var parents = Enumerable.Range(0, source.Count).ToArray();
        var parentPublishers = publishers.ToArray();
        var ambiguous = new bool[source.Count];
        int Find(int index)
        {
            while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
            return index;
        }
        void Join(int first, int second)
        {
            first = Find(first); second = Find(second);
            if (first == second || parentPublishers[first].Length > 0 && parentPublishers[second].Length > 0 &&
                !parentPublishers[first].Equals(parentPublishers[second], StringComparison.Ordinal) ||
                ambiguous[first] && parentPublishers[second].Length > 0 || ambiguous[second] && parentPublishers[first].Length > 0) return;
            parents[second] = first;
            if (parentPublishers[first].Length == 0) parentPublishers[first] = parentPublishers[second];
            ambiguous[first] |= ambiguous[second];
        }
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var evidence = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var keys = new string[source.Count][];
        for (int index = 0; index < source.Count; index++)
        {
            keys[index] = names[index].Length == 0 ? Array.Empty<string>() : ApplicationKeys(source[index]);
            if (names[index].Length == 0) continue;
            string nameKey = names[index] + "\0" + publishers[index];
            if (byName.TryGetValue(nameKey, out int previous)) Join(previous, index); else byName.Add(nameKey, index);
            foreach (var key in keys[index])
            {
                if (!evidence.TryGetValue(key, out var candidates)) evidence.Add(key, candidates = new List<int>());
                candidates.Add(index);
            }
        }
        var evidencePublishers = evidence.ToDictionary(pair => pair.Key,
            pair => pair.Value.Select(index => publishers[index]).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(2).ToArray(), StringComparer.Ordinal);
        var blankEvidence = new Dictionary<int, HashSet<string>>();
        for (int index = 0; index < source.Count; index++)
        {
            int parent = Find(index);
            if (parentPublishers[parent].Length > 0) continue;
            if (!blankEvidence.TryGetValue(parent, out var candidates)) blankEvidence.Add(parent, candidates = new HashSet<string>(StringComparer.Ordinal));
            foreach (var key in keys[index]) foreach (var publisher in evidencePublishers[key]) candidates.Add(publisher);
        }
        foreach (var candidate in blankEvidence) ambiguous[candidate.Key] = candidate.Value.Count > 1;
        foreach (var candidates in evidence.Values)
        {
            var knownPublishers = candidates.Select(index => publishers[index]).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var bucket in candidates.GroupBy(index => publishers[index].Length == 0 && knownPublishers.Length == 1 ? knownPublishers[0] : publishers[index], StringComparer.Ordinal))
            {
                int first = bucket.First();
                foreach (int next in bucket.Skip(1)) Join(first, next);
            }
        }
        return Enumerable.Range(0, source.Count).GroupBy(Find).Select(group =>
        {
            var indices = group.ToArray();
            var first = source[indices[0]];
            var commonEvidence = keys[indices[0]].Where(key => indices.All(index => keys[index].Contains(key, StringComparer.Ordinal)))
                .OrderBy(key => key.StartsWith("known:", StringComparison.Ordinal) ? 0 : 1).ThenBy(key => key, StringComparer.Ordinal).FirstOrDefault();
            string name = indices.Select(index => names[index]).Where(value => value.Length > 0).OrderBy(value => value, StringComparer.Ordinal).FirstOrDefault() ?? "";
            string key = commonEvidence is not null ? commonEvidence + "|publisher:" + parentPublishers[Find(indices[0])]
                : name.Length > 0 ? "name:" + name + "|publisher:" + parentPublishers[Find(indices[0])]
                : "registration:" + AutorunIdentity.Hash(first.Id + "|" + first.Registration + "|" + first.Fingerprint);
            return new UninstallApplicationGroup(key, ApplicationPresentationGroups.DisplayName(first.Name, first.Version), Array.AsReadOnly(indices.Select(index => source[index]).ToArray()));
        }).ToArray();
    }

    private static string[] ApplicationKeys(UninstallEntry entry)
    {
        var paths = entry.ApplicationPaths.Select(path => LocalPath(path, true)).Where(path => path is not null && !ApplicationPresentationGroups.IsSharedHost(path))
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        // An associated main executable is evidence; the shared installer or a common installation folder is not.
        if (paths.Length != 1) return Array.Empty<string>();
        var identity = ProcessIdentity.Classify(new ProcessRecord { Name = Path.GetFileName(paths[0]), Path = paths[0] }).ApplicationKey;
        return identity.StartsWith("known:", StringComparison.Ordinal)
            ? new[] { identity, "main:" + paths[0].ToUpperInvariant() } : new[] { "main:" + paths[0].ToUpperInvariant() };
    }

    public static bool MatchesFilter(UninstallEntry entry, bool advanced, int filter, ApplicationDisplayCatalog? display = null)
    {
        if (advanced) return filter switch
        {
            0 => true, 1 => !entry.IsHidden, 2 => entry.IsHidden, 3 => entry.IsRecommended,
            4 => entry.CanUninstall, 5 => !entry.CanUninstall, _ => false
        };
        if (!IsSimpleVisible(entry)) return false;
        var category = Classify(entry, display);
        return filter switch
        {
            0 => true, 1 => category == UninstallAppCategory.SystemApplication,
            2 => category == UninstallAppCategory.ThirdPartyApplication,
            3 => category is UninstallAppCategory.Driver or UninstallAppCategory.Runtime, _ => false
        };
    }

    public static bool IsSimpleVisible(UninstallEntry entry)
    {
        return DisplayNameKey(entry).Length > 0 && entry.Command != null && entry.ExecutableIdentity.Length > 0;
    }

    public static UninstallAppCategory Classify(UninstallEntry entry, ApplicationDisplayCatalog? display = null)
    {
        var name = entry.Name.Trim(); var publisher = entry.Publisher.Trim();
        bool microsoft = IsPublisher(publisher, "Microsoft", "Microsoft Corporation", "Microsoft Corp.");
        // Runtime families are classified before Microsoft/system entries. VC++ is never a driver.
        if (microsoft && (Starts(name, "Microsoft Visual C++ ") && Contains(name, "Redistributable", "Runtime", "运行库", "運行庫")
            || Starts(name, "Microsoft Windows Desktop Runtime", "Microsoft .NET Runtime", "Microsoft .NET Host", "Microsoft .NET Framework",
                "Microsoft ASP.NET Core", "Microsoft DirectX", ".NET Runtime", ".NET Desktop Runtime", "Windows Desktop Runtime"))
            || microsoft && Starts(name, "Microsoft .NET ") && Contains(name, " - Windows Server Hosting")
            || IsPublisher(publisher, "LunarG, Inc.", "LunarG Inc", "LunarG") && Starts(name, "Vulkan Run Time Libraries", "Vulkan Runtime")
            || IsPublisher(publisher, "Oracle Corporation", "Oracle", "Sun Microsystems, Inc.") && Starts(name, "Java(TM) ", "Java Runtime Environment", "Java SE Runtime Environment"))
            return UninstallAppCategory.Runtime;
        bool driverVendor = IsPublisher(publisher, "NVIDIA Corporation", "Intel Corporation", "Intel", "Advanced Micro Devices, Inc.", "Advanced Micro Devices, Inc", "AMD", "Realtek Semiconductor Corp.", "Realtek", "Qualcomm", "Qualcomm Technologies, Inc.", "MediaTek Inc.");
        if (Starts(name, "Windows Driver Package - ") || driverVendor &&
            (Contains(name, " Driver", " Drivers", "驱动", "驅動") || Starts(name, "AMD Chipset Software", "NVIDIA Graphics Driver")))
            return UninstallAppCategory.Driver;
        // IsSystem protects registered updates/child components from execution; it does not establish their publisher.
        return display?.IsMicrosoft(entry) == true ? UninstallAppCategory.SystemApplication : UninstallAppCategory.ThirdPartyApplication;
    }

    /// <summary>Invoke on a worker. Checks the resolved main file and at most three registered locations; never scans directories.</summary>
    public static string? ResolveApplicationLocation(UninstallEntry entry)
    {
        var main = entry.ApplicationPaths.Count == 1 ? LocalPath(entry.ApplicationPaths[0], true) : null;
        if (main != null && !SharedHost(main) && File.Exists(main)) return main;
        var location = LocalPath(entry.InstallLocation, false);
        if (location != null && !GenericSystemDirectory(location) && Directory.Exists(location)) return location;
        var icon = LocalPath(entry.IconPath, true);
        if (icon != null && !SharedHost(icon) && File.Exists(icon)) return icon;
        var command = entry.Command;
        var executable = command == null || command.IsMsi ? null : LocalPath(command.Executable, true);
        return executable != null && !SharedHost(executable) && File.Exists(executable) ? executable : null;
    }

    private static string? LocalPath(string value, bool executable)
    {
        try
        {
            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (value.Length < 4 || value.Length > 32759 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\'
                || value.IndexOf(':', 2) >= 0 || value.Any(char.IsControl) || value.Split('\\').Any(part => part is "." or "..")) return null;
            value = Path.GetFullPath(value).TrimEnd('\\');
            if (value.Length <= 3 || executable && !UninstallPolicy.IsLocalExecutable(value)) return null;
            return value;
        }
        catch { return null; }
    }
    private static bool GenericSystemDirectory(string path)
    {
        var tail = path.Length > 3 ? path.Substring(3).TrimEnd('\\') : "";
        return tail.Equals("Windows", StringComparison.OrdinalIgnoreCase) || tail.Equals("Windows\\System32", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("Windows\\SysWOW64", StringComparison.OrdinalIgnoreCase) || tail.Equals("Windows\\Installer", StringComparison.OrdinalIgnoreCase);
    }
    private static bool SharedHost(string path)
    {
        var name = Path.GetFileName(path);
        return new[] { "msiexec.exe", "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "regsvr32.exe", "control.exe", "explorer.exe", "dotnet.exe" }.Any(host => name.Equals(host, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsPublisher(string value, params string[] names) => names.Any(name => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    private static bool Starts(string value, params string[] names) => names.Any(name => value.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    private static bool Contains(string value, params string[] names) => names.Any(name => value.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
    private static string DisplayNameKey(UninstallEntry entry)
    {
        var name = entry.Name.Trim();
        if (!entry.HasDisplayName || name.Length == 0 || name.Length > 2048 || Guid.TryParse(name, out _)
            || name.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || name.StartsWith("HKLM:", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("HKCU:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase)
            || name.Length > 2 && char.IsLetter(name[0]) && name[1] == ':' && name[2] == '\\') return "";
        try
        {
            var result = new System.Text.StringBuilder(); bool space = false;
            foreach (char ch in name.Normalize(System.Text.NormalizationForm.FormKC))
                if (char.IsWhiteSpace(ch)) space = true;
                else { if (space && result.Length > 0) result.Append(' '); result.Append(ch); space = false; }
            return result.ToString();
        }
        catch (ArgumentException) { return ""; }
    }
}
