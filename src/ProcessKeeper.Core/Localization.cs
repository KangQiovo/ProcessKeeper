using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ProcessKeeper.Core;

/// <summary>Translates application-owned text only; arguments are never used as lookup keys.</summary>
public static class L
{
    private static string _language = "en";
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(LoadEnglish);
    private static readonly ConcurrentDictionary<string, string> Traditional = new(StringComparer.Ordinal);
    public static string Language
    {
        get => _language;
        set => _language = IsSupported(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public static bool IsSupported(string? value) => value is "en" or "zh-Hans" or "zh-Hant";
    public static string LanguageName(string value) => value switch
    { "zh-Hans" => "简体中文", "zh-Hant" => "繁體中文", _ => "English" };
    public static string T(string source) => Language switch
    {
        "zh-Hans" => source,
        "zh-Hant" => Traditional.GetOrAdd(source, ToTraditional),
        _ => English.Value.TryGetValue(source, out var translated) ? translated : source
    };
    public static string F(FormattableString source) => string.Format(
        CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : Language == "zh-Hans" ? "zh-CN" : "zh-TW"),
        T(source.Format), source.GetArguments());
    public static IReadOnlyDictionary<string, string> EnglishCatalog => English.Value;

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(L).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Localization.") && n.EndsWith(".json")))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var key = entry.GetProperty("source").GetString()!;
                var value = entry.GetProperty("en").GetString()!;
                if (result.TryGetValue(key, out var previous) && previous != value)
                    throw new InvalidDataException("Conflicting translation: " + key);
                result[key] = value;
            }
        }
        return result;
    }

    private static string ToTraditional(string source)
    {
        if (source.Length == 0) return source;
        const uint traditionalChinese = 0x04000000;
        var length = LCMapStringEx("zh-TW", traditionalChinese, source, -1, null, 0, 0, 0, 0);
        if (length == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(length);
        if (LCMapStringEx("zh-TW", traditionalChinese, source, -1, buffer, length, 0, 0, 0) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        StringBuilder? destination, int destinationLength, nint version, nint reserved, nint sortHandle);
}
