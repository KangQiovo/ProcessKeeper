using System.Globalization;

namespace ProcessKeeper.Core;

public static class LanguageResolver
{
    public static bool IsPreference(string? value) => value == "auto" || L.IsSupported(value);
    public static string ResolveCurrent(string preference) => Resolve(preference,
        CultureInfo.CurrentUICulture.Name, RegionInfo.CurrentRegion.TwoLetterISORegionName, TimeZoneInfo.Local.Id);

    public static string Resolve(string preference, string displayLanguage, string region, string timeZone)
    {
        if (L.IsSupported(preference)) return preference;
        if (preference != "auto") throw new ArgumentOutOfRangeException(nameof(preference));
        var language = displayLanguage.Replace('_', '-').ToLowerInvariant();
        // An explicit supported display language always wins over region and time zone.
        if (language == "en" || language.StartsWith("en-")) return "en";
        if (language == "zh" || language.StartsWith("zh-"))
        {
            if (language.Contains("-hant") || language is "zh-tw" or "zh-hk" or "zh-mo") return "zh-Hant";
            if (language.Contains("-hans") || language is "zh-cn" or "zh-sg" or "zh-my") return "zh-Hans";
            return region.ToUpperInvariant() is "TW" or "HK" or "MO" || IsTraditionalZone(timeZone) ? "zh-Hant" : "zh-Hans";
        }
        // A different, explicit display language uses the international UI. Region/time zone
        // only fill in missing OS language data; they never override the user's OS language.
        if (!string.IsNullOrWhiteSpace(language) && language != "iv") return "en";
        if (region.ToUpperInvariant() is "TW" or "HK" or "MO" || IsTraditionalZone(timeZone)) return "zh-Hant";
        if (region.ToUpperInvariant() is "CN" or "SG" || timeZone is "China Standard Time" or "Asia/Shanghai" or "Asia/Singapore") return "zh-Hans";
        return "en";
    }
    private static bool IsTraditionalZone(string zone) => zone is "Taipei Standard Time" or "Asia/Taipei" or "Asia/Hong_Kong" or "Asia/Macau";
}
