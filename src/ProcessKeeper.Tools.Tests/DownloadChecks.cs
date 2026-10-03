using System.Net;
using System.Net.Http.Headers;
using ProcessKeeper.Core;

static class DownloadChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "download-behavior-" + Guid.NewGuid().ToString("N"));
        if (!Path.GetFullPath(root).StartsWith("E:\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Tests must stay on E:");
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        try
        {
            var payload = Enumerable.Range(0, 1600000).Select(i => (byte)(i % 251)).ToArray();
            var server = new DownloadHandler(payload);
            using var http = new HttpClient(server);
            using var job = new ResourceDownloadJob(new("https://files.example/archive.bin", root, "archive.bin", Connections: 4), http);
            var result = await job.RunAsync();
            Check(File.ReadAllBytes(result.Path).SequenceEqual(payload), "Parallel ranges must assemble every byte in order");
            Check(server.MaximumConcurrency > 1 && server.MaximumConcurrency <= 4, "Bounded parallel streams must actually run concurrently");
            Check(result.Bytes == payload.Length && result.Sha256.Length == 64, "Result must report real length and computed SHA256");
            Check(!Directory.EnumerateDirectories(root).Any(), "Successful job must remove private fragments");
            server.RangeSupported = false;
            using var sequential = new ResourceDownloadJob(new("https://files.example/plain.bin", root, "plain.bin"), http);
            result = await sequential.RunAsync();
            Check(File.ReadAllBytes(result.Path).SequenceEqual(payload), "A server ignoring Range must fall back without duplicating bytes");
            server.RangeSupported = true; server.TruncateNextRange = true;
            using var retry = new ResourceDownloadJob(new("https://files.example/retry.bin", root, "retry.bin"), http);
            result = await retry.RunAsync();
            Check(File.ReadAllBytes(result.Path).SequenceEqual(payload), "Truncated range must retry without corrupting final output");
            using var mismatch = new ResourceDownloadJob(new("https://files.example/bad.bin", root, "bad.bin", ExpectedSha256: new string('0', 64)), http);
            try { await mismatch.RunAsync(); throw new Exception("Bad checksum incorrectly accepted"); }
            catch (InvalidDataException) { Check(!File.Exists(Path.Combine(root, "bad.bin")), "Bad checksum must never publish output"); }
            File.WriteAllText(Path.Combine(root, "existing.bin"), "original");
            try { using var existing = new ResourceDownloadJob(new("https://files.example/existing.bin", root, "existing.bin"), http); await existing.RunAsync(); throw new Exception("Existing file overwritten"); }
            catch (IOException) { Check(File.ReadAllText(Path.Combine(root, "existing.bin")) == "original", "Existing user files must be preserved"); }
            foreach (var name in new[] { "../escape.bin", "CON", "a:ads", "trailing.", "NUL.txt" })
            {
                try { using var invalid = new ResourceDownloadJob(new("https://files.example/file", root, name), http); throw new Exception("Invalid target accepted: " + name); }
                catch (ArgumentException) { checks++; }
            }
            using var forbidden = new ResourceDownloadJob(new("https://github.com/a/b/releases/download/v/file.bin?token=private", root, "token.bin", SourceId: "ghfast", AllowThirdParty: true), http);
            try { await forbidden.RunAsync(); throw new Exception("Private query sent to mirror"); }
            catch (ArgumentException) { checks++; }
            server.BadRange = true;
            using var malformed = new ResourceDownloadJob(new("https://files.example/wrong.bin", root, "wrong.bin"), http);
            try { await malformed.RunAsync(); throw new Exception("Malformed range accepted"); }
            catch (InvalidDataException) { Check(!File.Exists(Path.Combine(root, "wrong.bin")), "Invalid Content-Range must not produce a file"); }
            server.BadRange = false;
            using var cancelled = new ResourceDownloadJob(new("https://files.example/cancel.bin", root, "cancel.bin"), http);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await cancelled.RunAsync(token: stop.Token); throw new Exception("Cancelled job started"); }
            catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(root, "cancel.bin")), "Cancelled job must not publish output"); }
            Console.WriteLine($"PASS resource downloader: {checks} checks; simulated HTTP payloads on E:");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class DownloadHandler(byte[] bytes) : HttpMessageHandler
    {
        public bool RangeSupported = true, TruncateNextRange, BadRange;
        public int MaximumConcurrency, Active;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var active = Interlocked.Increment(ref Active); MaximumConcurrency = Math.Max(MaximumConcurrency, active);
            try
            {
                await Task.Delay(25, token);
                var range = request.Headers.Range?.Ranges.Single();
                long start = range?.From ?? 0, end = range?.To ?? bytes.LongLength - 1;
                var partial = RangeSupported && range is not null;
                var data = partial ? bytes.Skip((int)start).Take((int)(end - start + 1)).ToArray() : bytes;
                if (partial && data.Length > 1 && TruncateNextRange) { TruncateNextRange = false; data = data.Take(data.Length / 2).ToArray(); }
                var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(data) };
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(BadRange ? start + 1 : start, BadRange ? end + 1 : end, bytes.LongLength);
                return response;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }
}
