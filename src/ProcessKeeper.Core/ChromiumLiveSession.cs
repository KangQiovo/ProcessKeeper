using System.Net.WebSockets;
using System.Text.Json;

namespace ProcessKeeper.Core;

/// <summary>A read-only stream of screenshots of the existing page. Disposing closes
/// only this CDP connection; the browser and page remain running.</summary>
public sealed class ChromiumLiveSession : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly HttpMessageInvoker _transport;
    private readonly Action _verify;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId, _disposed;

    internal ChromiumLiveSession(ClientWebSocket socket, HttpMessageInvoker transport, Action verify)
    { _socket = socket; _transport = transport; _verify = verify; }

    public async Task<byte[]> CaptureFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        bool acquired = false;
        try
        {
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false); acquired = true;
            timeout.Token.ThrowIfCancellationRequested();
            _verify();
            if (_socket.State != WebSocketState.Open) throw new ChromiumLiveFailure(L.T("页面预览连接已经关闭，请重新识别。"));
            int id = Interlocked.Increment(ref _nextId);
            byte[] request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id, method = "Page.captureScreenshot",
                @params = new { format = "jpeg", quality = 75, fromSurface = true, captureBeyondViewport = false }
            });
            await _socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            int totalBytes = 0;
            for (int messages = 0; messages < 32; messages++)
            {
                byte[] payload = await ReceiveAsync(timeout.Token).ConfigureAwait(false);
                totalBytes = checked(totalBytes + payload.Length);
                if (totalBytes > ChromiumLivePolicy.SocketLimit) throw new ChromiumLiveFailure(L.T("页面预览响应超过读取上限。"));
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new ChromiumLiveFailure(L.T("页面预览响应无效。"));
                if (!root.TryGetProperty("id", out var responseId)) continue; // Bounded CDP events.
                if (!responseId.TryGetInt32(out int number) || number != id) throw new ChromiumLiveFailure(L.T("页面预览响应身份不一致。"));
                if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result) ||
                    result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
                    throw new ChromiumLiveFailure(L.T("浏览器暂时无法提供页面图像。"));
                string encoded = data.GetString()!;
                if (encoded.Length == 0 || encoded.Length > ((ChromiumLivePolicy.FrameLimit + 2) / 3) * 4)
                    throw new ChromiumLiveFailure(L.T("页面图像超过预览读取上限。"));
                byte[] frame = Convert.FromBase64String(encoded);
                ChromiumLivePolicy.ValidateFrame(frame);
                _verify();
                return frame;
            }
            throw new ChromiumLiveFailure(L.T("页面预览没有在响应上限内返回图像。"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { if (acquired) _socket.Abort(); throw; }
        catch (ChromiumLiveFailure) { if (acquired) _socket.Abort(); throw; }
        catch (Exception)
        {
            if (acquired) _socket.Abort();
            throw new ChromiumLiveFailure(L.T("页面实时预览已中断或超时，请重新识别；原浏览器保持运行。"));
        }
        finally { if (acquired) _gate.Release(); }
    }

    private async Task<byte[]> ReceiveAsync(CancellationToken token)
    {
        using var result = new MemoryStream();
        byte[] chunk = new byte[16384];
        while (true)
        {
            var received = await _socket.ReceiveAsync(chunk.AsMemory(), token).ConfigureAwait(false);
            if (received.MessageType != WebSocketMessageType.Text || result.Length + received.Count > ChromiumLivePolicy.SocketLimit)
                throw new ChromiumLiveFailure(L.T("页面预览连接已关闭或响应超过读取上限。"));
            result.Write(chunk, 0, received.Count);
            if (received.EndOfMessage) return result.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _socket.Abort();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _socket.Dispose(); _transport.Dispose(); }
        finally { _gate.Release(); }
        // Keep synchronization primitives valid for already-entered concurrent calls.
    }
}
