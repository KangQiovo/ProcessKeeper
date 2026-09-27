// Compatibility copy of modern settings/lifecycle contract; JSON formats remain shared.
using ProcessKeeper.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessKeeper.App;

public enum BackdropMaterial { Mica, Acrylic }
public enum AppearanceTheme { System, Light, Dark }

public sealed record AppearancePreferences(
    bool BackdropEnabled = true,
    BackdropMaterial Material = BackdropMaterial.Mica,
    AppearanceTheme Theme = AppearanceTheme.System);

/// <summary>Appearance is independent of the whitelist; a damaged file only resets visual preferences.</summary>
internal sealed class AppearancePreferencesStore
{
    private const int MaximumFileBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string FilePath { get; }

    public AppearancePreferencesStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        FilePath = Path.Combine(Path.GetFullPath(directory), "appearance.json");
    }

    public AppearancePreferences Load(out string? warning)
    {
        warning = null;
        try
        {
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException(L.T("外观配置过大。"));
            using var document = JsonDocument.Parse(stream);
            return SettingsJson.ReadAppearance(document.RootElement);
        }
        catch (FileNotFoundException) { return new AppearancePreferences(); }
        catch (DirectoryNotFoundException) { return new AppearancePreferences(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            warning = L.T("外观配置无法读取，已临时使用默认外观。原文件保留；更改选项或恢复默认可重新保存。");
            return new AppearancePreferences();
        }
    }

    public void Save(AppearancePreferences preferences)
    {
        if (preferences is null) throw new ArgumentNullException(nameof(preferences));
        if (!Enum.IsDefined(typeof(BackdropMaterial), preferences.Material) || !Enum.IsDefined(typeof(AppearanceTheme), preferences.Theme))
            throw new ArgumentException(L.T("外观设置包含未知选项。"), nameof(preferences));
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".appearance-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new
                {
                    Version = 1,
                    preferences.BackdropEnabled,
                    preferences.Material,
                    preferences.Theme
                }, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", ignoreMetadataErrors: false);
            else File.Move(temporary, FilePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
