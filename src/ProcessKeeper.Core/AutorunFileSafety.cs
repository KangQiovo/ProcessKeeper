using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

// Every mutable path component stays open without FILE_SHARE_DELETE until the operation
// finishes. A final no-follow handle supplies both the fingerprint and the mutation.
internal static class AutorunFileSafety
{
    private sealed record FolderPayload(string Content, long Created, long Written, int Attributes, string Security);
    private const uint ReadControl = 0x20000, ReadAttributes = 0x80, Delete = 0x10000;
    private const uint NoFollow = 0x00200000, DirectoryHandle = 0x02000000;
    private const FileAttributes RestorableAttributes = FileAttributes.Archive | FileAttributes.Hidden |
        FileAttributes.System | FileAttributes.Normal | FileAttributes.NotContentIndexed | FileAttributes.Temporary;

    internal static AutorunState Read(string path)
    {
        using var parents = LockParents(path);
        using var handle = OpenLeaf(path, 0x80000000 | ReadControl | ReadAttributes, missingAllowed: true);
        if (handle is null) return new(false, false, "");
        using var stream = new FileStream(handle, FileAccess.Read);
        return ReadLocked(stream, path);
    }

    internal static void Apply(string path, AutorunState expected, AutorunState desired)
    {
        using var parents = LockParents(path);
        if (!desired.Exists)
        {
            using var handle = OpenLeaf(path, 0x80000000 | ReadControl | ReadAttributes | Delete, missingAllowed: false)!;
            using var stream = new FileStream(handle, FileAccess.Read);
            if (ReadLocked(stream, path).Fingerprint != expected.Fingerprint) Changed();
            var disposition = new FileDispositionInfo { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>())) Error();
            return;
        }

