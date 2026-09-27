using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ProcessKeeper.Launcher;

// This pre-UI reader accepts a language code only. It cannot supply executable paths or arguments.
internal static class LauncherBootstrapText
{
    internal const string FailureTitle = "Process Keeper | Startup failed";
    internal const string FailureIntro = "Process Keeper could not start. Startup stopped to avoid running incomplete or modified files.";
    internal const string FailureHelp = "Check free disk space and directory permissions, then retry. If the file is damaged, download a complete ProcessKeeper.exe again.";
    internal const int MaximumSettingsBytes = 16 * 1024;

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        [FailureTitle] = "Process Keeper | 启动失败",
        [FailureIntro] = "Process Keeper 无法启动。为避免运行不完整或已被修改的文件，启动已停止。",
        [FailureHelp] = "请检查磁盘可用空间及目录权限后重试。如果文件已损坏，请重新下载完整的 ProcessKeeper.exe。",
        ["The embedded file manifest is missing. Download a complete ProcessKeeper.exe again."] = "缺少内嵌文件清单。请重新下载完整的 ProcessKeeper.exe。",
        ["The embedded application is missing. Download a complete ProcessKeeper.exe again."] = "缺少内嵌应用。请重新下载完整的 ProcessKeeper.exe。",
        ["Windows could not start Process Keeper."] = "Windows 无法启动 Process Keeper。",
        ["The system application data directory is unavailable."] = "系统应用数据目录不可用。",
        ["A cache file is a link. Startup stopped."] = "缓存文件是链接，启动已停止。",
        ["A cache ancestor is missing or contains a symbolic link or junction: "] = "缓存的上级目录缺失，或包含符号链接或联接点：",
        ["The cache owner or permissions are untrusted. Ask an administrator to inspect the directory. Its ownership and permissions were not changed."] = "缓存的所有者或权限不可信。请管理员检查该目录；其所有权和权限未被更改。",
        ["The cache has unexpected permissions and cannot be used. Its ownership was not changed."] = "缓存权限异常，无法使用；其所有权未被更改。",
        ["The cache must grant exclusive access to Administrators and SYSTEM."] = "缓存必须仅允许 Administrators 和 SYSTEM 访问。",
        ["A system data directory ancestor has an untrusted owner. Startup stopped."] = "系统数据目录的上级目录所有者不可信，启动已停止。",
        ["Other accounts can replace the system cache root. Startup stopped."] = "其他账户可以替换系统缓存根目录，启动已停止。",
        ["Cache directory is missing or occupied by a file: "] = "缓存目录缺失或被文件占用：",
        ["Startup stopped because the cache path contains a symbolic link or junction: "] = "缓存路径包含符号链接或联接点，启动已停止：",
        ["The cache lock path is unsafe."] = "缓存锁路径不安全。",
        ["Extracted files failed integrity verification."] = "解压后的文件未通过完整性验证。",
        ["The application cache changed. Startup stopped."] = "应用缓存发生变化，启动已停止。",
        ["The application cache contains an unsafe link."] = "应用缓存包含不安全的链接。",
        ["The application cache contains an unlisted file."] = "应用缓存包含清单之外的文件。",
        ["Cache file verification failed: "] = "缓存文件验证失败：",
        ["The application cache is missing a required file."] = "应用缓存缺少必需文件。",
        ["The embedded payload does not match its manifest."] = "内嵌应用包与清单不匹配。",
        ["The embedded ZIP contains unlisted, duplicate, or unsafe files."] = "内嵌 ZIP 包含清单之外、重复或不安全的文件。",
        ["An extracted file has an unexpected length."] = "解压后的文件大小异常。",
        ["Refusing to quarantine a path outside the version cache."] = "无法隔离版本缓存之外的路径。",
        ["The original launcher path is unsafe."] = "原始启动器路径不安全。",
        ["The original single-file launcher location cannot be verified."] = "无法验证原始单文件启动器的位置。",
        ["The original launcher has changed."] = "原始启动器已发生变化。",
        ["The original launcher has changed. Reopen the original single-file application."] = "原始启动器已发生变化。请重新打开原始单文件应用。",
        ["The application version identifier is invalid."] = "应用版本标识无效。",
        ["The current user's local application data directory is unavailable."] = "当前用户的本地应用数据目录不可用。",
        ["The current user could not be verified."] = "无法验证当前用户。",
        ["The access-page cache has unexpected ownership. Startup stopped."] = "权限提示页缓存的所有者异常，启动已停止。",
        ["The access-page cache has unexpected permissions."] = "权限提示页缓存的权限异常。",
        ["The access-page cache is missing required permissions."] = "权限提示页缓存缺少所需权限。",
        ["The embedded file manifest has an invalid format."] = "内嵌文件清单格式无效。",
        ["The embedded application entry point is invalid."] = "内嵌应用入口无效。",
        ["The embedded manifest contains an empty entry."] = "内嵌清单包含空项目。",
        ["An embedded file has an invalid size."] = "内嵌文件大小无效。",
        ["The embedded manifest is too large or contains duplicate paths."] = "内嵌清单过大或包含重复路径。",
        ["The embedded manifest is missing the main application."] = "内嵌清单缺少主应用。",
        ["The embedded manifest contains a file/directory conflict."] = "内嵌清单存在文件与目录冲突。",
        ["The embedded file manifest has an invalid size."] = "内嵌文件清单大小无效。",
        ["The embedded file manifest could not be read."] = "无法读取内嵌文件清单。",
        ["The embedded payload could not be read."] = "无法读取内嵌应用包。",
        ["The embedded payload failed SHA-256 verification. Download a complete ProcessKeeper.exe again."] = "内嵌应用包未通过 SHA-256 验证。请重新下载完整的 ProcessKeeper.exe。",
        ["An embedded file path escapes the application cache."] = "内嵌文件路径超出应用缓存目录。",
        ["The embedded manifest contains an unsafe path."] = "内嵌清单包含不安全的路径。",
        ["The embedded manifest contains an unsafe path segment."] = "内嵌清单包含不安全的路径片段。",
        ["The embedded manifest contains a Windows reserved name."] = "内嵌清单包含 Windows 保留名称。",
        ["The embedded manifest contains an invalid SHA-256 value."] = "内嵌清单包含无效的 SHA-256 值。"
    };

    internal static string CurrentLanguage()
    {
        var preference = "auto";
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper", "view.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= MaximumSettingsBytes)
            {
                var bytes = new byte[MaximumSettingsBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                preference = ReadPreference(bytes.AsMemory(0, count));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
        var region = "";
        var zone = "";
        try { region = RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch (ArgumentException) { }
        try { zone = TimeZoneInfo.Local.Id; } catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException) { }
        return Resolve(preference, CultureInfo.CurrentUICulture.Name, region, zone);
    }

    internal static string ReadPreference(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumSettingsBytes) return "auto";
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "auto";
            var preferences = doc.RootElement.EnumerateObject().Where(p => p.NameEquals("Language")).ToArray();
            if (preferences.Length != 1 || preferences[0].Value.ValueKind != JsonValueKind.String) return "auto";
            var preference = preferences[0].Value.GetString();
            return preference is "auto" or "en" or "zh-Hans" or "zh-Hant" ? preference : "auto";
        }
        catch (JsonException) { return "auto"; }
    }

    // Keep the pre-UI fallback in sync with Core/LanguageResolver; no Core runtime dependency.
    internal static string Resolve(string preference, string displayLanguage, string region, string timeZone)
    {
        if (preference is "en" or "zh-Hans" or "zh-Hant") return preference;
        var language = displayLanguage.Replace('_', '-').ToLowerInvariant();
        if (language == "en" || language.StartsWith("en-")) return "en";
        if (language == "zh" || language.StartsWith("zh-"))
        {
            if (language.Contains("-hant") || language is "zh-tw" or "zh-hk" or "zh-mo") return "zh-Hant";
            if (language.Contains("-hans") || language is "zh-cn" or "zh-sg" or "zh-my") return "zh-Hans";
            return region.ToUpperInvariant() is "TW" or "HK" or "MO" || TraditionalZone(timeZone) ? "zh-Hant" : "zh-Hans";
        }
        if (!string.IsNullOrWhiteSpace(language) && language != "iv") return "en";
        if (region.ToUpperInvariant() is "TW" or "HK" or "MO" || TraditionalZone(timeZone)) return "zh-Hant";
        if (region.ToUpperInvariant() is "CN" or "SG" || timeZone is "China Standard Time" or "Asia/Shanghai" or "Asia/Singapore") return "zh-Hans";
        return "en";
    }

    private static bool TraditionalZone(string zone) => zone is "Taipei Standard Time" or "Asia/Taipei" or "Asia/Hong_Kong" or "Asia/Macau";

    internal static string Text(string source, string language)
    {
        if (language == "en") return source;
        foreach (var pair in Chinese.OrderByDescending(p => p.Key.Length))
        {
            // Only the three known path prefixes may have a dynamic suffix.
            if (source != pair.Key && !(pair.Key.EndsWith(": ", StringComparison.Ordinal) && source.StartsWith(pair.Key, StringComparison.Ordinal))) continue;
            var translated = language == "zh-Hant" ? ToTraditional(pair.Value) : pair.Value;
            return translated + source[pair.Key.Length..];
        }
        return source;
    }

    private static string ToTraditional(string source)
    {
        var size = LCMapStringEx("zh-TW", 0x04000000, source, -1, null, 0, IntPtr.Zero, IntPtr.Zero, 0);
        if (size <= 0) return source;
        var output = new StringBuilder(size);
        return LCMapStringEx("zh-TW", 0x04000000, source, -1, output, size, IntPtr.Zero, IntPtr.Zero, 0) > 0 ? output.ToString().TrimEnd('\0') : source;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(string localeName, uint flags, string source, int sourceLength,
        StringBuilder? destination, int destinationLength, IntPtr versionInformation, IntPtr reserved, nint sortHandle);
}
