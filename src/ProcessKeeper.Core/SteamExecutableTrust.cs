using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ProcessKeeper.Core;

internal static class SteamExecutableTrust
{
    public static void Validate(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        nint fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, fileInfo, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2 /* NONE */,
            UnionChoice = 1 /* FILE */, FileInfo = fileInfo, StateAction = 1 /* VERIFY */,
            ProviderFlags = 0x1000 | 0x10 /* cache-only URL retrieval; no online revocation request */
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new nint(-1), ref action, ref data) != 0)
                throw new InvalidOperationException(L.T("Steam 主程序的数字签名未通过 Windows 信任验证，未启动此文件。"));
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var subject = certificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines | X500DistinguishedNameFlags.DoNotUseQuotes);
            var organizations = subject.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim()).Where(part => part.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
                .Select(part => part[2..].Trim()).ToArray();
            if (organizations.Length != 1 || !IsValveOrganization(organizations[0]))
                throw new InvalidOperationException(L.T("此文件的签名发布者不是 Valve，未发送 Steam 退出请求。"));
        }
        finally
        {
            data.StateAction = 2 /* CLOSE */;
            _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.DestroyStructure<TrustFile>(fileInfo);
            Marshal.FreeHGlobal(fileInfo);
        }
    }

    internal static bool IsValveOrganization(string value) => value.Equals("Valve Corp.", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Valve Corp", StringComparison.OrdinalIgnoreCase) || value.Equals("Valve Corporation", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public nint FileHandle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size; public nint PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public nint FileInfo; public uint StateAction; public nint StateData, UrlReference;
        public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
}
