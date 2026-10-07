using ProcessKeeper.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessKeeper.App;

public sealed record ViewPreferences(bool LiveRefresh = true, bool ShowSystemProcesses = false,
    int CategoryFilter = 0, int SortOrder = 0, string Language = "auto", bool HistoryAutoScroll = true, string InstalledDrive = "",
    bool GroupGamePlatforms = true, bool HideMicrosoftApps = false, bool SyncEmptyProfileRules = true);

public sealed class ViewPreferencesStore
{
    public string FilePath { get; }

    public ViewPreferencesStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        FilePath = Path.Combine(Path.GetFullPath(directory), "view.json");
    }

    public ViewPreferences Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > 16 * 1024) throw new InvalidDataException(L.T("列表配置不能超过 16 KiB。"));
            using var json = JsonDocument.Parse(stream);
            return SettingsJson.ReadView(json.RootElement);
        }
        catch (FileNotFoundException) { return new ViewPreferences(); }
        catch (DirectoryNotFoundException) { return new ViewPreferences(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new InvalidDataException(L.T("列表显示配置无法读取，原文件未修改。") + ex.Message, ex);
        }
    }

    public void Save(ViewPreferences value) => SettingsJson.AtomicWrite(FilePath, SettingsJson.ViewBytes(value));
}