        // Creation is allowed only for the exact absent state confirmed by the caller.
        if (expected.Exists || expected.Fingerprint != new AutorunState(false, false, "").Fingerprint) Changed();
        using (var existing = OpenLeaf(path, ReadAttributes, missingAllowed: true))
            if (existing is not null) Changed();
        var saved = JsonSerializer.Deserialize<FolderPayload>(desired.Payload) ?? throw new InvalidDataException();
        var content = Convert.FromBase64String(saved.Content);
        if (content.Length > AutorunWindowsBackend.MaximumFileBytes) throw new InvalidDataException();
        ValidateAttributes((FileAttributes)saved.Attributes);
        var acl = new FileSecurity();
        acl.SetSecurityDescriptorBinaryForm(Convert.FromBase64String(saved.Security));
        ValidateOwner(acl);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // A random, newly-created file is held exclusively throughout restoration and rename.
        // Original permissions are installed at creation, without a user-writable gap.
        using var output = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.WriteThrough, acl);
        var renamed = false;
        try
        {
            output.Write(content); output.Flush(true);
            var restoreAccess = new FileSecurity();
            restoreAccess.SetSecurityDescriptorBinaryForm(Convert.FromBase64String(saved.Security), AccessControlSections.Access);
            output.SetAccessControl(restoreAccess);
            var basic = new FileBasicInfo
            {
                CreationTime = new DateTime(saved.Created, DateTimeKind.Utc).ToFileTimeUtc(),
                LastWriteTime = new DateTime(saved.Written, DateTimeKind.Utc).ToFileTimeUtc(),
                Attributes = unchecked((uint)saved.Attributes)
            };
            if (!SetFileInformationByHandle(output.SafeFileHandle, 0, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>())) Error();
            var restored = ReadLocked(output, temporary);
            if (restored.Fingerprint != desired.Fingerprint)
                throw new IOException(L.T("启动文件的原始权限或属性无法完整恢复。"));
            RenameWithoutReplacing(output.SafeFileHandle, path);
            if (ReadLocked(output, path).Fingerprint != desired.Fingerprint)
                throw new IOException(L.T("启动文件的原始权限或属性无法完整恢复。"));
            renamed = true;
        }
        finally
        {
            if (!renamed)
            {
                // Delete this exact temporary object; never follow a later path replacement.
                var disposition = new FileDispositionInfo { DeleteFile = true };
                _ = SetFileInformationByHandle(output.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>());
            }
        }
    }

    private static AutorunState ReadLocked(FileStream stream, string path)
    {
        ValidateHandlePath(stream.SafeFileHandle, path, directory: false);
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, 0, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>())) Error();
        ValidateAttributes((FileAttributes)basic.Attributes);
        var security = stream.GetAccessControl();
        ValidateOwner(security);
        if (stream.Length > AutorunWindowsBackend.MaximumFileBytes) throw new IOException(L.T("启动文件超过可恢复管理的大小上限，只能查看。"));
        var bytes = new byte[checked((int)stream.Length)];
        stream.Position = 0; stream.ReadExactly(bytes);
        return new(true, true, JsonSerializer.Serialize(new FolderPayload(Convert.ToBase64String(bytes),
            DateTime.FromFileTimeUtc(basic.CreationTime).Ticks, DateTime.FromFileTimeUtc(basic.LastWriteTime).Ticks,
            (int)basic.Attributes, Convert.ToBase64String(security.GetSecurityDescriptorBinaryForm()))));
    }

    private static void ValidateOwner(FileSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var identity = WindowsIdentity.GetCurrent();
        if (owner is null || ((identity.User is null || !owner.Equals(identity.User)) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
            throw new IOException(L.T("启动文件的所有者不支持安全恢复，只能查看。"));
    }
    private static void ValidateAttributes(FileAttributes attributes)
    {
        if ((attributes & ~RestorableAttributes) != 0)
            throw new IOException(L.T("启动文件含有无法完整恢复的特殊属性，只能查看。"));
    }

    private static SafeFileHandle? OpenLeaf(string path, uint access, bool missingAllowed)
    {
        var handle = CreateFile(path, access, 1, 0, 3, NoFollow, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (missingAllowed && error is 2 or 3) return null;
        throw new Win32Exception(error);
    }

    private static ParentLocks LockParents(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        var root = Path.GetPathRoot(path) ?? throw new InvalidDataException();
        var parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException();
        var pieces = parent[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var locked = new ParentLocks();
        try
        {
            var current = root;
            for (var i = -1; i < pieces.Length; i++)
            {
                if (i >= 0) current = Path.Combine(current, pieces[i]);
                // A metadata-only handle does not participate in Windows share-access checks.
                // FILE_LIST_DIRECTORY (GENERIC_READ) is needed to actually deny rename/delete.
                var handle = CreateFile(current, 0x80000000, 1 | 2, 0, 3, DirectoryHandle | NoFollow, 0);
                if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                locked.Handles.Add(handle);
                ValidateHandlePath(handle, current, directory: true);
            }
            return locked;
        }
        catch { locked.Dispose(); throw; }
    }

    private static void ValidateHandlePath(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 0, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>())) Error();
        var attributes = (FileAttributes)basic.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException(L.T("启动文件路径包含链接或已被替换。"));
        var name = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
        if (count == 0 || count >= name.Capacity) Error();
        var resolved = name.ToString();
        if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(resolved.TrimEnd('\\'), Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException(L.T("启动文件路径包含链接或已被替换。"));
    }

    private static void RenameWithoutReplacing(SafeFileHandle handle, string destination)
    {
        var text = Encoding.Unicode.GetBytes(destination);
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        // FileName is a null-terminated WCHAR string even though FileNameLength excludes
        // the terminator. Without these two bytes the native call can append heap garbage.
        var memory = Marshal.AllocHGlobal(nameOffset + text.Length + 2);
        try
        {
            for (var i = 0; i < nameOffset; i++) Marshal.WriteByte(memory, i, 0);
            Marshal.WriteInt32(memory, lengthOffset, text.Length);
            Marshal.Copy(text, 0, memory + nameOffset, text.Length);
            Marshal.WriteInt16(memory, nameOffset + text.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, memory, (uint)(nameOffset + text.Length + 2))) Error();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private sealed class ParentLocks : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        public void Dispose() { for (var i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }
    private static void Changed() => throw new IOException(L.T("入口已发生变化，请刷新列表后重新确认。"));
    private static void Error() => throw new Win32Exception(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct FileBasicInfo
    { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FileDispositionInfo { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileBasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref FileBasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref FileDispositionInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, nint info, uint size);
}
