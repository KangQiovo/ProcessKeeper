using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace ProcessKeeper.Core;

/// <summary>Attaches only to an already enabled, identity-checked loopback CDP endpoint.
/// This service never launches, restarts, navigates, activates or terminates a browser.</summary>
public sealed class ChromiumLiveService
{
    private readonly IChromiumLiveHost _host;
    public ChromiumLiveService(IChromiumLiveHost? host = null) => _host = host ?? new ChromiumLiveHost();

    public async Task<ChromiumLiveDiscoveryResult> DiscoverAsync(IReadOnlyList<ProcessRecord> scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var targets = new List<BrowserPageTarget>();
        var notices = new List<string>();
        var watch = Stopwatch.StartNew();
        var sources = scope.Where(ChromiumLivePolicy.IsBrowser).DistinctBy(process => (process.Id, process.StartTimeUtcTicks)).Take(33).ToArray();
        if (sources.Length > 32) return new([], [L.T("一次最多识别 32 个浏览器进程，请缩小选择范围。")]);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) { notices.Add(L.T("识别已达到时间上限，请缩小选择范围后重试。")); break; }
            try
            {
                _host.ValidateIdentity(source);
                var arguments = Array.AsReadOnly(_host.ReadArguments(source).ToArray());
                int declared = ChromiumLivePolicy.ReadPort(source, arguments);
                var listeners = _host.ReadListeners();
                var ports = listeners.Where(listener => listener.ProcessId == source.Id && (declared == 0 || listener.Port == declared))
                    .Select(listener => listener.Port).Distinct().Order().Take(17).ToArray();
                if (ports.Length > 16) throw new ChromiumLiveFailure(L.T("候选调试端口过多，无法可靠选择这个浏览器的页面接口。"));
                var found = new List<BrowserPageTarget>();
                foreach (int port in ports)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(20)) break;
                    try
                    {
                        var address = Verify(source, arguments, port);
                        var pages = await ReadPagesAsync(source, arguments, address, port, cancellationToken).ConfigureAwait(false);
                        Verify(source, arguments, port);
                        found.AddRange(pages);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { /* An owned listener is not necessarily a CDP endpoint. Never include URL-bearing errors. */ }
                }
                if (found.Select(target => target.Port).Distinct().Count() > 1)
                    throw new ChromiumLiveFailure(L.T("找到多个页面接口，无法唯一核实原浏览器的调试端口。"));
                targets.AddRange(found);
                if (found.Count == 0) notices.Add(L.F($"PID {source.Id} | 未找到可安全附加的本机 CDP 页面；没有重启或修改原浏览器。"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ChromiumLiveFailure error) { notices.Add($"PID {source.Id} | {error.Message}"); }
            catch (Exception) { notices.Add(L.F($"PID {source.Id} | 浏览器身份或本机页面接口无法核实，请刷新后重试。")); }
        }
        if (sources.Length == 0) notices.Add(L.T("选择范围内没有 Chrome 或 Edge 浏览器主进程。"));
        return new(targets.AsReadOnly(), notices.AsReadOnly());
    }

    public async Task<ChromiumLiveSession> ConnectAsync(BrowserPageTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ClientWebSocket? socket = null;
        HttpMessageInvoker? transport = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ChromiumLivePolicy.ValidId(target.Id)) throw new ChromiumLiveFailure(L.T("页面身份无效，请重新识别。"));
            ChromiumLivePolicy.ValidateWebSocket(target.WebSocketUrl, target.Port, "/devtools/page/" + target.Id);
            var address = Verify(target.Source, target.SourceArguments, target.Port);
            var pages = await ReadPagesAsync(target.Source, target.SourceArguments, address, target.Port, cancellationToken).ConfigureAwait(false);
            if (!pages.Any(page => page.Id == target.Id)) throw new ChromiumLiveFailure(L.T("原页面已关闭或不再是可预览页面，请重新识别。"));
            address = Verify(target.Source, target.SourceArguments, target.Port);
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            transport = new HttpMessageInvoker(NewHandler());
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            // Rewrite the already validated localhost URL to the literal address in
            // the owning process's binding table; no DNS or proxy can redirect it.
            await socket.ConnectAsync(ChromiumLivePolicy.LocalUri(address, target.Port, "/devtools/page/" + target.Id, "ws"),
                transport, timeout.Token).ConfigureAwait(false);
            Verify(target.Source, target.SourceArguments, target.Port);
            var pinned = target with { SourceArguments = Array.AsReadOnly(target.SourceArguments.ToArray()) };
            var session = new ChromiumLiveSession(socket, transport, () => Verify(pinned.Source, pinned.SourceArguments, pinned.Port));
            socket = null; transport = null;
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ChromiumLiveFailure) { throw; }
        catch (Exception) { throw new ChromiumLiveFailure(L.T("无法连接原页面的本机实时预览接口；原浏览器保持运行。")); }
        finally { socket?.Dispose(); transport?.Dispose(); }
    }

    private IPAddress Verify(ProcessRecord source, IReadOnlyList<string> arguments, int port)
    {
        _host.ValidateIdentity(source);
        int declared = ChromiumLivePolicy.ReadPort(source, arguments);
        if (declared != 0 && declared != port || !_host.ReadArguments(source).SequenceEqual(arguments, StringComparer.Ordinal))
            throw new ChromiumLiveFailure(L.T("浏览器启动参数已变化，请重新识别页面。"));
        var address = ChromiumLivePolicy.RequireBinding(_host.ReadListeners(), port, source.Id);
        if (!_host.IsPortOwnedBy(port, source.Id)) throw new ChromiumLiveFailure(L.T("调试端口归属已变化，预览已停止。"));
        _host.ValidateIdentity(source);
        return address;
    }

    private static async Task<IReadOnlyList<BrowserPageTarget>> ReadPagesAsync(ProcessRecord source, IReadOnlyList<string> arguments,
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var client = new HttpClient(NewHandler()) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = ChromiumLivePolicy.JsonLimit };
        using var version = await GetJsonAsync(client, ChromiumLivePolicy.LocalUri(address, port, "/json/version"), timeout.Token).ConfigureAwait(false);
        var versionRoot = version.RootElement;
        string browser = String(versionRoot, "Browser", 128);
        if (!(browser.StartsWith("Chrome/", StringComparison.Ordinal) || browser.StartsWith("HeadlessChrome/", StringComparison.Ordinal) ||
            browser.StartsWith("Edg/", StringComparison.Ordinal) || browser.StartsWith("Microsoft Edge/", StringComparison.Ordinal)) ||
            String(versionRoot, "Protocol-Version", 32).Length == 0)
            throw new ChromiumLiveFailure(L.T("本机监听不是受支持的浏览器调试接口。"));
        string browserSocket = String(versionRoot, "webSocketDebuggerUrl", 1024);
        if (!Uri.TryCreate(browserSocket, UriKind.Absolute, out var browserUri) ||
            !browserUri.AbsolutePath.StartsWith("/devtools/browser/", StringComparison.Ordinal) ||
            !ChromiumLivePolicy.ValidId(browserUri.AbsolutePath["/devtools/browser/".Length..]))
            throw new ChromiumLiveFailure(L.T("浏览器调试接口身份无效。"));
        ChromiumLivePolicy.ValidateWebSocket(browserSocket, port, browserUri.AbsolutePath);
        using var list = await GetJsonAsync(client, ChromiumLivePolicy.LocalUri(address, port, "/json/list"), timeout.Token).ConfigureAwait(false);
        if (list.RootElement.ValueKind != JsonValueKind.Array || list.RootElement.GetArrayLength() > 128)
            throw new ChromiumLiveFailure(L.T("页面列表无效或数量超出预览上限。"));
        var result = new List<BrowserPageTarget>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in list.RootElement.EnumerateArray())
        {
            if (String(page, "type", 32) != "page") continue;
            string id = String(page, "id", 128);
            if (!ChromiumLivePolicy.ValidId(id) || !identifiers.Add(id)) throw new ChromiumLiveFailure(L.T("页面列表包含无效或重复身份。"));
            string websocket = String(page, "webSocketDebuggerUrl", 1024);
            ChromiumLivePolicy.ValidateWebSocket(websocket, port, "/devtools/page/" + id);
            string title = String(page, "title", 4096);
            string url = String(page, "url", 16384);
            result.Add(new(source, arguments, port, id, title, url, websocket));
        }
        return result;
    }

    internal static SocketsHttpHandler NewHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2), MaxResponseHeadersLength = 32,
        AutomaticDecompression = DecompressionMethods.None
    };

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > ChromiumLivePolicy.JsonLimit)
            throw new ChromiumLiveFailure(L.T("页面接口响应状态或大小无效。"));
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > ChromiumLivePolicy.JsonLimit) throw new ChromiumLiveFailure(L.T("页面列表响应超过读取上限。"));
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static string String(JsonElement element, string name, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            throw new ChromiumLiveFailure(L.T("页面接口响应缺少必要字段。"));
        string value = property.GetString()!;
        if (value.Length > maxLength || value.Contains('\0')) throw new ChromiumLiveFailure(L.T("页面接口字段超过预览上限。"));
        return value;
    }
}
