namespace ProcessKeeper.Core;

public enum UninstallAppCategory { SystemApplication, ThirdPartyApplication, Driver, Runtime }
public sealed record UninstallApplicationGroup(string Key, string Name, IReadOnlyList<UninstallEntry> Entries);

/// <summary>Presentation categories describe registration metadata, never trust or malware status.</summary>
public static class UninstallDisplay
{
    /// <summary>Groups display names only. Every child retains its original execution identity.</summary>
    public static IReadOnlyList<UninstallApplicationGroup> Group(IEnumerable<UninstallEntry> entries)
    {
        var groups = new List<(string Key, string Name, List<UninstallEntry> Entries)>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
            string key = DisplayNameKey(entry);
            if (key.Length > 0 && byName.TryGetValue(key, out int index)) groups[index].Entries.Add(entry);
            else
            {
                if (key.Length > 0) byName.Add(key, groups.Count);
                string groupKey = key.Length > 0 ? "name:" + key.ToUpperInvariant()
                    : "registration:" + AutorunIdentity.Hash(entry.Id + "|" + entry.Registration + "|" + entry.Fingerprint);
                groups.Add((groupKey, entry.Name, new List<UninstallEntry> { entry }));
            }
        }
        return groups.Select(group => new UninstallApplicationGroup(group.Key, group.Name, Array.AsReadOnly(group.Entries.ToArray()))).ToArray();
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
