using System.Xml;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Management.Deployment;

namespace ProcessKeeper.Core;

internal sealed record InstalledPackageSeed(
    string Name,
    string Publisher,
    string FamilyName,
    string FullName,
    string InstallLocation,
    IReadOnlyList<string> ExecutablePaths,
    string Warning,
    string ExternalInstallLocation = "");

/// <summary>Reads package registration and declared entry points without activating an application.</summary>
internal static class InstalledPackageCatalog
{
    private const int MaximumPackages = 1500;
    private const int MaximumApplications = 100;
    private const long MaximumManifestCharacters = 4 * 1024 * 1024;
    private static readonly HashSet<string> ManifestNamespaces = new(StringComparer.Ordinal)
    {
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10",
        "http://schemas.microsoft.com/appx/2010/manifest",
        "http://schemas.microsoft.com/appx/2013/manifest"
    };

    /// <summary>Exact registered package ownership for a readable file. Runs only inside the bounded publisher worker.</summary>
    internal static bool IsMicrosoftExecutable(string path, CancellationToken token)
    {
        try
        {
            var normalized = DisplayPath.Normalize(path);
            if (normalized.Length == 0) return false;
            var directory = Path.GetDirectoryName(normalized);
            for (var depth = 0; !string.IsNullOrEmpty(directory) && depth < 128; depth++, directory = Path.GetDirectoryName(directory))
            {
                token.ThrowIfCancellationRequested();
                var fullName = Path.GetFileName(directory);
                if (!MicrosoftPublisherProbe.IsMicrosoftPackageFullName(fullName)) continue;
                // FindPackageForUser never activates the app and avoids enumerating all packages or a drive.
                var package = new PackageManager().FindPackageForUser(string.Empty, fullName);
                if (package is null || !package.Id.FullName.Equals(fullName, StringComparison.Ordinal) ||
                    !DisplayPath.Normalize(package.InstalledLocation.Path).Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                    package.IsDevelopmentMode || package.IsFramework || package.IsResourcePackage) return false;
                return MicrosoftPublisherProbe.HasTrustedMicrosoftPackagePublisher(package.Id.Publisher,
                    package.Id.PublisherId, (int)package.SignatureKind, package.Status.VerifyIsOK());
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Missing registrations/native capabilities and unreadable package status stay visible. */ }
        return false;
    }

    internal static IReadOnlyList<InstalledPackageSeed> Read(
        ICollection<string> warnings, CancellationToken token = default, Func<bool>? budgetAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        token.ThrowIfCancellationRequested();
        var results = new List<InstalledPackageSeed>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Empty SID means the current user, including when this process is elevated.
            // Do not use GetAppListEntriesAsync: inventory must never activate installed apps.
            var packages = new PackageManager().FindPackagesForUser(string.Empty);
            int count = 0;
            foreach (Package package in packages)
            {
                token.ThrowIfCancellationRequested();
                if (budgetAvailable?.Invoke() == false) { warnings.Add(L.T("包注册读取达到本轮扫描时间边界，后续包未读取。")); break; }
                if (++count > MaximumPackages)
                {
                    warnings.Add(L.F($"已安装包超过 {MaximumPackages} 个，本次仅读取前 {MaximumPackages} 个。"));
                    break;
                }

                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    var id = package.Id;
                    string family = id.FamilyName;
                    string fullName = id.FullName;
                    string packageName = id.Name;
                    if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(fullName))
                    {
                        warnings.Add(L.T("某个已安装包的身份为空，已跳过。"));
                        continue;
                    }
                    if (!seen.Add(fullName)) continue;

                    var notes = new List<string>();
                    string name = ReadText(() => package.DisplayName, packageName, L.T("显示名称"), notes);
                    string publisher = ReadText(() => package.PublisherDisplayName, id.Publisher, L.T("发布者"), notes);
                    string location = ReadText(() => package.InstalledLocation.Path, string.Empty, L.T("安装位置"), notes);
                    var entries = ReadEntryPoints(location, notes, token);
                    // A readable manifest without Application entries is a runtime/component package.
                    // An unreadable manifest is inconclusive; retain its registered identity with a warning.
                    if (entries.HasApplications == false) continue;
                    string warning = string.Join("；", notes);
                    if (warning.Length > 0) warnings.Add($"{name}：{warning}");
                    results.Add(new InstalledPackageSeed(name, publisher, family, fullName,
                        location, entries.Paths, warning, ReadExternalInstallLocation(package)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    warnings.Add(L.F($"无法读取某个已安装包，已跳过：{ErrorText(ex)}"));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add(L.F($"无法完整读取当前用户的已安装包：{ErrorText(ex)}"));
        }
        return results.AsReadOnly();
    }

    private static string ReadText(Func<string> read, string fallback, string field, List<string> notes)
    {
        try
        {
            string? value = read();
            if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return value.Trim();
            notes.Add(L.F($"{field}为空或资源未解析，已使用注册身份"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add(L.F($"{field}不可读：{ErrorText(ex)}"));
        }
        return fallback;
    }

    private static string ReadExternalInstallLocation(Package package)
    {
        try
        {
            if (package.IsDevelopmentMode || !package.Status.VerifyIsOK()) return "";
            // Only Windows registration supplies this association. Manifest absolute paths and
            // EffectiveLocation are not substitutes for an explicitly registered external root.
            const string packageType = "Windows.ApplicationModel.Package";
            if (ApiInformation.IsPropertyPresent(packageType, "EffectiveExternalLocation"))
            {
                var location = package.EffectiveExternalLocation?.Path;
                if (!string.IsNullOrWhiteSpace(location))
                    return SelectExternalInstallLocation(location, false, true);
            }
            if (ApiInformation.IsPropertyPresent(packageType, "EffectiveExternalPath"))
                return SelectExternalInstallLocation(package.EffectiveExternalPath, false, true);
        }
        catch { /* Unavailable APIs, unreadable state and unresolved directories supply no association. */ }
        return "";
    }

    internal static string SelectExternalInstallLocation(string? location, bool isDevelopmentMode, bool isHealthy)
    {
        if (isDevelopmentMode || !isHealthy || string.IsNullOrWhiteSpace(location) || location.Length > 32767 ||
            location.Any(char.IsControl) || !Path.IsPathFullyQualified(location) || location.StartsWith(@"\\", StringComparison.Ordinal) ||
            location.Length < 4 || !char.IsAsciiLetter(location[0]) || location[1] != ':' || location.IndexOf(':', 2) >= 0 ||
            location.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0) return "";
        try
        {
            string candidate = location.Replace('/', '\\').TrimEnd('\\');
            string[] segments = candidate[3..].Split('\\');
            if (segments.Length > 128 || segments.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.'))) return "";
            string directory = Path.GetFullPath(candidate).TrimEnd('\\');
            if (directory.Equals(Path.GetPathRoot(directory)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                new[] { "Windows", "System32", "SysWOW64", "Program Files", "Program Files (x86)", "ProgramData", "WindowsApps",
                    "Users", "AppData", "Local", "Roaming", "Temp", "Desktop", "Downloads", "Documents" }
                .Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)) return "";
            if (new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ApplicationData }.Select(Environment.GetFolderPath).Where(value => value.Length > 0)
                .Any(value => directory.Equals(value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return "";
            int depth = 0;
            for (string? current = directory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (++depth > 128) return "";
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return "";
            }
            return directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return ""; }
    }

    private static (bool? HasApplications, IReadOnlyList<string> Paths) ReadEntryPoints(
        string location, List<string> notes, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location))
            {
                notes.Add(L.T("无法确认清单入口：安装位置不可用"));
                return (null, Array.Empty<string>());
            }
            string root = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string manifest = Path.Combine(root, "AppxManifest.xml");
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestCharacters,
                MaxCharactersFromEntities = 1024,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? foundationNamespace = null;
            int applicationsDepth = -1;
            int applicationCount = 0;
            int missingCount = 0;
            int rejectedCount = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                {
                    if (reader.LocalName != "Package" || !ManifestNamespaces.Contains(reader.NamespaceURI))
                        throw new XmlException(L.T("包清单根元素或命名空间不受支持。"));
                    foundationNamespace = reader.NamespaceURI;
                }
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == applicationsDepth)
                    applicationsDepth = -1;
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != foundationNamespace) continue;
                if (reader.Depth == 1 && reader.LocalName == "Applications" && !reader.IsEmptyElement)
                {
                    applicationsDepth = reader.Depth;
                    continue;
                }
                if (applicationsDepth != 1 || reader.Depth != 2 || reader.LocalName != "Application") continue;
                applicationCount++;
                if (applicationCount > MaximumApplications) continue;

                // Only declared Application executables count. Do not scan disk or collect DLL/extension attributes.
                string? executable = reader.GetAttribute("Executable");
                if (string.IsNullOrWhiteSpace(executable)) continue;
                if (!TryResolveExecutable(root, executable, out string path))
                {
                    rejectedCount++;
                    continue;
                }
                if (File.Exists(path)) paths.Add(path);
                else missingCount++;
            }
            if (foundationNamespace is null) throw new XmlException(L.T("包清单为空。"));
            if (applicationCount > MaximumApplications) notes.Add(L.F($"清单入口超过 {MaximumApplications} 个，仅采集前 {MaximumApplications} 个"));
            if (rejectedCount > 0) notes.Add(L.F($"{rejectedCount} 个入口不是包内的普通 EXE 文件，已忽略"));
            if (missingCount > 0) notes.Add(L.F($"{missingCount} 个入口文件不存在或不可读，保留包身份"));
            return (applicationCount > 0, Array.AsReadOnly(paths.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            notes.Add(L.F($"清单入口不可读：{ErrorText(ex)}"));
            return (null, Array.Empty<string>());
        }
    }

    private static bool TryResolveExecutable(string root, string executable, out string path)
    {
        path = string.Empty;
        try
        {
            // Reject traversal, absolute paths, alternate data streams and Win32 name normalization aliases.
            if (Path.IsPathRooted(executable) || executable.IndexOfAny([':', '\0', '*', '?', '"', '<', '>', '|']) >= 0)
                return false;
            string[] segments = executable.Split(['\\', '/']);
            if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith(' ') || s.EndsWith('.')))
                return false;
            if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string candidate = Path.GetFullPath(Path.Combine(root, executable));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

            // Reparse points could escape the lexical package root. Check only declared path components;
            // this is bounded by the manifest and never recursively enumerates installation directories.
            string component = root;
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (string segment in segments)
            {
                component = Path.Combine(component, segment);
                try
                {
                    if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch (FileNotFoundException) { break; }
                catch (DirectoryNotFoundException) { break; }
            }
            path = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string ErrorText(Exception ex)
    {
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 240 ? message : message[..240] + "…";
    }
}
