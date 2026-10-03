using System.Net;
using System.Net.Http.Headers;
using ProcessKeeper.Core;

internal static class DownloadAdvancedChecks
{
    internal static async Task Run()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "download-stream-" + Guid.NewGuid().ToString("N"));
        if (!Path.GetFullPath(root).StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Fixture must remain on E:");
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string detail) { checks++; if (!value) throw new Exception(detail); }
        try
        {
            var data = Enumerable.Range(0, 2400000).Select(i => (byte)(i % 239)).ToArray();
            var server = new StreamServer(data);
            using var http = new HttpClient(server);
            using (var paused = new ResourceDownloadJob(new("https://fixture.example/resume", root, "resume.bin"), http))
            {
                var signalled = false;
                var progress = new InlineProgress(p => { if (p.State == "downloading" && p.Bytes > 100000 && !signalled) { signalled = true; paused.Pause(); } });
                try { await paused.RunAsync(progress); throw new Exception("Pause did not stop the transfer"); }
                catch (OperationCanceledException) { Check(signalled && !File.Exists(Path.Combine(root, "resume.bin")), "Pause must not publish unfinished output"); }
                var fragments = Directory.GetDirectories(root).Single();
                Check(Directory.GetFiles(fragments).Any(p => new FileInfo(p).Length > 0), "Pause preserves real streamed fragments");
                var boundary = server.Ranges.Count;
                var resumed = await paused.RunAsync();
                Check(server.Ranges.Skip(boundary).Any(r => r.From > 0), "Validated resume starts at persisted offsets in retained adaptive segments");
                Check(File.ReadAllBytes(resumed.Path).SequenceEqual(data), "Resumed stream must contain every original byte in order");
            }
            Check(!Directory.GetDirectories(root).Any(), "Resumed completion cleans temporary parts");

            using (var changed = new ResourceDownloadJob(new("https://fixture.example/changed", root, "changed.bin"), http))
            {
                var signalled = false;
                try { await changed.RunAsync(new InlineProgress(p => { if (p.State == "downloading" && !signalled) { signalled = true; changed.Pause(); } })); }
                catch (OperationCanceledException) { }
                Check(signalled, "Changed-validator fixture paused after receiving old bytes");
                server.Data = data.Select(b => (byte)(255 - b)).ToArray(); server.Tag = "\"v2\"";
                var start = server.Ranges.Count;
                var result = await changed.RunAsync();
                Check(server.Ranges.Skip(start).First().From == 0, "Changed ETag discards all previous fragments and begins a fresh whole-file range");
                Check(File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Changed validators cannot mix old and new files");
            }

            server.IgnoreRangesAfterProbe = true; server.ThrowDisposedOnCancel = true;
            using (var fallback = new ResourceDownloadJob(new("https://fixture.example/late-no-range", root, "fallback.bin"), http))
            {
                var result = await fallback.RunAsync();
                Check(File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Late 200 range rejection must settle sibling streams and fall back to one complete transfer");
                Check(server.SequentialRequests > 0, "Late range rejection performs a real sequential GET");
            }
            server.IgnoreRangesAfterProbe = false; server.ThrowDisposedOnCancel = false;
            server.IgnoreRangesAfterProbe = true;
            using (var restored = new ResourceDownloadJob(new("https://fixture.example/restored", root, "restored.bin"), http))
            {
                var signalled = false;
                try { await restored.RunAsync(new InlineProgress(p => { if (p.State == "downloading" && !signalled) { signalled = true; restored.Pause(); } })); }
                catch (OperationCanceledException) { }
                var completed = Path.Combine(Directory.GetDirectories(root).Single(), "completed.tmp");
                Check(signalled && File.Exists(completed) && new FileInfo(completed).Length < server.Data.Length, "Range-fallback pause preserves an unfinished sequential staging file");
                server.IgnoreRangesAfterProbe = false;
                var result = await restored.RunAsync();
                Check(File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Restoring range support must rebuild all parts instead of reusing an unfinished sequential staging file");
            }
            server.TruncateNextBody = true;
            using (var truncated = new ResourceDownloadJob(new("https://fixture.example/truncated", root, "truncated.bin", Connections: 1), http))
            {
                var initial = server.SequentialRequests;
                var result = await truncated.RunAsync();
                Check(server.SequentialRequests >= initial + 2, "Truncated sequential streams are retried");
                Check(File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Sequential retry restarts the output instead of appending duplicated bytes");
            }

            server.ChangeTagAfterProbe = true;
            using (var inconsistent = new ResourceDownloadJob(new("https://fixture.example/inconsistent", root, "inconsistent.bin"), http))
            {
                var rejected = false;
                try { await inconsistent.RunAsync(); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && !File.Exists(Path.Combine(root, "inconsistent.bin")), "A range response contradicting the probed strong ETag must never publish output");
            }
            server.ChangeTagAfterProbe = false;
            server.Tag = null;
            server.LastModified = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
            foreach (var date in new DateTimeOffset?[] { null, server.LastModified })
            {
                server.ServerDate = date;
                var before = server.Ranges.Count;
                using var weak = new ResourceDownloadJob(new("https://fixture.example/weak", root, "weak-" + (date.HasValue ? "same-date" : "no-date") + ".bin"), http);
                var result = await weak.RunAsync();
                Check(server.Ranges.Count > before && File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Fresh adaptive ranges work with weak dates while byte assembly remains exact");
                Check(!server.InvalidIfRange, "A weak date is never sent as an If-Range validator");
            }
            server.ServerDate = server.LastModified.Value.AddMinutes(2);
            var previous = server.Ranges.Count;
            using (var dated = new ResourceDownloadJob(new("https://fixture.example/dated", root, "dated.bin"), http))
            {
                var result = await dated.RunAsync();
                Check(server.Ranges.Count >= previous + 4 && File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "An adequately separated Date/Last-Modified pair may use validated parallel ranges");
            }
            server.Tag = "W/\"weak\"";
            previous = server.Ranges.Count;
            using (var weakTag = new ResourceDownloadJob(new("https://fixture.example/weak-tag", root, "weak-tag.bin"), http))
            {
                var result = await weakTag.RunAsync();
                Check(server.Ranges.Count > previous && File.ReadAllBytes(result.Path).SequenceEqual(server.Data), "Fresh adaptive ranges can use a weak-ETag server without relying on weak validation");
                Check(!server.InvalidIfRange, "A weak ETag must not be sent or replaced by date If-Range");
            }
            server.Tag = "\"v3\""; server.LastModified = server.ServerDate = null;
            using (var abandoned = new ResourceDownloadJob(new("https://fixture.example/abandoned", root, "abandoned.bin"), http))
            {
                var paused = false;
                try { await abandoned.RunAsync(new InlineProgress(p => { if (p.State == "downloading" && !paused) { paused = true; abandoned.Pause(); } })); }
                catch (OperationCanceledException) { }
                Check(Directory.GetDirectories(root).Any(), "Paused unfinished download remains available before explicit discard");
            }
            Check(!Directory.GetDirectories(root).Any() && !File.Exists(Path.Combine(root, "abandoned.bin")), "Explicit disposal removes only unfinished staging and preserves completed user files");
            Console.WriteLine($"PASS downloader streamed pause/resume, validators and fallbacks: {checks} checks");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class InlineProgress(Action<ResourceDownloadProgress> callback) : IProgress<ResourceDownloadProgress>
    { public void Report(ResourceDownloadProgress value) => callback(value); }
    private sealed class StreamServer(byte[] data) : HttpMessageHandler
    {
        internal byte[] Data = data;
        internal string? Tag = "\"v1\"";
        internal DateTimeOffset? LastModified, ServerDate;
        internal bool IgnoreRangesAfterProbe, ThrowDisposedOnCancel, TruncateNextBody, ChangeTagAfterProbe, InvalidIfRange;
        internal int SequentialRequests;
        internal readonly List<(long From, long To)> Ranges = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.Single();
            var probe = range?.From == 0 && range?.To == 0;
            var partial = range is not null && (probe || !IgnoreRangesAfterProbe);
            if (!probe && IgnoreRangesAfterProbe && range is not null)
            {
                if (range.From == 0) await Task.Delay(Timeout.Infinite, cancellationToken);
                else await Task.Delay(30, cancellationToken);
            }
            var start = partial ? range!.From!.Value : 0;
            var end = partial ? range!.To ?? Data.Length - 1 : Data.Length - 1;
            if (!probe && partial) lock (Ranges) Ranges.Add((start, end));
            if (!probe && request.Headers.IfRange is not null && (Tag?.StartsWith("W/", StringComparison.Ordinal) == true ||
                Tag is null && (!ServerDate.HasValue || !LastModified.HasValue || ServerDate.Value - LastModified.Value < TimeSpan.FromSeconds(60)))) InvalidIfRange = true;
            if (range is null) Interlocked.Increment(ref SequentialRequests);
            var length = checked((int)(end - start + 1));
            var actual = length;
            if (range is null && TruncateNextBody) { TruncateNextBody = false; actual /= 2; }
            var body = new SlowBody(Data, checked((int)start), actual, !probe, ThrowDisposedOnCancel);
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(body) };
            if (Tag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(ChangeTagAfterProbe && !probe ? "\"unexpected\"" : Tag);
            response.Headers.Date = ServerDate; response.Content.Headers.LastModified = LastModified;
            response.Content.Headers.ContentLength = length;
            if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Data.Length);
            return response;
        }
    }
    private sealed class SlowBody(byte[] data, int start, int length, bool slow, bool failDisposed) : Stream
    {
        private int _offset; private bool _disposed;
        public override bool CanRead => !_disposed; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => length; public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try { if (slow) await Task.Delay(5, cancellationToken); }
            catch (OperationCanceledException) when (_disposed && failDisposed) { throw new ObjectDisposedException(nameof(SlowBody)); }
            if (_disposed) throw new ObjectDisposedException(nameof(SlowBody));
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(Math.Min(8192, count), length - _offset);
            if (read == 0) return 0;
            Array.Copy(data, start + _offset, buffer, offset, read); _offset += read; return read;
        }
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
