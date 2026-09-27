using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcessKeeper.Launcher;

internal static class CacheSecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static string GetVersionRoot(string payloadHash)
    {
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonData)) throw new IOException("The system application data directory is unavailable.");
        AssertNoReparseAncestors(commonData);
        // ProgramData may grant ordinary users the right to CREATE a child folder;
        // it must not grant them the right to replace an administrator-owned child.
        var ancestor = new DirectoryInfo(commonData);
        while (ancestor is not null)
        {
            AssertParentCannotReplaceChildren(ancestor.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            ancestor = ancestor.Parent;
        }
        var product = Path.Combine(commonData, "ProcessKeeper");
        var runtime = Path.Combine(product, "Runtime");
        var version = Path.Combine(runtime, payloadHash);
        CreateDirectory(product);
        CreateDirectory(runtime);
        CreateDirectory(version);
        return version;
    }

    public static void CreateDirectory(string path)
    {
        AssertNoReparseAncestors(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            // Supply the owner and DACL at creation, rather than creating a publicly
            // writable directory and trying to tighten it after a race window.
            directory.Create(NewDirectorySecurity());
        }
        ValidateDirectory(path); // Also detects an attacker winning the create race.
    }

    public static FileStream CreateFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        ValidateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A cache file is a link. Startup stopped.");
            ValidateDescriptor(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), isDirectory: false);
        }
        var rights = (access == FileAccess.ReadWrite ? FileSystemRights.Read | FileSystemRights.Write : FileSystemRights.Write)
            | FileSystemRights.ReadPermissions;
        var file = new FileInfo(path).Create(mode, rights, share, 81920, FileOptions.None, NewFileSecurity());
        try
        {
            ValidateDescriptor(file.GetAccessControl(), isDirectory: false);
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    public static void ValidateDirectory(string path)
    {
        AssertNoReparseAncestors(path);
        CacheStore.EnsurePlainDirectory(path, create: false);
        ValidateDescriptor(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), isDirectory: true);
    }

    public static void ValidateFile(FileStream file) => ValidateDescriptor(file.GetAccessControl(), isDirectory: false);

    public static void AssertNoReparseAncestors(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A cache ancestor is missing or contains a symbolic link or junction: " + current.FullName);
            current = current.Parent;
        }
    }

    internal static DirectorySecurity NewDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity NewFileSecurity()
    {
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static void ValidateDescriptor(FileSystemSecurity security, bool isDirectory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if ((!Administrators.Equals(owner) && !SystemAccount.Equals(owner)) || !security.AreAccessRulesProtected)
            throw new IOException("The cache owner or permissions are untrusted. Ask an administrator to inspect the directory. Its ownership and permissions were not changed.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                (!Administrators.Equals(rule.IdentityReference) && !SystemAccount.Equals(rule.IdentityReference)) ||
                rule.FileSystemRights != FileSystemRights.FullControl || rule.InheritanceFlags != inheritance ||
                rule.PropagationFlags != PropagationFlags.None || !seen.Add(rule.IdentityReference.Value))
                throw new IOException("The cache has unexpected permissions and cannot be used. Its ownership was not changed.");
        }
        if (seen.Count != 2) throw new IOException("The cache must grant exclusive access to Administrators and SYSTEM.");
    }

    internal static void AssertParentCannotReplaceChildren(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Administrators.Equals(owner) && !SystemAccount.Equals(owner) && !TrustedInstaller.Equals(owner))
            throw new IOException("A system data directory ancestor has an untrusted owner. Startup stopped.");
        var dangerous = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (Administrators.Equals(rule.IdentityReference) || SystemAccount.Equals(rule.IdentityReference) || TrustedInstaller.Equals(rule.IdentityReference)) continue;
            if ((rule.FileSystemRights & dangerous) != 0 || ((uint)rule.FileSystemRights & 0x10000000u) != 0)
                throw new IOException("Other accounts can replace the system cache root. Startup stopped.");
        }
    }
}
