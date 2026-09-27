using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcessKeeper.Launcher;

// This directory is exclusively for the non-elevated access page. An elevated
// launcher always chooses CacheSecurity's separate administrator-owned root.
internal static class UserCacheSecurity
{
    public static string GetVersionRoot(string payloadHash)
    {
        if (payloadHash.Length != 64 || !payloadHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("The application version identifier is invalid.");
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) throw new IOException("The current user's local application data directory is unavailable.");
        CacheSecurity.AssertNoReparseAncestors(localData);
        var product = Path.Combine(localData, "ProcessKeeper");
        // The existing product directory also contains personal settings. Never
        // replace its ACL or owner; this mode owns only RuntimePreview below it.
        CacheStore.EnsurePlainDirectory(product);
        CacheSecurity.AssertNoReparseAncestors(product);
        var runtime = Path.Combine(product, "RuntimePreview");
        CreateDirectory(runtime);
        var version = Path.Combine(runtime, payloadHash);
        CreateDirectory(version);
        return version;
    }

    internal static void CreateDirectory(string path)
    {
        CacheSecurity.AssertNoReparseAncestors(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("The current user could not be verified.");
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) directory.Create(NewDirectorySecurity(user));
        CacheSecurity.AssertNoReparseAncestors(path);
        ValidateDescriptor(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), user);
    }

    internal static DirectorySecurity NewDirectorySecurity(SecurityIdentifier user)
    {
        var result = new DirectorySecurity();
        result.SetOwner(user);
        result.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            result.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return result;
    }

    internal static void ValidateDescriptor(DirectorySecurity security, SecurityIdentifier user)
    {
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw new IOException("The access-page cache has unexpected ownership. Startup stopped.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                (!user.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference)) ||
                rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) ||
                rule.PropagationFlags != PropagationFlags.None || !seen.Add(rule.IdentityReference.Value))
                throw new IOException("The access-page cache has unexpected permissions.");
        }
        if (seen.Count != 2) throw new IOException("The access-page cache is missing required permissions.");
    }
}
