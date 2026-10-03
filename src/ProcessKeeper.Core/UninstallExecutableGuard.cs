using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

/// <summary>Pins a local executable and its ancestors; trust is checked against this same file handle.</summary>
internal sealed class UninstallExecutableGuard : IDisposable
{
    private readonly List<SafeFileHandle> _directories = new();
    private FileStream? _file;
    private readonly string _path;
    internal UninstallExecutableGuard(string path, string expected)
    {
        _path = path;
        try
        {
            if (!UninstallPolicy.IsLocalExecutable(path)) throw new InvalidDataException();
            var ancestors = new Stack<string>();
            for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) ancestors.Push(dir!);
            ancestors.Push(Path.GetPathRoot(path)!);
            foreach (var dir in ancestors.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var handle = CreateFile(dir, 0x80, 3 /* no DELETE sharing */, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                _directories.Add(handle);
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException();
            }
            var file = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (file.IsInvalid) { file.Dispose(); throw new IOException(); }
            _file = new FileStream(file, FileAccess.Read);
            if (expected.Length == 0 || Identity(file) != expected) throw new IOException();
        }
        catch { Dispose(); throw new IOException(L.T("卸载程序文件无法锁定或身份已变化，未执行。")); }
    }
    internal static string ReadIdentity(string path)
    {
        try
        {
            var value = new MicrosoftPublisherProbe().ReadIdentity(path);
            return value is null ? "" : JsonSerializer.Serialize(value);
        }
        catch { return ""; }
    }
    private static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0) return "";
        return JsonSerializer.Serialize(new PublisherFileIdentity(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
            ((long)info.SizeHigh << 32) | info.SizeLow, ((long)info.WrittenHigh << 32) | info.WrittenLow, ((long)info.CreatedHigh << 32) | info.CreatedLow));
    }
    internal string Hash(CancellationToken token)
    {
        if (_file is null || _file.Length > 512L * 1024 * 1024) throw new IOException(L.T("卸载器过大或无法读取，未执行。"));
        _file.Position = 0; using var hash = SHA256.Create(); var buffer = new byte[65536]; int read;
        while ((read = _file.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, read, null, 0); }
        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return Convert.ToHexString(hash.Hash!);
    }
    internal string DescribeSigner(bool msi, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (msi && !string.Equals(_path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = _path, FileHandle = _file!.SafeFileHandle.DangerousGetHandle() };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = pointer, StateAction = 1,
            ProviderFlags = 0x1000 | 0x10 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (msi && SfcIsFileProtected(IntPtr.Zero, _path)) return L.T("Windows 保护的系统卸载组件");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new InvalidDataException();
            token.ThrowIfCancellationRequested();
            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            var certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) throw new InvalidDataException();
            var header = Marshal.PtrToStructure<ProviderCertificate>(certificate);
            if (header.Size < Marshal.SizeOf<ProviderCertificate>() || header.Certificate == IntPtr.Zero) throw new InvalidDataException();
#pragma warning disable SYSLIB0057
            using var x509 = new X509Certificate2(header.Certificate);
#pragma warning restore SYSLIB0057
            var actualPublisher = x509.GetNameInfo(X509NameType.SimpleName, false);
            if (actualPublisher.Length == 0 || actualPublisher.Any(char.IsControl) || msi &&
                !actualPublisher.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            return L.T("已验证的数字签名") + " | " + actualPublisher;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            if (msi) throw new InvalidOperationException(L.T("Windows 系统卸载组件无法验证，未执行。"));
            return L.T("未签名或签名无法验证。仅在确认信任此注册卸载器时继续。");
        }
        finally
        {
            data.StateAction = 2; _ = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }
    public void Dispose() { _file?.Dispose(); _file = null; for (int i = _directories.Count - 1; i >= 0; i--) _directories[i].Dispose(); _directories.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public IntPtr PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData, UrlReference; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public IntPtr Certificate; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("sfc.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SfcIsFileProtected(IntPtr rpc, string path);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)] private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}
