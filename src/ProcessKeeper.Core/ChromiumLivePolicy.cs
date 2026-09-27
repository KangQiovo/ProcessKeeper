using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace ProcessKeeper.Core;

internal static class ChromiumLivePolicy
{
    internal const int JsonLimit = 1024 * 1024;
    internal const int FrameLimit = 8 * 1024 * 1024;
    internal const int SocketLimit = 12 * 1024 * 1024;

    internal static bool IsBrowser(ProcessRecord process) =>
        process.Name is not null && (process.Name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
            process.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)) &&
        Path.GetFileName(process.Path).Equals(process.Name, StringComparison.OrdinalIgnoreCase);

    internal static int ReadPort(ProcessRecord process, IReadOnlyList<string> arguments)
    {
        if (!IsBrowser(process) || arguments.Count is < 2 or > 512 ||
            !ProtectionPolicy.TryNormalizePath(arguments[0], out string executable) ||
            !ProtectionPolicy.TryNormalizePath(process.Path, out string source) ||
            !executable.Equals(source, StringComparison.OrdinalIgnoreCase) ||
            arguments.Any(value => value.Length > 32768 || value.Contains('\0')))
            throw new ChromiumLiveFailure(L.T("无法核实浏览器主进程及原启动参数。"));
        if (arguments.Skip(1).Any(value => Option(value, "--type") || Option(value, "--remote-debugging-pipe")))
            throw new ChromiumLiveFailure(L.T("这是浏览器子进程或管道调试实例，无法通过本机页面接口安全连接。"));
        if (!arguments.Skip(1).Any(value => value is "--headless" or "--headless=new" or "--headless=old"))
            throw new ChromiumLiveFailure(L.T("所选浏览器未以支持的无头模式启动。"));
        int? port = null;
        for (int i = 1; i < arguments.Count; i++)
        {
            string value = arguments[i];
            if (Option(value, "--remote-debugging-address"))
            {
                string address = Value(arguments, ref i, "--remote-debugging-address");
                if (!IsLocalHost(address)) throw new ChromiumLiveFailure(L.T("浏览器调试地址不是回环地址，未连接。"));
            }
            else if (Option(value, "--remote-debugging-port"))
            {
                if (port is not null || !int.TryParse(Value(arguments, ref i, "--remote-debugging-port"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed is < 0 or > 65535)
                    throw new ChromiumLiveFailure(L.T("浏览器调试端口参数无效或重复。"));
                port = parsed;
            }
        }
        return port ?? throw new ChromiumLiveFailure(L.T("浏览器未开放 CDP 页面接口，无法安全附加实时预览；原进程保持运行。"));
    }

    private static bool Option(string value, string name) => value == name || value.StartsWith(name + "=", StringComparison.Ordinal);
    private static string Value(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (arguments[index].StartsWith(option + "=", StringComparison.Ordinal)) return arguments[index][(option.Length + 1)..];
        if (++index >= arguments.Count) throw new ChromiumLiveFailure(L.T("浏览器调试参数缺少值。"));
        return arguments[index];
    }

    internal static bool IsLocalHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address) && address.ScopeIdOrZero() == 0;

    // Never select a wildcard, or a port shared with another process. Native ownership
    // also checks interfaces omitted by ReadLoopbackListeners.
    internal static IPAddress RequireBinding(IReadOnlyList<AvdPortListener> listeners, int port, int processId)
    {
        var rows = listeners.Where(row => row.Port == port).ToArray();
        if (port is < 1 or > 65535 || rows.Length == 0 || rows.Any(row => row.ProcessId != processId ||
            !IPAddress.TryParse(row.Address, out var address) || !IPAddress.IsLoopback(address) || address.ScopeIdOrZero() != 0))
            throw new ChromiumLiveFailure(L.T("本机调试端口不再由原浏览器独占，或监听地址不是回环地址。"));
        return IPAddress.Parse(rows.OrderBy(row => row.IsIPv6).First().Address);
    }

    internal static bool ValidId(string id) => id.Length is > 0 and <= 128 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static Uri ValidateWebSocket(string value, int port, string path)
    {
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        int rawPathStart = authorityStart >= 3 ? value.IndexOf('/', authorityStart) : -1;
        if (value.Length > 1024 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "ws" ||
            !IsLocalHost(uri.Host) || uri.Port != port || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != path || value.Contains('\\') || value.Contains('%') ||
            rawPathStart < 0 || value[rawPathStart..] != path)
            throw new ChromiumLiveFailure(L.T("页面接口返回了不可信的调试地址，未连接。"));
        return uri;
    }

    internal static Uri LocalUri(IPAddress address, int port, string path, string scheme = "http") =>
        new UriBuilder(scheme, address.ToString(), port, path).Uri;

    internal static void ValidateFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 24 or > FrameLimit) throw new ChromiumLiveFailure(L.T("页面图像大小超出预览上限。"));
        if (bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (bytes.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new ChromiumLiveFailure(L.T("页面图像格式无效。"));
            ValidateDimensions(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));
            return;
        }
        if (bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9)
        {
            int offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset++] != 0xff) break;
                while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                if (offset >= bytes.Length) break;
                int marker = bytes[offset++];
                if (marker is 0xda or 0xd9) break;
                if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
                if (offset + 2 > bytes.Length) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
                if (length < 2 || offset + length > bytes.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
                {
                    if (length < 8) break;
                    ValidateDimensions(BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
                    return;
                }
                offset += length;
            }
        }
        throw new ChromiumLiveFailure(L.T("页面接口未返回可识别的 JPEG 或 PNG 图像。"));
    }

    private static void ValidateDimensions(uint width, uint height)
    {
        if (width is 0 or > 4096 || height is 0 or > 4096 || (ulong)width * height > 8 * 1024 * 1024)
            throw new ChromiumLiveFailure(L.T("页面图像尺寸超出预览上限。"));
    }

    private static long ScopeIdOrZero(this IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
}
