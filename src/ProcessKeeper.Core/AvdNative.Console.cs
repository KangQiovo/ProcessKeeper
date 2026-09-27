using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ProcessKeeper.Core;

public static partial class AvdNative
{
    public static async Task<string> GetAvdNameAsync(ProcessRecord owner, int port, string userProfile, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var heldIdentity = OpenVerified(owner);
        using var console = await ConnectConsoleAsync(owner, port, userProfile, deadline.Token).ConfigureAwait(false);
        ValidateConsoleOwner(owner, port);
        return await console.GetNameAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Sends only the official console kill command after authenticating and
    /// rechecking the exact AVD on the same connection. Callers must confirm with the user
    /// beforehand and independently observe process exit afterwards.</summary>
    public static async Task ShutdownAsync(ProcessRecord owner, int port, string expectedAvdName, string userProfile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedAvdName)) throw new ArgumentException(L.T("必须提供确认过的 AVD 名称。"), nameof(expectedAvdName));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        using var heldIdentity = OpenVerified(owner);
        using var console = await ConnectConsoleAsync(owner, port, userProfile, deadline.Token).ConfigureAwait(false);
        string actualName = await console.GetNameAsync(deadline.Token).ConfigureAwait(false);
        if (!actualName.Equals(expectedAvdName, StringComparison.Ordinal))
            throw new InvalidOperationException(L.T("控制台 AVD 名称已变化，未发送退出命令。"));
        ValidateConsoleOwner(owner, port);
        cancellationToken.ThrowIfCancellationRequested();
        await console.WriteAsync("kill", deadline.Token).ConfigureAwait(false);
        await console.ReadResponseAsync(deadline.Token, allowEndOfStream: true).ConfigureAwait(false);
    }

    private static void ValidateConsoleOwner(ProcessRecord owner, int port)
    {
        ValidateIdentity(owner);
        if (!IsPortOwnedBy(port, owner.Id)) throw new InvalidOperationException(L.T("模拟器控制台端口归属已变化或存在冲突，未发送操作命令。"));
    }

    private static async Task<ConsoleConnection> ConnectConsoleAsync(ProcessRecord owner, int port, string userProfile, CancellationToken cancellationToken)
    {
        ValidateConsoleOwner(owner, port);
        var endpoint = ReadLoopbackListeners().Where(row => row.Port == port && row.ProcessId == owner.Id)
            .OrderBy(row => row.IsIPv6).FirstOrDefault() ?? throw new InvalidOperationException(L.T("未找到属于该模拟器的本机控制台。"));
        var address = IPAddress.Parse(endpoint.Address);
        if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
        else if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
        if (!IPAddress.IsLoopback(address)) throw new InvalidOperationException(L.T("拒绝连接非本机模拟器控制台。"));
        var client = new TcpClient(address.AddressFamily);
        ConsoleConnection? console = null;
        try
        {
            await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
            ValidateConsoleOwner(owner, port);
            console = new ConsoleConnection(client);
            var greeting = await console.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (!greeting.Any(line => line.StartsWith("Android Console", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L.T("监听端口没有返回 Android Emulator 控制台标识。"));
            if (greeting.Any(line => line.Contains("Authentication required", StringComparison.OrdinalIgnoreCase)))
            {
                var environment = ReadAndroidEnvironment(owner);
                if (!environment.TryGetValue("USERPROFILE", out string? actualProfile) ||
                    !ProtectionPolicy.TryNormalizePath(actualProfile, out string normalizedActual) ||
                    !ProtectionPolicy.TryNormalizePath(userProfile, out string normalizedRequested) ||
                    !ProtectionPolicy.TryNormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out string signedInProfile) ||
                    !normalizedActual.Equals(normalizedRequested, StringComparison.OrdinalIgnoreCase) ||
                    !normalizedActual.Equals(signedInProfile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(L.T("模拟器用户配置目录无法核实，未读取控制台凭据。"));
                string file = Path.Combine(normalizedActual, ".emulator_console_auth_token");
                var info = new FileInfo(file);
                if (!info.Exists || info.Length is < 1 or > 4096)
                    throw new InvalidOperationException(L.T("模拟器控制台认证文件不存在或格式不受支持。"));
                string token = (await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)).Trim();
                if (token.Length is < 1 or > 1024 || token.Any(character => character < '!' || character > '~'))
                    throw new InvalidOperationException(L.T("模拟器控制台认证文件格式无效。"));
                ValidateConsoleOwner(owner, port);
                await console.WriteAsync("auth " + token, cancellationToken).ConfigureAwait(false);
                await console.ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            }
            return console;
        }
        catch
        {
            if (console is not null) console.Dispose();
            else client.Dispose();
            throw;
        }
    }

    private sealed class ConsoleConnection(TcpClient client) : IDisposable
    {
        private readonly NetworkStream _stream = client.GetStream();
        private readonly byte[] _buffer = new byte[1024];
        private int _offset, _count;

        public async Task<string> GetNameAsync(CancellationToken cancellationToken)
        {
            await WriteAsync("avd name", cancellationToken).ConfigureAwait(false);
            var response = await ReadResponseAsync(cancellationToken).ConfigureAwait(false);
            if (response.Count != 1 || response[0].Length is < 1 or > 256 || response[0].Any(char.IsControl))
                throw new InvalidOperationException(L.T("模拟器没有返回唯一、有效的 AVD 名称。"));
            return response[0];
        }

        public async Task WriteAsync(string command, CancellationToken cancellationToken)
        {
            if (command.Contains('\r') || command.Contains('\n')) throw new InvalidOperationException(L.T("控制台命令包含无效分隔符。"));
            byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<string>> ReadResponseAsync(CancellationToken cancellationToken, bool allowEndOfStream = false)
        {
            var response = new List<string>();
            int total = 0;
            while (response.Count < 128 && total < 65536)
            {
                string? line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    if (allowEndOfStream) return response;
                    throw new IOException(L.T("模拟器控制台意外断开，未确认请求完成。"));
                }
                total += line.Length;
                if (line.Equals("OK", StringComparison.Ordinal)) return response;
                if (line.StartsWith("KO", StringComparison.Ordinal)) throw new InvalidOperationException(L.T("模拟器控制台拒绝了请求；未改用其他设备或 ADB 连接。"));
                if (line.Length > 0) response.Add(line);
            }
            throw new InvalidOperationException(L.T("模拟器控制台响应超出读取上限。"));
        }

        private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();
            while (line.Count < 8192)
            {
                if (_offset >= _count)
                {
                    _count = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0) return line.Count == 0 ? null : throw new IOException(L.T("模拟器控制台响应未完整终止。"));
                }
                byte value = _buffer[_offset++];
                if (value == (byte)'\n') return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.Add(value);
            }
            throw new InvalidOperationException(L.T("模拟器控制台响应行过长。"));
        }

        public void Dispose() => client.Dispose();
    }
}
