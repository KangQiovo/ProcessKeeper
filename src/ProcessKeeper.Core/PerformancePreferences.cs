using System.Text.Json;

namespace ProcessKeeper.Core;

public enum PerformanceDisplay { InApp, Overlay }
public sealed record PerformancePreferences(bool Enabled = false, PerformanceDisplay Display = PerformanceDisplay.InApp,
    bool Detailed = true, bool UseAcrylic = true, bool Horizontal = true, bool Locked = false,
    double X = 16, double Y = 16, double Width = 232, double Height = 90, bool CompactLine = false);

public sealed class PerformancePreferencesStore
{
    public string FilePath { get; }
    public PerformancePreferencesStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        FilePath = Path.Combine(Path.GetFullPath(directory), "performance.json");
    }
    public PerformancePreferences Load()
    {
        try
        {
            using var file = File.OpenRead(FilePath);
            if (file.Length > 8192) throw new InvalidDataException(L.T("性能显示设置无效。"));
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 4 });
            return ReadJson(document.RootElement);
        }
        catch (FileNotFoundException) { return new PerformancePreferences(); }
        catch (DirectoryNotFoundException) { return new PerformancePreferences(); }
    }
    public void Save(PerformancePreferences preferences)
    {
        var bytes = SerializeBytes(preferences); var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, ".performance-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak"); else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static PerformancePreferences Validate(PerformancePreferences value)
    {
        bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        if (value is null || !Enum.IsDefined(typeof(PerformanceDisplay), value.Display) ||
            !Finite(value.X) || !Finite(value.Y) || Math.Abs(value.X) > 100000 || Math.Abs(value.Y) > 100000 ||
            !Finite(value.Width) || !Finite(value.Height) || value.Width < 180 || value.Width > 1200 || value.Height < (value.CompactLine ? 32 : 90) || value.Height > 800)
            throw new InvalidDataException(L.T("性能显示设置无效。"));
        return value;
    }
    public static byte[] SerializeBytes(PerformancePreferences value)
    {
        value = Validate(value);
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, value.Enabled, Display = value.Display.ToString(), value.Detailed,
            value.UseAcrylic, value.Horizontal, value.Locked, value.X, value.Y, value.Width, value.Height, value.CompactLine }, new JsonSerializerOptions { WriteIndented = true });
    }
    public static PerformancePreferences ReadJson(JsonElement value)
    {
        var hasCompact = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("CompactLine", out _);
        var fields = new[] { "Version", "Enabled", "Display", "Detailed", "UseAcrylic", "Horizontal", "Locked", "X", "Y", "Width", "Height" };
        RuleFileCodec.RequireFields(value, hasCompact ? fields.Concat(new[] { "CompactLine" }).ToArray() : fields);
        bool Flag(string key) => value.GetProperty(key).ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetProperty(key).GetBoolean() : throw new InvalidDataException(L.T("性能显示设置无效。"));
        double Number(string key) => value.GetProperty(key).ValueKind == JsonValueKind.Number && value.GetProperty(key).TryGetDouble(out var number)
            ? number : throw new InvalidDataException(L.T("性能显示设置无效。"));
        if (Number("Version") != 1 || value.GetProperty("Display").ValueKind != JsonValueKind.String ||
            !Enum.TryParse<PerformanceDisplay>(value.GetProperty("Display").GetString(), out var mode) || value.GetProperty("Display").GetString() != mode.ToString())
            throw new InvalidDataException(L.T("性能显示设置无效。"));
        return Validate(new PerformancePreferences(Flag("Enabled"), mode, Flag("Detailed"), Flag("UseAcrylic"), Flag("Horizontal"), Flag("Locked"), Number("X"), Number("Y"), Number("Width"), Number("Height"), hasCompact && Flag("CompactLine")));
    }
}
