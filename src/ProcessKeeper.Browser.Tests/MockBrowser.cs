using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;

internal sealed class MockBrowser : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _accept;
    private readonly Func<Task>? _beforeAcceptCompletion;
    internal int Port { get; }
    internal bool ListenerIsBound => _listener.Server.IsBound;
    internal string Mode { get; set; } = "good";
    internal ConcurrentQueue<string> Requests { get; } = [];
    internal ConcurrentQueue<string> Commands { get; } = [];
    internal TaskCompletionSource ScreenshotRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jOioAAAAASUVORK5CYII=");

    internal MockBrowser(Func<Task>? beforeAcceptCompletion = null)
    {
        _beforeAcceptCompletion = beforeAcceptCompletion;
        _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _connections.Add(HandleAsync(client)); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally { if (_beforeAcceptCompletion is not null) await _beforeAcceptCompletion(); }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new List<byte>(); byte[] one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, _stop.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
                string path = lines[0].Split(' ')[1]; Requests.Enqueue(path);
                if (path == "/redirect-sentinel") { await RespondAsync(stream, 200, "{}"); return; }
                if (Mode == "redirect-version" || Mode == "redirect-list" && path == "/json/list" ||
                    Mode == "redirect-websocket" && path.StartsWith("/devtools/", StringComparison.Ordinal))
                { await RespondAsync(stream, 302, "", "Location: http://127.0.0.1:" + Port + "/redirect-sentinel\r\n"); return; }
                if (path == "/json/version")
                {
                    string body = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["Browser"] = "HeadlessChrome/123.0.0.0", ["Protocol-Version"] = "1.3",
                        ["webSocketDebuggerUrl"] = $"ws://127.0.0.1:{Port}/devtools/browser/browser-1"
                    });
                    await RespondAsync(stream, 200, body); return;
                }
                if (path == "/json/list")
                {
                    if (Mode == "oversized-list") { await RespondAsync(stream, 200, new string('x', ChromiumLivePolicy.JsonLimit + 1)); return; }
                    string websocket = Mode == "external-page" ? "ws://example.invalid:80/devtools/page/page-1" :
                        Mode == "wrong-port" ? $"ws://127.0.0.1:{Port + 1}/devtools/page/page-1" : $"ws://localhost:{Port}/devtools/page/page-1";
                    object page = new { type = Mode == "workers-only" ? "service_worker" : "page", id = "page-1", title = "Fixture page",
                        url = "https://example.invalid/?fixture-secret=never-log", webSocketDebuggerUrl = websocket };
                    string body = Mode == "duplicate-page" ? JsonSerializer.Serialize(new[] { page, page }) : JsonSerializer.Serialize(new[] { page });
                    await RespondAsync(stream, 200, body); return;
                }
                if (path == "/devtools/page/page-1")
                {
                    string key = lines.Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                    string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), _stop.Token);
                    using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
                    byte[] buffer = new byte[8192];
                    while (!_stop.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (!result.EndOfMessage) throw new InvalidOperationException("Fixture command exceeds bound.");
                        using var request = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                        string method = request.RootElement.GetProperty("method").GetString()!;
                        Commands.Enqueue(method); ScreenshotRequested.TrySetResult();
                        if (Mode == "stall") { await Task.Delay(Timeout.Infinite, _stop.Token); return; }
                        int id = request.RootElement.GetProperty("id").GetInt32();
                        if (Mode == "wrong-id") id++;
                        byte[] frame = Png.ToArray();
                        if (Mode == "huge-image") { frame[16] = 0x7f; frame[17] = 0xff; }
                        string encoded = Mode == "invalid-base64" ? "fixture-secret=never-log" : Convert.ToBase64String(frame);
                        if (Mode == "oversized-frame") encoded = new string('A', ((ChromiumLivePolicy.FrameLimit + 2) / 3) * 4 + 4);
                        if (Mode == "event-first")
                        {
                            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"method\":\"Page.frameResized\",\"params\":{}}"), WebSocketMessageType.Text, true, _stop.Token);
                        }
                        byte[] response = Mode == "remote-error" ? Encoding.UTF8.GetBytes("{\"id\":" + id + ",\"error\":{\"message\":\"fixture-secret=never-log\"}}") :
                            JsonSerializer.SerializeToUtf8Bytes(new { id, result = new { data = encoded } });
                        await socket.SendAsync(response.AsMemory(), WebSocketMessageType.Text, true, _stop.Token);
                    }
                    return;
                }
                await RespondAsync(stream, 404, "{}");
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException) { }
        }
    }

    private async Task RespondAsync(NetworkStream stream, int code, string body, string additionalHeaders = "")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n{additionalHeaders}\r\n");
        await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(bytes, _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync();
            // Cancellation callbacks finishing does not mean the accept operation has drained.
            // Keep its socket alive until the accept loop has observed cancellation and exited.
            await _accept;
        }
        finally
        {
            _listener.Stop();
            try { await Task.WhenAll(_connections); }
            finally { _stop.Dispose(); }
        }
    }
}

internal sealed class FixtureHost(int port) : IChromiumLiveHost
{
    internal ProcessRecord Source { get; } = new() { Id = 42042, Name = "chrome.exe", Path = @"C:\Fixture\chrome.exe",
        OwnerSid = "S-1-5-21-fixture-only", SessionId = 1, StartTimeUtcTicks = 12345 };
    internal IReadOnlyList<string> Arguments { get; set; } = [@"C:\Fixture\chrome.exe", "--headless=new", "--remote-debugging-port=" + port];
    internal IReadOnlyList<AvdPortListener> Listeners { get; set; } = [new(port, 42042, false) { Address = "127.0.0.1" }];
    internal bool IdentityValid { get; set; } = true;
    internal bool OwnershipValid { get; set; } = true;
    internal int ValidationCalls { get; private set; }
    public void ValidateIdentity(ProcessRecord process)
    {
        ValidationCalls++;
        if (!IdentityValid || process != Source) throw new InvalidOperationException("fixture-secret=never-log");
    }
    public IReadOnlyList<string> ReadArguments(ProcessRecord process) => Arguments;
    public IReadOnlyList<AvdPortListener> ReadListeners() => Listeners;
    public bool IsPortOwnedBy(int targetPort, int processId) => OwnershipValid && targetPort == port && processId == Source.Id;
}
