using System.Text.Json;

namespace ProcessKeeper.Core;

public sealed class UpdatePreferencesStore
{
    public string FilePath { get; }
    public UpdatePreferencesStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        FilePath = Path.Combine(Path.GetFullPath(directory), "update.json");
    }
    public UpdatePreferences Load()
    {
        try
        {
            using var file = File.OpenRead(FilePath);
            if (file.Length > 8192) throw new InvalidDataException(L.T("更新设置不能超过 8 KiB。"));
            using var json = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 8 }); return ReadJson(json.RootElement);
        }
        catch (FileNotFoundException) { return new UpdatePreferences(); }
        catch (DirectoryNotFoundException) { return new UpdatePreferences(); }
    }
    public void Save(UpdatePreferences value)
    {
        var bytes = Serialize(value); var directory = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static UpdatePreferences Validate(UpdatePreferences value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        var repository = NormalizeRepository(value.Repository);
        if (!Enum.IsDefined(typeof(UpdateSourceMode), value.SourceMode) ||
            value.ThirdPartySourceId != "auto" && !UpdateSources.All.Any(source => !source.IsOfficial && source.Id == value.ThirdPartySourceId))
            throw new InvalidDataException(L.T("更新下载源设置无效。"));
        return value with { Repository = repository };
    }
    public static string NormalizeRepository(string? value) => UpdatePolicy.NormalizeRepository(value);
    public static byte[] Serialize(UpdatePreferences value)
    {
        value = Validate(value);
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, value.Repository, value.CheckOnStartup, SourceMode = value.SourceMode.ToString(), value.ThirdPartySourceId, value.AutoDesktopShortcut }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static UpdatePreferences ReadJson(string json)
    {
        if (json.Length > 8192) throw new InvalidDataException(L.T("更新设置不能超过 8 KiB。"));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); return ReadJson(document.RootElement);
    }
    public static UpdatePreferences ReadJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.T("更新设置格式无效。"));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("Version" or "Repository" or "CheckOnStartup" or "SourceMode" or "ThirdPartySourceId" or "AutoDesktopShortcut"))
                throw new InvalidDataException(L.T("更新设置包含重复或未知字段。"));
        // This is the first update-settings schema. A present but incomplete
        // object is corruption, not an old version whose effects may be defaulted.
        if (names.Count != 6) throw new InvalidDataException(L.T("更新设置格式无效。"));
        var defaults = new UpdatePreferences();
        if (element.TryGetProperty("Version", out var version) && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)) throw new InvalidDataException(L.T("更新设置版本不受支持。"));
        string Text(string name, string fallback) => element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : !names.Contains(name) ? fallback : throw new InvalidDataException(L.T("更新设置格式无效。"));
        bool Flag(string name, bool fallback) => element.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : !names.Contains(name) ? fallback : throw new InvalidDataException(L.T("更新设置格式无效。"));
        var sourceMode = Text("SourceMode", "Auto");
        if (!Enum.TryParse(sourceMode, false, out UpdateSourceMode mode) || !Enum.IsDefined(typeof(UpdateSourceMode), mode) || sourceMode != mode.ToString()) throw new InvalidDataException(L.T("更新下载源设置无效。"));
        return Validate(new UpdatePreferences(Text("Repository", defaults.Repository), Flag("CheckOnStartup", true), mode, Text("ThirdPartySourceId", "auto"), Flag("AutoDesktopShortcut", true)));
    }
}
