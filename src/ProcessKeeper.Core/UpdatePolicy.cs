namespace ProcessKeeper.Core;

/// <summary>The shipped application's update authority. Settings can select a transport, never a repository.</summary>
public static class UpdatePolicy
{
    public const string Repository = "KangQiovo/ProcessKeeper";

    public static string NormalizeRepository(string? value)
    {
        var normalized = (value ?? "").Trim();
        const string prefix = "https://github.com/";
        // Match the raw input rather than Uri-normalized paths: dot segments,
        // percent-encoded separators, credentials and look-alike hosts are refused.
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(prefix.Length);
            if (normalized.EndsWith("/", StringComparison.Ordinal)) normalized = normalized.Substring(0, normalized.Length - 1);
        }
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(0, normalized.Length - 4);
        if (!normalized.Equals(Repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("更新仓库固定为 KangQiovo/ProcessKeeper，不能使用其他仓库。"));
        return Repository;
    }
}
