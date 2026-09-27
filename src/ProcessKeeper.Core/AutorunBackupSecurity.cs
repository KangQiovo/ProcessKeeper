using System.Security.AccessControl;
using System.Security.Principal;

namespace ProcessKeeper.Core;

/// <summary>Production recovery data must not be replaceable by the same user without elevation.</summary>
internal static class AutorunBackupSecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Computing the path does not create or modify any directory.
    internal static string GetDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrEmpty(sid))
            throw new IOException(L.T("无法确认自启动备份的系统目录或账户。"));
        return Path.Combine(root, "ProcessKeeper", "AutorunBackups", sid);
    }

    internal static void EnsureDirectory(string path)
    {
        RequireProductionPath(path);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        AssertSafeAncestors(commonData);
        CreateProtectedDirectory(Path.Combine(commonData, "ProcessKeeper"));
        CreateProtectedDirectory(Path.Combine(commonData, "ProcessKeeper", "AutorunBackups"));
        CreateProtectedDirectory(path);
    }

    internal static void ValidateDirectory(string path)
    {
        RequireProductionPath(path);
        AssertSafeAncestors(path);
        ValidateDescriptor(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), true);
    }

    internal static FileStream CreateFile(string path)
    {
        ValidateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        // CreateNew and the protected parent prevent replacing any existing object.
        var stream = new FileInfo(path).Create(FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.ReadPermissions, FileShare.None, 4096, FileOptions.WriteThrough, security);
        try { ValidateFile(stream); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal static void ValidateFile(string path)
    {
        ValidateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.T("自启动备份路径包含链接，已停止读取或写入。"));
        ValidateDescriptor(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), false);
    }

    internal static void ValidateFile(FileStream file) => ValidateDescriptor(file.GetAccessControl(), false);

    private static void RequireProductionPath(string path)
    {
        if (!Path.GetFullPath(path).TrimEnd('\\').Equals(Path.GetFullPath(GetDirectory()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("自启动备份目录不属于当前账户的受限系统目录。"));
    }

    private static void CreateProtectedDirectory(string path)
    {
        AssertSafeAncestors(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) directory.Create(NewDirectorySecurity());
        AssertSafeAncestors(path);
        ValidateDescriptor(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), true);
    }

    private static void AssertSafeAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("自启动备份路径包含链接，已停止读取或写入。"));
            AssertParentCannotReplaceChildren(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        }
    }

    internal static DirectorySecurity NewDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, SystemAccount })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    internal static void ValidateDescriptor(FileSystemSecurity security, bool isDirectory)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if ((!Administrators.Equals(owner) && !SystemAccount.Equals(owner)) || !security.AreAccessRulesProtected)
            throw new IOException(L.T("自启动备份所有者或权限不可信；原目录权限未被修改。"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                (!Administrators.Equals(rule.IdentityReference) && !SystemAccount.Equals(rule.IdentityReference)) ||
                rule.FileSystemRights != FileSystemRights.FullControl || rule.InheritanceFlags != inheritance ||
                rule.PropagationFlags != PropagationFlags.None || !seen.Add(rule.IdentityReference.Value))
                throw new IOException(L.T("自启动备份必须仅允许管理员和 SYSTEM 访问，未更改现有权限。"));
        }
        if (seen.Count != 2) throw new IOException(L.T("自启动备份必须仅允许管理员和 SYSTEM 访问，未更改现有权限。"));
    }

    internal static void AssertParentCannotReplaceChildren(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Administrators.Equals(owner) && !SystemAccount.Equals(owner) && !TrustedInstaller.Equals(owner))
            throw new IOException(L.T("自启动备份上级目录可被非管理员替换，已停止操作。"));
        var dangerous = FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if (Administrators.Equals(rule.IdentityReference) || SystemAccount.Equals(rule.IdentityReference) || TrustedInstaller.Equals(rule.IdentityReference)) continue;
            if ((rule.FileSystemRights & dangerous) != 0 || ((uint)rule.FileSystemRights & 0x10000000u) != 0)
                throw new IOException(L.T("自启动备份上级目录可被非管理员替换，已停止操作。"));
        }
    }
}
