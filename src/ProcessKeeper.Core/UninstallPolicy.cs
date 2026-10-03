namespace ProcessKeeper.Core;

/// <summary>Parses only a native executable route. Never passes registered text through a command shell.</summary>
public static class UninstallPolicy
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe", "powershell.exe", "pwsh.exe", "rundll32.exe", "regsvr32.exe", "wscript.exe", "cscript.exe", "mshta.exe", "explorer.exe", "control.exe", "reg.exe", "schtasks.exe", "sc.exe", "wmic.exe", "msiexec.exe", "installutil.exe", "msbuild.exe", "dotnet.exe", "python.exe", "node.exe", "bash.exe" };
    public static UninstallCommand? Parse(string raw, bool msi, string keyName, string systemDirectory)
    {
        if (msi)
        {
            if (!Guid.TryParseExact(keyName, "B", out var product)) return null;
            return new(Path.Combine(systemDirectory, "msiexec.exe"), "/x " + product.ToString("B").ToUpperInvariant() + " /norestart", true, product.ToString("B").ToUpperInvariant());
        }
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 32767 || raw.Any(char.IsControl)) return null;
        var value = Environment.ExpandEnvironmentVariables(raw.Trim());
        string path, arguments;
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1); if (end < 2) return null;
            path = value.Substring(1, end - 1); arguments = value.Substring(end + 1).Trim();
            if (end + 1 < value.Length && !char.IsWhiteSpace(value[end + 1])) return null;
        }
        else
        {
            // An unquoted path containing spaces is ambiguous on Windows. Do not guess which executable runs.
            int space = value.IndexOf(' '); path = space < 0 ? value : value.Substring(0, space); arguments = space < 0 ? "" : value.Substring(space + 1).Trim();
        }
        if (!IsLocalExecutable(path) || Hosts.Contains(Path.GetFileName(path)) || arguments.IndexOfAny(new[] { '\r', '\n', '|', '&', '<', '>' }) >= 0) return null;
        // Vendors also register setup.exe, maintenance tools and product-specific names.
        // The registration, reviewed hash and explicit confirmation establish this route;
        // an executable's filename is not evidence of whether it can uninstall.
        return new(Path.GetFullPath(path), arguments, false);
    }
    public static bool IsLocalExecutable(string path)
    {
        try { return path.Length >= 7 && path.Length < 32760 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
            path.IndexOf(':', 2) < 0 && path.IndexOf('"') < 0 && !path.Any(char.IsControl) &&
            Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !path.Split('\\').Any(part => part is "." or "..") && Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    public static bool IsRecommended(string name) => UninstallRecommendations.Match(name) is not null;
    public static bool Same(UninstallEntry expected, UninstallEntry? current) => current is not null &&
        expected.Id.Length > 0 && expected.Fingerprint.Length > 0 && expected.Id == current.Id && expected.Locator == current.Locator &&
        expected.Fingerprint == current.Fingerprint && expected.Command == current.Command && expected.QuietCommand == current.QuietCommand &&
        expected.ExecutableIdentity == current.ExecutableIdentity && expected.QuietExecutableIdentity == current.QuietExecutableIdentity;
}