internal static class SettingsJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static void RequireObject(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.T("设置字段必须是对象，不能为 null。"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException(L.T("设置包含重复或未知字段：") + property.Name);
        if (names.Any(name => !seen.Contains(name))) throw new InvalidDataException(L.T("设置缺少必要字段。"));
    }

    internal static int Integer(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new InvalidDataException(name + L.T(" 必须是整数。"));
        return number;
    }

    internal static bool Boolean(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(name + L.T(" 必须是布尔值。"));
        return value.GetBoolean();
    }

    internal static T EnumValue<T>(JsonElement element, string name) where T : struct, Enum
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || !Enum.TryParse<T>(value.GetString(), out var parsed) ||
            !Enum.IsDefined(parsed) || Enum.GetName(parsed) != value.GetString())
            throw new InvalidDataException(name + L.T(" 包含未知选项。"));
        return parsed;
    }

    internal static void Version(JsonElement element)
    {
        if (Integer(element, "Version") != 1) throw new InvalidDataException(L.T("设置版本不受支持。"));
    }

    internal static ViewPreferences ReadView(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.T("设置字段必须是对象，不能为 null。"));
        var hasLanguage = root.TryGetProperty("Language", out var language);
        var hasHistoryAutoScroll = root.TryGetProperty("HistoryAutoScroll", out _);
        var hasInstalledDrive = root.TryGetProperty("InstalledDrive", out var installedDrive);
        var hasGroupGamePlatforms = root.TryGetProperty("GroupGamePlatforms", out _);
        var hasHideMicrosoftApps = root.TryGetProperty("HideMicrosoftApps", out _);
        var hasSyncEmptyProfileRules = root.TryGetProperty("SyncEmptyProfileRules", out _);
        var fields = new List<string> { "Version", "LiveRefresh", "ShowSystemProcesses", "CategoryFilter", "SortOrder" };
        if (hasLanguage) fields.Add("Language");
        if (hasHistoryAutoScroll) fields.Add("HistoryAutoScroll");
        if (hasInstalledDrive) fields.Add("InstalledDrive");
        if (hasGroupGamePlatforms) fields.Add("GroupGamePlatforms");
        if (hasHideMicrosoftApps) fields.Add("HideMicrosoftApps");
        if (hasSyncEmptyProfileRules) fields.Add("SyncEmptyProfileRules");
        RequireObject(root, fields.ToArray());
        Version(root);
        if (hasLanguage && (language.ValueKind != JsonValueKind.String || !LanguageResolver.IsPreference(language.GetString())))
            throw new InvalidDataException(L.T("语言选项无效。"));
        if (hasInstalledDrive && installedDrive.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(L.T("盘符筛选无效。"));
        var value = new ViewPreferences(Boolean(root, "LiveRefresh"), Boolean(root, "ShowSystemProcesses"),
            Integer(root, "CategoryFilter"), Integer(root, "SortOrder"), hasLanguage ? language.GetString()! : "auto",
            !hasHistoryAutoScroll || Boolean(root, "HistoryAutoScroll"), hasInstalledDrive ? installedDrive.GetString()! : "",
            !hasGroupGamePlatforms || Boolean(root, "GroupGamePlatforms"), hasHideMicrosoftApps && Boolean(root, "HideMicrosoftApps"),
            !hasSyncEmptyProfileRules || Boolean(root, "SyncEmptyProfileRules"));
        ValidateView(value);
        return value;
    }

    internal static AppearancePreferences ReadAppearance(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out _))
            throw new InvalidDataException(L.T("设置缺少必要字段。"));
        var version = Integer(root, "Version");
        if (version == 1)
        {
            RequireObject(root, "Version", "BackdropEnabled", "Material", "Theme");
            return new AppearancePreferences(Boolean(root, "BackdropEnabled"),
                EnumValue<BackdropMaterial>(root, "Material"), EnumValue<AppearanceTheme>(root, "Theme"));
        }
        if (version != 2) throw new InvalidDataException(L.T("设置版本不受支持。"));
        RequireObject(root, "Version", "BackdropEnabled", "Material", "Theme", "CustomBackdropEnabled", "TintOpacity", "LuminosityOpacity", "TintColor");
        var tint = root.GetProperty("TintOpacity"); var luminosity = root.GetProperty("LuminosityOpacity");
        if (tint.ValueKind != JsonValueKind.Number || !tint.TryGetDouble(out var tintOpacity) ||
            luminosity.ValueKind != JsonValueKind.Number || !luminosity.TryGetDouble(out var luminosityOpacity))
            throw new InvalidDataException(L.T("材质不透明度必须是 0 到 1 之间的有限数值。"));
        var color = root.GetProperty("TintColor");
        if (color.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException(L.T("材质颜色必须是 #RRGGBB，或使用随主题颜色。"));
        return ValidateAppearance(new AppearancePreferences(Boolean(root, "BackdropEnabled"),
            EnumValue<BackdropMaterial>(root, "Material"), EnumValue<AppearanceTheme>(root, "Theme"),
            Boolean(root, "CustomBackdropEnabled"), tintOpacity, luminosityOpacity, color.GetString()));
    }

    internal static void ValidateView(ViewPreferences? value)
    {
        if (value is null || !LanguageResolver.IsPreference(value.Language) || value.CategoryFilter is < 0 or > 4 || value.SortOrder is < 0 or > 3 ||
            (!value.ShowSystemProcesses && value.CategoryFilter == 4))
            throw new InvalidDataException(L.T("列表选项无效；系统分类需要同时启用显示系统进程。"));
        if (value.InstalledDrive is null || (value.InstalledDrive.Length != 0 &&
            (value.InstalledDrive.Length != 2 || value.InstalledDrive[0] is < 'A' or > 'Z' || value.InstalledDrive[1] != ':')))
            throw new InvalidDataException(L.T("盘符筛选无效。"));
    }

    internal static byte[] ViewBytes(ViewPreferences value)
    {
        ValidateView(value);
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, value.LiveRefresh, value.ShowSystemProcesses, value.CategoryFilter, value.SortOrder, value.Language, value.HistoryAutoScroll, value.InstalledDrive, value.GroupGamePlatforms, value.HideMicrosoftApps, value.SyncEmptyProfileRules }, Options);
    }

    internal static byte[] AppearanceBytes(AppearancePreferences? value)
    {
        var normalized = ValidateAppearance(value);
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, normalized.BackdropEnabled, normalized.Material, normalized.Theme,
            normalized.CustomBackdropEnabled, normalized.TintOpacity, normalized.LuminosityOpacity, normalized.TintColor }, Options);
    }

    internal static AppearancePreferences ValidateAppearance(AppearancePreferences? value)
    {
        if (value is null || !Enum.IsDefined(value.Material) || !Enum.IsDefined(value.Theme))
            throw new InvalidDataException(L.T("外观设置包含未知选项。"));
        if (double.IsNaN(value.TintOpacity) || double.IsInfinity(value.TintOpacity) || value.TintOpacity is < 0 or > 1 ||
            double.IsNaN(value.LuminosityOpacity) || double.IsInfinity(value.LuminosityOpacity) || value.LuminosityOpacity is < 0 or > 1)
            throw new InvalidDataException(L.T("材质不透明度必须是 0 到 1 之间的有限数值。"));
        if (value.TintColor is not null && (value.TintColor.Length != 7 || value.TintColor[0] != '#' || !value.TintColor.Skip(1).All(Uri.IsHexDigit)))
            throw new InvalidDataException(L.T("材质颜色必须是 #RRGGBB，或使用随主题颜色。"));
        return value.TintColor is null ? value : value with { TintColor = value.TintColor.ToUpperInvariant() };
    }

    internal static void WriteNew(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data);
        stream.Flush(flushToDisk: true);
    }

    internal static void AtomicWrite(string path, byte[] data)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteNew(temporary, data);
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
