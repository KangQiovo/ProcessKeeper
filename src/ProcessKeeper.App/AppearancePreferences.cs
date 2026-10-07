using ProcessKeeper.Core;
using System.Text.Json;

namespace ProcessKeeper.App;

public enum BackdropMaterial { Mica, Acrylic }
public enum AppearanceTheme { System, Light, Dark }

public sealed record AppearancePreferences(
    bool BackdropEnabled = true,
    BackdropMaterial Material = BackdropMaterial.Mica,
    AppearanceTheme Theme = AppearanceTheme.System,
    bool CustomBackdropEnabled = false,
    double TintOpacity = 0.8,
    double LuminosityOpacity = 0.85,
    string? TintColor = null);

/// <summary>Appearance is independent of the whitelist; a damaged file only resets visual preferences.</summary>
internal sealed class AppearancePreferencesStore
{
    private const int MaximumFileBytes = 16 * 1024;

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
        byte[] data;
        try { data = SettingsJson.AppearanceBytes(preferences); }
        catch (InvalidDataException error) { throw new ArgumentException(error.Message, nameof(preferences), error); }
        SettingsJson.AtomicWrite(FilePath, data);
    }
}
