using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ProcessKeeper.Core;

internal static class DownloadAdaptiveChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "download-adaptive-" + Guid.NewGuid().ToString("N"));
        if (!Path.GetFullPath(root).StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Fixture must stay on E:");
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        try
        {
            var data = Enumerable.Range(0, 6 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
            var digest = Convert.ToHexString(SHA256.HashData(data));
            var server = new Server(data);
            using var http = new HttpClient(server);
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/file.bin?download=1", root, "adaptive.bin", Connections: 12, ExpectedSha256: digest), http))
            {
                var reports = new List<ResourceDownloadProgress>();
                var result = await job.RunAsync(new Inline(value => reports.Add(value)));
                Check(File.ReadAllBytes(result.Path).SequenceEqual(data), "Adaptive direct-link pieces must assemble exactly once in byte order");
                Check(server.Requests.First() == (0L, data.LongLength - 1), "Adaptive transfer must begin with one whole-file range instead of a fixed equal partition");
                Check(server.Requests.Count >= 3 && server.MaximumActive >= 3 && server.MaximumActive <= 12, "Ordinary links must add bounded simultaneous streams as unfinished work is split");
                Check(server.Requests.Skip(1).Any(r => r.From % (data.Length / 12) != 0), "Dynamic ranges must be based on unfinished work, not predetermined equal pieces");
                Check(result.Sha256.Equals(digest, StringComparison.OrdinalIgnoreCase), "Computed hash must match the trusted expected digest");
                Check(reports.Any(value => value.State == "downloading" && value.ActiveConnections > 1 && value.Segments > 1), "Progress exposes real dynamic worker and segment counts");
                Check(reports.All(value => value.ActiveConnections is >= 0 and <= 12 && value.Segments <= 4096), "Visible worker and segment counts stay within their hard limits");
                Check(server.SeenUrls.All(value => value == "https://ordinary.example/file.bin?download=1"), "Generic adaptive downloads preserve ordinary hosts and query strings");
            }
            server.Tag = null; server.Requests.Clear(); server.MaximumActive = 0;
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/no-validator", root, "fresh.bin", Connections: 8, ExpectedSha256: digest), http))
            {
                var result = await job.RunAsync();
                Check(server.MaximumActive > 1 && File.ReadAllBytes(result.Path).SequenceEqual(data), "Fresh range downloads must support ordinary servers without strong validators when final digest is trusted");
                Check(!server.SentInvalidIfRange, "A missing or weak validator must never be sent as If-Range");
            }
            foreach (var tag in new string?[] { null, "W/\"weak\"" })
            {
                server.Tag = tag; server.Requests.Clear();
                var name = tag is null ? "unvalidated" : "weak-tag";
                using var job = new ResourceDownloadJob(new("http://ordinary.example/" + name + "?download=true", root, name + ".bin", Connections: 12), http);
                var paused = false;
                try { await job.RunAsync(new Inline(value => { if (value.State == "downloading" && value.Bytes > 300000 && !paused) { paused = true; job.Pause(); } })); }
                catch (OperationCanceledException) { }
                Check(paused && !File.Exists(Path.Combine(root, name + ".bin")), "Unvalidated pause cannot publish partial bytes: " + name);
                var boundary = server.Requests.Count;
                server.Data = data.Select(value => (byte)(255 - value)).ToArray();
                var result = await job.RunAsync();
                Check(server.Requests.Skip(boundary).First().From == 0, "Unvalidated resume discards previous pieces and starts at zero: " + name);
                Check(File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Changed unvalidated entity never mixes persisted old fragments: " + name);
                Check(!server.SentInvalidIfRange, "Fresh/resumed unvalidated request never sends weak If-Range: " + name);
                server.Data = data;
            }
            server.Tag = "\"v1\""; server.Requests.Clear();
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/pause", root, "resumed.bin", Connections: 8), http))
            {
                var paused = false;
                try { await job.RunAsync(new Inline(p => { if (p.State == "downloading" && p.Bytes > 512 * 1024 && !paused) { paused = true; job.Pause(); } })); }
                catch (OperationCanceledException) { }
                Check(paused && !File.Exists(Path.Combine(root, "resumed.bin")), "Pause must retain partial pieces without publishing a final file");
                var before = server.Requests.Count;
                var result = await job.RunAsync();
                Check(File.ReadAllBytes(result.Path).SequenceEqual(data), "Adaptive pause/resume must retain segment boundaries without overlaps or holes");
                Check(server.Requests.Skip(before).Any(r => r.From > 0), "Validated resume must begin at committed offsets");
            }
            server.RejectSplit = true; server.Requests.Clear();
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/reject", root, "fallback.bin", Connections: 8), http))
            {
                var result = await job.RunAsync();
                Check(server.Sequential > 0 && File.ReadAllBytes(result.Path).SequenceEqual(data), "Rejected dynamic ranges must settle all workers before a clean sequential fallback");
                Check(server.Active == 0, "Late 200 fallback settles every dynamically started response before returning");
            }
            server.RejectSplit = false; server.Requests.Clear(); server.MaximumActive = 0;
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/cap32", root, "cap32.bin", Connections: 32, ExpectedSha256: digest), http))
            {
                var result = await job.RunAsync();
                Check(server.MaximumActive > 8 && server.MaximumActive <= 32 && File.ReadAllBytes(result.Path).SequenceEqual(data), "Expanded maximum allows more than eight real concurrent streams without exceeding 32");
            }
            server.ExtraBodyByte = true;
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/extra", root, "extra.bin", Connections: 2), http))
            {
                var rejected = false;
                try { await job.RunAsync(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !File.Exists(Path.Combine(root, "extra.bin")), "Extra bytes in a completed unsplit response are rejected rather than silently accepted");
            }
            server.ExtraBodyByte = false; server.ChangeTagAfterProbe = true; server.Tag = "W/\"old\"";
            using (var job = new ResourceDownloadJob(new("https://ordinary.example/weak-change", root, "weak-change.bin", Connections: 12), http))
            {
                var rejected = false;
                try { await job.RunAsync(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !File.Exists(Path.Combine(root, "weak-change.bin")), "Contradicting weak-tag metadata is detected even when it is not used as a resume validator");
            }
            server.ChangeTagAfterProbe = false;
            foreach (var cap in new[] { 0, 33 })
            {
                try { using var invalid = new ResourceDownloadJob(new("https://ordinary.example/file", root, "bad.bin", Connections: cap), http); throw new Exception("Invalid concurrency accepted"); }
                catch (ArgumentException) { checks++; }
            }
            Check(!Directory.EnumerateDirectories(root).Any(), "Completed/disposed adaptive jobs must clean only their private pieces");
            Console.WriteLine($"PASS adaptive universal direct-link downloader: {checks} checks; controlled streams, no external downloads");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class Inline(Action<ResourceDownloadProgress> report) : IProgress<ResourceDownloadProgress>
    { public void Report(ResourceDownloadProgress value) => report(value); }
    private sealed class Server(byte[] data) : HttpMessageHandler
    {
        public string? Tag = "\"v1\"";
        public byte[] Data = data;
        public bool RejectSplit, SentInvalidIfRange, ExtraBodyByte, ChangeTagAfterProbe;
        public int Active, MaximumActive, Sequential;
        public readonly List<(long From, long To)> Requests = new();
        public readonly List<string> SeenUrls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var range = request.Headers.Range?.Ranges.Single();
            var probe = range?.From == 0 && range?.To == 0;
            lock (Requests) SeenUrls.Add(request.RequestUri!.AbsoluteUri);
            var partial = range is not null && !(RejectSplit && !probe && range.From > 0);
            var start = partial ? range!.From!.Value : 0;
            var end = partial ? range!.To!.Value : Data.LongLength - 1;
            if (!probe && range is not null) lock (Requests) Requests.Add((range.From!.Value, range.To!.Value));
            if (range is null) Interlocked.Increment(ref Sequential);
            if ((Tag is null || Tag.StartsWith("W/", StringComparison.Ordinal)) && request.Headers.IfRange is not null) SentInvalidIfRange = true;
            var active = probe ? 0 : Interlocked.Increment(ref Active);
            if (!probe) lock (Requests) MaximumActive = Math.Max(MaximumActive, active);
            var stream = new Body(Data, (int)start, (int)(end - start + 1), probe ? null : () => Interlocked.Decrement(ref Active), !probe && ExtraBodyByte);
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = end - start + 1;
            if (Tag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(ChangeTagAfterProbe && !probe ? "W/\"new\"" : Tag);
            if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Data.LongLength);
            return Task.FromResult(response);
        }
    }
    private sealed class Body(byte[] data, int start, int length, Action? closed, bool extra = false) : Stream
    {
        private int _position; private bool _disposed;
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Body));
            if (closed is not null) await Task.Delay(4, token);
            token.ThrowIfCancellationRequested();
            if (_position == length && extra) { _position++; buffer[offset] = 42; return 1; }
            if (_position > length) return 0;
            var read = Math.Min(Math.Min(count, 16384), length - _position);
            Array.Copy(data, start + _position, buffer, offset, read); _position += read; return read;
        }
        protected override void Dispose(bool disposing) { if (!_disposed) { _disposed = true; closed?.Invoke(); } base.Dispose(disposing); }
        public override bool CanRead => !_disposed; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
