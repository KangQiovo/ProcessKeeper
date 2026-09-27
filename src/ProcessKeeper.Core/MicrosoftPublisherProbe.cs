using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

public sealed record PublisherFileIdentity(uint Volume, ulong FileId, long Length, long WrittenUtcTicks, long CreatedUtcTicks);
public interface IMicrosoftPublisherProbe
{
    PublisherFileIdentity? ReadIdentity(string path);
    bool IsMicrosoft(string path, CancellationToken token);
}

/// <summary>Uses the actual signer selected by successful Windows trust verification. Display attribution only.</summary>
public sealed class MicrosoftPublisherProbe : IMicrosoftPublisherProbe
{
    public PublisherFileIdentity? ReadIdentity(string path)
    {
        try
        {
            if (DisplayPath.Normalize(path).Length == 0) return null;
            AutorunPathSafety.RejectReparseAncestors(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Identity(file.SafeFileHandle);
        }
        catch { return null; }
    }
    public bool IsMicrosoft(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (DisplayPath.Normalize(path).Length == 0) return false;
            AutorunPathSafety.RejectReparseAncestors(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Identity(stream.SafeFileHandle) is null) return false;
            var windows = DisplayPath.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (windows.Length > 0 && DisplayPath.Under(path, windows) && SfcIsFileProtected(nint.Zero, path)) return true;
            var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path, FileHandle = stream.SafeFileHandle.DangerousGetHandle() };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = pointer, StateAction = 1,
                ProviderFlags = 0x1000 | 0x10 }; // Cache-only verification; never open a network connection for a display filter.
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            try
            {
                if (WinVerifyTrust(new nint(-1), ref action, ref data) != 0) return false;
                token.ThrowIfCancellationRequested();
                var provider = WTHelperProvDataFromStateData(data.StateData); if (provider == nint.Zero) return false;
                var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0); if (signer == nint.Zero) return false;
                var certificate = WTHelperGetProvCertFromChain(signer, 0); if (certificate == nint.Zero) return false;
                var header = Marshal.PtrToStructure<ProviderCertificate>(certificate);
                if (header.Size < Marshal.SizeOf<ProviderCertificate>() || header.Certificate == nint.Zero) return false;
#pragma warning disable SYSLIB0057
                using var x509 = new X509Certificate2(header.Certificate);
#pragma warning restore SYSLIB0057
                return HasMicrosoftOrganization(x509.SubjectName.RawData);
            }
            finally
            {
                data.StateAction = 2;
                _ = WinVerifyTrust(new nint(-1), ref action, ref data);
                Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; } // Missing native APIs, inaccessible files and unknown publishers stay visible.
    }

    internal static bool HasMicrosoftOrganization(byte[] encodedName)
    {
        // Parse DER attributes rather than splitting a formatted DN, whose escaped newlines can spoof O=.
        try
        {
            if (encodedName.Length > 65536) return false;
            int offset = 0, organizations = 0; bool microsoft = false;
            int Read(int tag, int end)
            {
                if (offset + 2 > end || encodedName[offset++] != tag) throw new InvalidDataException();
                int length = encodedName[offset++];
                if ((length & 128) != 0)
                {
                    int count = length & 127; if (count is < 1 or > 3 || offset + count > end || encodedName[offset] == 0) throw new InvalidDataException();
                    length = 0; for (int index = 0; index < count; index++) length = checked(length * 256 + encodedName[offset++]);
                    if (length < 128) throw new InvalidDataException();
                }
                if (length > end - offset) throw new InvalidDataException(); return offset + length;
            }
            var sequenceEnd = Read(0x30, encodedName.Length); if (sequenceEnd != encodedName.Length) return false;
            while (offset < sequenceEnd)
            {
                var setEnd = Read(0x31, sequenceEnd);
                while (offset < setEnd)
                {
                    var attributeEnd = Read(0x30, setEnd); var oidEnd = Read(0x06, attributeEnd);
                    bool organization = oidEnd - offset == 3 && encodedName[offset] == 0x55 && encodedName[offset + 1] == 0x04 && encodedName[offset + 2] == 0x0a;
                    offset = oidEnd; if (offset >= attributeEnd) return false;
                    int tag = encodedName[offset]; var valueEnd = Read(tag, attributeEnd);
                    if (valueEnd != attributeEnd) return false;
                    if (organization)
                    {
                        organizations++;
                        var length = valueEnd - offset;
                        var value = tag == 12 ? new UTF8Encoding(false, true).GetString(encodedName, offset, length) :
                            tag == 30 && length % 2 == 0 ? new UnicodeEncoding(true, false, true).GetString(encodedName, offset, length) :
                            tag is 19 or 20 && encodedName.Skip(offset).Take(length).All(part => part < 128) ? Encoding.ASCII.GetString(encodedName, offset, length) : "";
                        microsoft = value.Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
                    }
                    offset = valueEnd;
                }
                if (offset != setEnd) return false;
            }
            return organizations == 1 && microsoft;
        }
        catch { return false; }
    }
    private static PublisherFileIdentity? Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & 0x410) != 0) return null;
        return new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, ((long)info.SizeHigh << 32) | info.SizeLow,
            ((long)info.WrittenHigh << 32) | info.WrittenLow, ((long)info.CreatedHigh << 32) | info.CreatedLow);
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    { public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile
    { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    { public uint Size; public nint PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice; public nint FileInfo; public uint StateAction; public nint StateData, UrlReference; public uint ProviderFlags, UiContext; public nint SignatureSettings; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public nint Certificate; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("sfc.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SfcIsFileProtected(nint rpc, string path);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(nint provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint index);
}
