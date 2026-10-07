using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

/// <summary>Read-only original registration evidence. None of these fields authorize executing an installer or changing an application.</summary>
public sealed record InstalledPresentationRegistration(string ApplicationId, string RegistryKey, string Name, string Publisher,
    string Version, string InstallLocation, string DisplayIconPath, string MsiProductCode = "", string BundleProviderKey = "", string BundleCachePath = "");
public sealed record InstalledAdvertisedShortcut(string ApplicationId, string ProductCode, string TargetPath, string MainExecutablePath = "");
public sealed record InstalledRuntimeRegistration(string ProviderKey, string Version, string Architecture, string InstallLocation, string ExecutablePath);

public static class InstalledRegistrationPresentation
{
    public static IReadOnlyList<InstalledApplication> Associate(IReadOnlyList<InstalledApplication> applications,
        IReadOnlyList<InstalledPresentationRegistration> registrations, IReadOnlyList<InstalledAdvertisedShortcut> shortcuts,
        IReadOnlyList<InstalledRuntimeRegistration> runtimes)
    {
        if (applications.Count > InstalledApplicationCatalog.MaximumApplications || registrations.Count > 10000
            || shortcuts.Count > 5000 || runtimes.Count > 256) return applications;
        var originals = applications.ToArray();
        var byId = originals.Select((app, index) => (app.Id, index)).GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().index, StringComparer.OrdinalIgnoreCase);
        var links = originals.Select(app => new HashSet<string>(app.PresentationRegistrationLinks, StringComparer.Ordinal)).ToArray();
        var roles = new InstalledExecutableIdentity();
        var explicitDirectories = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < originals.Length; index++)
        foreach (var path in originals[index].EntryPaths.Take(300))
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is null || !SpecificDirectory(directory) || !originals[index].Executables.Any(exe => SamePath(exe.Path, path))) continue;
            if (!explicitDirectories.TryGetValue(directory, out var bucket)) explicitDirectories[directory] = bucket = [];
            if (!bucket.Contains(index)) bucket.Add(index);
        }
        foreach (var registration in registrations)
        {
            if (!byId.TryGetValue(registration.ApplicationId, out var ownerIndex)) continue;
            var owner = originals[ownerIndex]; var vendor = ApplicationPresentationGroups.PublisherKey(registration.Publisher);
            if (vendor.Length == 0 || !owner.Name.Equals(registration.Name, StringComparison.OrdinalIgnoreCase)
                || ApplicationPresentationGroups.PublisherKey(owner.Publisher) != vendor || registration.RegistryKey.Length is 0 or > 1024
                || registration.RegistryKey.Any(char.IsControl)) continue;
            var key = "registration:" + registration.RegistryKey.ToLowerInvariant();
            bool Link(int index)
            {
                var publisher = ApplicationPresentationGroups.PublisherKey(originals[index].Publisher);
                if (publisher.Length > 0 && publisher != vendor) return false;
                links[ownerIndex].Add(key); links[index].Add(key);
                return true;
            }
            if (Guid.TryParse(registration.MsiProductCode, out var product))
            foreach (var shortcut in shortcuts)
            {
                if (!Guid.TryParse(shortcut.ProductCode, out var shortcutProduct) || shortcutProduct != product
                    || !byId.TryGetValue(shortcut.ApplicationId, out var index)) continue;
                var app = originals[index];
                if (!app.EntryPaths.Any(path => SamePath(path, shortcut.TargetPath))
                    || !app.Executables.Any(exe => SamePath(exe.Path, shortcut.TargetPath))) continue;
                var publisher = ApplicationPresentationGroups.PublisherKey(app.Publisher);
                if (publisher.Length > 0 && publisher != vendor) continue;
                var main = owner.Executables.SingleOrDefault(exe => SamePath(exe.Path, shortcut.MainExecutablePath));
                if (main is null) continue;
                // GetPath may expose an advertised icon placeholder. The descriptor's registered component
                // supplies the actual entry; retain the original icon file as a component and its original ID/root.
                originals[index] = app with
                {
                    EntryPaths = app.EntryPaths.Where(path => !SamePath(path, shortcut.TargetPath)).Append(main.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Executables = app.Executables.Concat(new[] { main }).DistinctBy(exe => exe.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                    IdentityEvidence = app.IdentityEvidence + "\nMSI registered shortcut component: " + main.Path
                };
                Link(index);
            }
            if (owner.Executables.Any(exe => SamePath(exe.Path, registration.DisplayIconPath)))
            {
                var iconDirectory = Path.GetDirectoryName(registration.DisplayIconPath);
                if (iconDirectory is not null && explicitDirectories.TryGetValue(iconDirectory, out var candidates))
                foreach (var index in candidates)
                {
                    if (index == ownerIndex) continue;
                    var app = originals[index]; var name = ApplicationPresentationGroups.NormalizeName(registration.Name);
                    if (name.Length == 0 || ApplicationPresentationGroups.NormalizeName(app.Name) != name
                        || !SamePath(app.InstallLocation.TrimEnd('\\'), iconDirectory.TrimEnd('\\'))) continue;
                    var matches = app.Executables.Where(exe => app.EntryPaths.Any(path => SamePath(path, exe.Path))
                        && SamePath(Path.GetDirectoryName(exe.Path) ?? "", iconDirectory)
                        && ApplicationPresentationGroups.NormalizeName(exe.ProductName) == name
                        && ApplicationPresentationGroups.PublisherKey(exe.CompanyName) == vendor
                        && !string.IsNullOrWhiteSpace(exe.FileVersion) && exe.FileVersion.Length <= 128
                        && roles.Classify(app, exe).Role is InstalledExecutableRole.Main or InstalledExecutableRole.PotentialMain).Take(2).ToArray();
                    if (matches.Length == 1) Link(index);
                }
            }
            if (!registration.BundleProviderKey.StartsWith("CPython-", StringComparison.Ordinal)
                || !Version.TryParse(registration.BundleProviderKey[8..], out var providerVersion)
                || providerVersion.Build >= 0 || providerVersion.Revision >= 0) continue;
            var installer = owner.Executables.SingleOrDefault(exe => SamePath(exe.Path, registration.BundleCachePath));
            if (installer is null || !SamePath(registration.DisplayIconPath, registration.BundleCachePath)
                || installer.FileVersion != registration.Version || installer.ProductName != registration.Name
                || ApplicationPresentationGroups.PublisherKey(installer.CompanyName) != vendor) continue;
            var productMatch = PythonProduct.Match(installer.ProductName);
            if (!productMatch.Success || !Version.TryParse(productMatch.Groups["version"].Value, out var release)
                || release.Major != providerVersion.Major || release.Minor != providerVersion.Minor) continue;
            foreach (var runtime in runtimes.Where(runtime => runtime.ProviderKey == registration.BundleProviderKey
                && runtime.Version == providerVersion.ToString() && runtime.Architecture == productMatch.Groups["bits"].Value + "bit"))
            {
                if (!SpecificDirectory(runtime.InstallLocation) || !ProcessIdentity.IsUnderDirectory(runtime.ExecutablePath, runtime.InstallLocation)) continue;
                var directory = Path.GetDirectoryName(runtime.ExecutablePath);
                if (directory is null || !explicitDirectories.TryGetValue(directory, out var candidates)) continue;
                foreach (var index in candidates)
                {
                    var app = originals[index];
                    if (!SamePath(app.InstallLocation.TrimEnd('\\'), runtime.InstallLocation.TrimEnd('\\'))
                        || !app.EntryPaths.Any(path => SamePath(path, runtime.ExecutablePath))) continue;
                    var executable = app.Executables.SingleOrDefault(exe => SamePath(exe.Path, runtime.ExecutablePath));
                    if (executable is null || ApplicationPresentationGroups.NormalizeName(executable.ProductName) != "python"
                        || ApplicationPresentationGroups.PublisherKey(executable.CompanyName) != vendor
                        || !Version.TryParse(executable.FileVersion, out var runtimeRelease) || runtimeRelease != release) continue;
                    if (!Link(index)) continue;
                    originals[index] = app with
                    {
                        RegisteredRuntimeMainPaths = app.RegisteredRuntimeMainPaths.Append(executable.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                    originals[ownerIndex] = originals[ownerIndex] with
                    {
                        RegisteredRuntimeInstallerPaths = originals[ownerIndex].RegisteredRuntimeInstallerPaths.Append(installer.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    };
                }
            }
        }
        return originals.Select((app, index) => app with { PresentationRegistrationLinks = links[index].Order(StringComparer.Ordinal).ToArray() }).ToArray();
    }
    private static readonly Regex PythonProduct = new(@"^Python\s+(?<version>\d+\.\d+\.\d+)\s+\((?<bits>32|64)-bit\)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static bool SamePath(string left, string right) => left.Length > 0 && right.Length > 0
        && left.Replace('/', '\\').Equals(right.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    private static bool SpecificDirectory(string directory) => Path.IsPathFullyQualified(directory) && directory.Length > 3
        && !new[] { "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "Common Files", "Users", "ProgramData",
            "AppData", "Local", "Roaming", "Temp", "Apps", "Programs", "Packages", "WindowsApps", "Installer" }
        .Contains(Path.GetFileName(directory.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
}
