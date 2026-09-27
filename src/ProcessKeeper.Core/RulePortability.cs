namespace ProcessKeeper.Core;

/// <summary>Separates stable application identities from rules whose meaning depends on one computer.</summary>
public static class RulePortability
{
    private static readonly HashSet<string> SupportedKnownIdentities = new(StringComparer.OrdinalIgnoreCase)
    {
        "known:clash", "known:codex", "known:qq", "known:steam", "known:huorong", "known:uu",
        "known:translucenttb", "known:avd", "known:mydock"
    };

    private static readonly HashSet<string> ReservedPackageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    /// <summary>Does not probe installed software; an absent application remains a valid, inactive match.</summary>
    public static bool IsPortable(WhitelistRule rule)
    {
        if (rule is null || rule.Kind != RuleKind.Application || string.IsNullOrEmpty(rule.Value)) return false;
        if (SupportedKnownIdentities.Contains(rule.Value)) return true;
        const string packagePrefix = "package:";
        return rule.Value.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) &&
            IsValidPackageFamilyName(rule.Value[packagePrefix.Length..]);
    }

    /// <summary>Produces a separate read-only list containing only identities that can travel across computers.</summary>
    public static IReadOnlyList<WhitelistRule> ForSharing(IReadOnlyList<WhitelistRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return Array.AsReadOnly(rules.Where(IsPortable).ToArray());
    }

    /// <summary>
    /// Retains all imported rules for review, disabling machine-specific ones unless explicitly permitted.
    /// Call again with the original imported list when changing that choice; never enable a rule that was disabled in the source.
    /// </summary>
    public static IReadOnlyList<WhitelistRule> PrepareImport(IReadOnlyList<WhitelistRule> rules, bool enableLocalRules = false)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var result = new WhitelistRule[rules.Count];
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i] ?? throw new ArgumentException(L.T("导入的规则不能包含空项。"), nameof(rules));
            result[i] = !enableLocalRules && !IsPortable(rule) && rule.Enabled ? rule with { Enabled = false } : rule;
        }
        return Array.AsReadOnly(result);
    }

    private static bool IsValidPackageFamilyName(string family)
    {
        // Microsoft package identity grammar: Name (3..50 ASCII package characters), '_', PublisherId
        // (13 Crockford-base32 characters). This is the stable family, never a full package path or AUMID.
        // https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview
        if (family.Length is < 17 or > 64) return false;
        int separator = family.Length - 14;
        if (family[separator] != '_') return false;
        string name = family[..separator];
        if (name.Length is < 3 or > 50 || name.EndsWith('.') ||
            name.StartsWith("xn--", StringComparison.OrdinalIgnoreCase) ||
            name.Contains(".xn--", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (char character in name)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')) return false;
        int firstDot = name.IndexOf('.');
        string firstPart = firstDot < 0 ? name : name[..firstDot];
        if (ReservedPackageNames.Contains(firstPart)) return false;
        foreach (char character in family.AsSpan(separator + 1))
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')) return false;
            if (!"0123456789abcdefghjkmnpqrstvwxyz".Contains(char.ToLowerInvariant(character))) return false;
        }
        return true;
    }
}
