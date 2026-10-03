using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using ProcessKeeper.Core;

internal static class Program
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAAQSURBVBhXY2Bg+P+fARkAABz1Af843v6+AAAAAElFTkSuQmCC");
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAACAAIDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD8q3dpGLMSzE5JJySaKKK68X/vFT/E/wAyY7I//9k=");
    private static int _checks;
    private static async Task<int> Main()
    {
        try
        {
            var image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(Png))));
            Check(image != null && image.SequenceEqual(Png), "valid PNG is returned unchanged");
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(Jpeg))));
            Check(image != null && image.SequenceEqual(Jpeg), "valid JPEG is returned unchanged");
            var oversized = (byte[])Png.Clone(); oversized[18] = 4; oversized[19] = 1;
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(oversized))));
            Check(image == null, "PNG dimensions above 1024 are rejected before decoding");
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(Png))),
                validateImage: (_, _) => { Thread.Sleep(120); return Task.FromResult(true); },
                sourceTimeout: TimeSpan.FromMilliseconds(15), totalTimeout: TimeSpan.FromMilliseconds(25));
            Check(image == null, "expired synchronous platform validation never authorizes the image");
            var clock = Stopwatch.StartNew();
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(Png))),
                validateImage: (_, _) => { Thread.Sleep(300); return Task.FromResult(true); },
                sourceTimeout: TimeSpan.FromMilliseconds(15), totalTimeout: TimeSpan.FromMilliseconds(25));
            Check(image == null && clock.ElapsedMilliseconds < 180, "total budget also bounds synchronous platform validation");
            await Reject(Encoding.UTF8.GetBytes("<html>sign in</html>"), "HTML is not an image");
            await Reject(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/ >"), "SVG is never sent to the image decoder");
            await Reject(Encoding.ASCII.GetBytes("GIF89a.............."), "GIF is unsupported");
            await Reject(Png.Take(Png.Length - 1).ToArray(), "truncated PNG is rejected");
            var corrupt = (byte[])Png.Clone(); corrupt[corrupt.Length - 14] ^= 1;
            await Reject(corrupt, "corrupt PNG chunk CRC is rejected");
            await Reject(Jpeg.Take(Jpeg.Length - 2).ToArray(), "JPEG without terminal EOI is rejected");
            var largeJpeg = (byte[])Jpeg.Clone();
            for (int p = 0; p < largeJpeg.Length - 8; p++)
                if (largeJpeg[p] == 255 && largeJpeg[p + 1] == 192) { largeJpeg[p + 7] = 4; largeJpeg[p + 8] = 1; break; }
            await Reject(largeJpeg, "JPEG dimensions above 1024 are rejected");
            await Reject(new byte[262145], "image above byte limit is rejected");

            int requestCount = 0, decoded = 0;
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(++requestCount == 1 ? Png : Jpeg))),
                validateImage: (bytes, _) => Task.FromResult(++decoded > 1));
            Check(image != null && image.SequenceEqual(Jpeg) && decoded == 2, "complete decode failure tries another source");
            requestCount = 0;
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(++requestCount < 4
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response(Jpeg))));
            Check(image != null && image.SequenceEqual(Jpeg), "last backup can recover three unavailable sources");
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
            Check(image == null, "all unavailable sources hide the avatar");
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(Png))),
                validateImage: (_, _) => throw new InvalidDataException("fixture decode error"));
            Check(image == null, "decoder exceptions are optional-image failures");

            foreach (var target in new[] { "http://avatars.githubusercontent.com/u/126958983?v=4&s=160",
                "https://example.org/avatar.png", "https://127.0.0.1/avatar.png",
                "https://avatars.githubusercontent.com/u/1?v=4&s=160",
                "https://user@avatars.githubusercontent.com/u/126958983?v=4&s=160",
                "https://avatars.githubusercontent.com:8443/u/126958983?v=4&s=160" })
            {
                var destinations = new List<string>();
                image = await AuthorAvatarLoader.LoadAsync(new Handler((request, _) =>
                {
                    destinations.Add(request.RequestUri!.AbsoluteUri);
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri(target);
                    return Task.FromResult(response);
                }));
                Check(image == null && !destinations.Contains(target), "untrusted redirect is not requested: " + target);
            }
            requestCount = 0;
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) =>
            {
                if (++requestCount == 1) { var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://avatars.githubusercontent.com/u/126958983?s=160&v=4"); return Task.FromResult(redirect); }
                return Task.FromResult(Response(Jpeg));
            }));
            Check(image != null && image.SequenceEqual(Jpeg), "fixed GitHub owner redirect is allowed");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var handler = new Handler((_, _) => throw new Exception("must not request"));
                image = await AuthorAvatarLoader.LoadAsync(handler, cancelled.Token);
                Check(image == null, "cancelled startup does not send a request");
            }
            var never = new TaskCompletionSource<HttpResponseMessage>();
            clock.Restart();
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => never.Task),
                sourceTimeout: TimeSpan.FromMilliseconds(20), totalTimeout: TimeSpan.FromMilliseconds(60));
            Check(image == null && clock.ElapsedMilliseconds < 300, "ignored HTTP cancellation still respects total deadline");
            requestCount = 0;
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => ++requestCount == 1 ? never.Task : Task.FromResult(Response(Jpeg))),
                sourceTimeout: TimeSpan.FromMilliseconds(20), totalTimeout: TimeSpan.FromMilliseconds(150));
            Check(image != null && image.SequenceEqual(Jpeg), "per-source deadline permits a later healthy source");
            using (var cancelled = new CancellationTokenSource(20))
            {
                clock.Restart();
                image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => never.Task), cancelled.Token);
                Check(image == null && clock.ElapsedMilliseconds < 300, "caller cancellation promptly hides avatar during network wait");
            }
            var streams = new List<GeneratedStream>();
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) =>
            {
                var stream = new GeneratedStream(1024 * 1024); streams.Add(stream);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamBody(stream) });
            }));
            Check(image == null && streams.All(stream => stream.ReadBytes <= 262145 && stream.Disposed), "chunked oversized body stops at byte cap and is disposed");
            image = await AuthorAvatarLoader.LoadAsync(new Handler((_, _) =>
            {
                var response = Response(Png); response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html"); return Task.FromResult(response);
            }));
            Check(image == null, "HTML media type is rejected despite a misleading image prefix");
            Check(OffDedicatedCallerThread(), "transport and validator run off caller thread with no cookies credentials or application payload");
            Console.WriteLine("PASS | " + _checks + " avatar checks | " + Environment.Version + " | process " + (IntPtr.Size * 8) + "-bit");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    private static bool OffDedicatedCallerThread()
    {
        bool result = false;
        var caller = new Thread(() =>
        {
            int callerId = Thread.CurrentThread.ManagedThreadId; bool offThread = true, noSecrets = true;
            var image = AuthorAvatarLoader.LoadAsync(new Handler((request, _) =>
            {
                offThread &= Thread.CurrentThread.ManagedThreadId != callerId;
                noSecrets &= request.Method == HttpMethod.Get && !request.Headers.Contains("Cookie") && request.Headers.Authorization == null && request.Headers.Referrer == null && request.Content == null;
                return Task.FromResult(Response(Png));
            }), validateImage: (_, _) => { offThread &= Thread.CurrentThread.ManagedThreadId != callerId; return Task.FromResult(true); }).GetAwaiter().GetResult();
            result = image != null && offThread && noSecrets;
        }) { IsBackground = true };
        caller.Start(); return caller.Join(2000) && result;
    }
    private static async Task Reject(byte[] bytes, string reason)
        => Check(await AuthorAvatarLoader.LoadAsync(new Handler((_, _) => Task.FromResult(Response(bytes)))) == null, reason);
    private static HttpResponseMessage Response(byte[] bytes) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => _send(request, token);
    }
    private sealed class StreamBody : HttpContent
    {
        private readonly Stream _stream;
        internal StreamBody(Stream stream) => _stream = stream;
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(_stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
    private sealed class GeneratedStream : Stream
    {
        private readonly int _length;
        internal int ReadBytes; internal bool Disposed;
        internal GeneratedStream(int length) => _length = length;
        public override int Read(byte[] buffer, int offset, int count) { int actual = Math.Min(count, _length - ReadBytes); Array.Clear(buffer, offset, actual); ReadBytes += actual; return actual; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => Task.FromResult(Read(buffer, offset, count));
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
