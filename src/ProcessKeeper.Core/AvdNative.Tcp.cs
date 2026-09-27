using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

public sealed record AvdPortListener(int Port, int ProcessId, bool IsIPv6)
{
    public string Address { get; init; } = "";
}

public static partial class AvdNative
{
    public static IReadOnlyList<AvdPortListener> ReadLoopbackListeners() => ReadTcpListeners()
        .Where(row => IsLoopbackBinding(IPAddress.Parse(row.Address))).ToArray();

    private static bool IsLoopbackBinding(IPAddress address) => IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    public static bool IsPortOwnedBy(int port, int processId)
    {
        if (port is < 1 or > 65535 || processId <= 4) return false;
        var rows = ReadTcpListeners().Where(row => row.Port == port).ToArray();
        // A wildcard or another interface owner also makes the binding ambiguous.
        return rows.Any(row => row.ProcessId == processId && IsLoopbackBinding(IPAddress.Parse(row.Address))) &&
            rows.All(row => row.ProcessId == processId);
    }

    private static IReadOnlyList<AvdPortListener> ReadTcpListeners()
    {
        var result = new List<AvdPortListener>();
        foreach (bool ipv6 in new[] { false, true })
        {
            int size = 0;
            uint status = TcpNative.GetExtendedTcpTable(0, ref size, false, ipv6 ? 23 : 2, 3 /* TCP_TABLE_OWNER_PID_LISTENER */, 0);
            if (status != 122 && status != 0) throw new InvalidOperationException(L.F($"无法读取本机 TCP 监听归属（Win32 {status}）。"));
            bool complete = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (size is < 4 or > 16 * 1024 * 1024) throw new InvalidOperationException(L.T("TCP 监听表大小无效。"));
                int capacity = size;
                nint buffer = Marshal.AllocHGlobal(capacity);
                try
                {
                    status = TcpNative.GetExtendedTcpTable(buffer, ref size, false, ipv6 ? 23 : 2, 3, 0);
                    if (status == 122) continue;
                    if (status != 0) throw new InvalidOperationException(L.F($"TCP 监听归属读取失败（Win32 {status}）。"));
                    uint count = unchecked((uint)Marshal.ReadInt32(buffer));
                    int rowSize = ipv6 ? 56 : 24;
                    if (count > (capacity - 4) / rowSize) throw new InvalidOperationException(L.T("TCP 监听表条目越界。"));
                    byte[] bytes = new byte[rowSize];
                    for (int i = 0; i < count; i++)
                    {
                        Marshal.Copy(buffer + 4 + i * rowSize, bytes, 0, rowSize);
                        int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(ipv6 ? 20 : 8, 2));
                        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(ipv6 ? 52 : 20, 4));
                        var address = ipv6 ? new IPAddress(bytes.AsSpan(0, 16), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4))) : new IPAddress(bytes.AsSpan(4, 4));
                        if (port > 0 && pid <= int.MaxValue) result.Add(new AvdPortListener(port, (int)pid, ipv6) { Address = address.ToString() });
                    }
                    complete = true;
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (!complete) throw new InvalidOperationException(L.T("TCP 监听表持续变化，无法可靠核实端口归属。"));
        }
        return result;
    }

    private static class TcpNative
    {
        [DllImport("iphlpapi.dll", SetLastError = true)] internal static extern uint GetExtendedTcpTable(nint table, ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order, int addressFamily, int tableClass, uint reserved);
    }
}
