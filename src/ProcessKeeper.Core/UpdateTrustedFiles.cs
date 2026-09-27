using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

internal static class UpdateTrustedFiles
{
    private static readonly SecurityIdentifier Admin = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    internal static string CacheRoot
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || identity.User is null)
                throw new UnauthorizedAccessException(L.T("此功能需要已验证的管理员启动器。"));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProcessKeeper", "Universal", identity.User.Value);
        }
    }
    internal static bool ValidId(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool ValidHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    internal static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    internal static void ValidatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(CacheRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Untrusted update path.");
        AutorunPathSafety.RejectReparseAncestors(full);
        var cursor = new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (cursor is not null && cursor.FullName.StartsWith(CacheRoot, StringComparison.OrdinalIgnoreCase))
        {
            ValidateAcl(cursor.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), true);
            cursor = cursor.Parent;
        }
    }
    internal static void ValidateAcl(FileSystemSecurity acl, bool directory, bool requireProtected = true)
    {
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!Admin.Equals(owner) && !SystemAccount.Equals(owner)) throw new IOException("Untrusted update file owner.");
        if (requireProtected && !acl.AreAccessRulesProtected) throw new IOException("Update permissions must be protected.");
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length != 2 || !rules.Any(rule => Admin.Equals(rule.IdentityReference)) || !rules.Any(rule => SystemAccount.Equals(rule.IdentityReference)))
            throw new IOException("Untrusted update permissions.");
        foreach (var rule in rules)
            if (rule.AccessControlType != AccessControlType.Allow || requireProtected && rule.IsInherited || rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.PropagationFlags != PropagationFlags.None || rule.InheritanceFlags != (directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None))
                throw new IOException("Untrusted update permissions.");
    }
    internal static void CreateDirectory(string path)
    {
        ValidatePath(path);
        var acl = new DirectorySecurity(); acl.SetOwner(Admin); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Admin, SystemAccount }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
        ValidateAcl(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), true);
    }
    internal static FileStream Create(string path)
    {
        ValidatePath(path);
        var acl = new FileSecurity(); acl.SetOwner(Admin); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Admin, SystemAccount }) acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 65536, FileOptions.WriteThrough, acl);
    }
    internal static FileStream OpenRead(string path, bool requireProtected = true)
    {
        ValidatePath(path);
        var handle = CreateFile(path, 0x80000000 | 0x20000, 1, nint.Zero, 3, 0x00200000, nint.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open a trusted update file."); }
        try
        {
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0) throw new IOException("Linked update file refused.");
            var final = new System.Text.StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, final, 32768, 0);
            if (length == 0 || length >= 32768 || !SamePath(final.ToString().Replace(@"\\?\", ""), path))
                throw new IOException("Update file path changed.");
            var stream = new FileStream(handle, FileAccess.Read);
            try { ValidateAcl(stream.GetAccessControl(), false, requireProtected); return stream; } catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }
    internal static void RequireHash(Stream stream, string hash)
    {
        if (!ValidHash(hash)) throw new IOException("Invalid update SHA-256.");
        using var algorithm = SHA256.Create(); stream.Position = 0;
        var actual = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", ""); stream.Position = 0;
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException(L.T("更新文件未通过 SHA-256 校验。"));
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, System.Text.StringBuilder path, uint size, uint flags);
}
