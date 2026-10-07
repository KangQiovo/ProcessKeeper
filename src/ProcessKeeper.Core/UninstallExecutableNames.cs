using System.Text;

namespace ProcessKeeper.Core;

/// <summary>Display hints only. Registration and reviewed file identity remain the only execution route.</summary>
internal static class UninstallExecutableNames
{
    internal static bool IsPossible(string value, string applicationName)
    {
        var stem = value.ToLowerInvariant();
        if (stem is "unins" or "uninst" or "uninstall" or "uninstaller" or "unwise" or "unwise32" or "unwise64" or "isuninst" or "isuninst32" or "isuninst64") return true;
        if (stem.StartsWith("unins", StringComparison.Ordinal) && stem.Length is > 5 and <= 11 && stem.Substring(5).All(c => c is >= '0' and <= '9')) return true;
        foreach (var marker in new[] { "uninstaller", "uninstall", "uninst", "remove" })
        {
            if (stem == marker + "32" || stem == marker + "64") return true;
            if (stem.StartsWith(marker, StringComparison.Ordinal) && stem.Length > marker.Length)
            {
                var suffix = value.Substring(marker.Length);
                if (suffix[0] is '-' or '_' or ' ' or '.' || char.IsUpper(suffix[0]) || applicationName.Length >= 2 && Key(suffix) == applicationName) return true;
            }
            if (stem.EndsWith("_" + marker, StringComparison.Ordinal) || stem.EndsWith("-" + marker, StringComparison.Ordinal) || stem.EndsWith(" " + marker, StringComparison.Ordinal)) return true;
            if (applicationName.Length >= 2 && stem.EndsWith(marker, StringComparison.Ordinal) && Key(value.Substring(0, value.Length - marker.Length)) == applicationName) return true;
        }
        return stem.Contains("卸载") || stem.Contains("卸載") || stem.Contains("解除安裝");
    }
    private static string Key(string value)
    {
        var result = new StringBuilder(); foreach (var c in value) if (char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c)); return result.ToString();
    }
}
