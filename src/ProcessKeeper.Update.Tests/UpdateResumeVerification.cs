using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ProcessKeeper.Core;

internal static class UpdateResumeVerification
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProcessKeeper-resume-" + Guid.NewGuid().ToString("N"));
        check(Path.GetPathRoot(root)!.Equals("E:\\", StringComparison.OrdinalIgnoreCase), "resumable update fixture remains on E");
        Directory.CreateDirectory(root);
        var binary = new byte[2 * 1024 * 1024]; new Random(104).NextBytes(binary);
        binary[0] = (byte)'M'; binary[1] = (byte)'Z'; BitConverter.GetBytes(64).CopyTo(binary, 60); BitConverter.GetBytes(0x4550).CopyTo(binary, 64);
        BitConverter.GetBytes((ushort)0x14c).CopyTo(binary, 68); BitConverter.GetBytes((ushort)2).CopyTo(binary, 86);
        BitConverter.GetBytes((ushort)0x10b).CopyTo(binary, 88); BitConverter.GetBytes((ushort)2).CopyTo(binary, 156);
        using var handler = new Handler(binary);
        using var service = Create(handler);
        var settings = new UpdatePreferences(SourceMode: UpdateSourceMode.Official);
        var release = (await service.CheckAsync(settings, "1.6.0")).LatestRelease!; var asset = release.Assets.Single();
        string Stage(string name) { var value = Path.Combine(root, name); Directory.CreateDirectory(value); return value; }
        async Task Pause(UpdateDownloadSession session, UpdatePreferences? selected = null)
        {
            handler.Slow = true; using var cancel = new CancellationTokenSource();
            try
            {
                await service.DownloadResumableAsync(selected ?? settings, session, new ProgressLog(progress => { if (progress.Stage == "downloading" && progress.BytesReceived > 0) cancel.Cancel(); }), cancel.Token);
                throw new Exception("Expected owned session pause");
            }
            catch (OperationCanceledException) { check(session.BytesReceived > 0 && session.BytesReceived < session.TotalBytes && !session.IsComplete, "pause retains real partial bytes and never reports completion"); }
            handler.Slow = false;
        }
        foreach (bool officialOffline in new[] { false, true })
        {
            var autoStage = Stage(officialOffline ? "auto-mirror-fallback" : "auto-return-official");
            var auto = service.CreateDownloadSession(release, asset, autoStage);
            await Pause(auto, new UpdatePreferences(SourceMode: UpdateSourceMode.ThirdParty, ThirdPartySourceId: "llkk"));
            long retainedOffset = auto.BytesReceived;
            using var resumeHandler = new Handler(binary) { OfficialOffline = officialOffline, PreferFastMirror = true };
            using var resumeService = Create(resumeHandler);
            var completed = await resumeService.DownloadResumableAsync(new UpdatePreferences(), auto);
            string expectedHost = officialOffline ? "ghfast.top" : "github.com";
            check(completed.SourceId == (officialOffline ? "ghfast" : "official"), "Auto resume reselects official first and only falls back when unavailable: " + officialOffline);
            check(resumeHandler.Ranges.Any(range => range.Host == expectedHost && range.Start == retainedOffset), "Auto source switch resumes exact receipt-verified offset: " + officialOffline);
            check(resumeHandler.Ranges.First().Host == "github.com", "Auto resume probes official before any mirrors: " + officialOffline);
            check(officialOffline || resumeHandler.Ranges.All(range => range.Host == "github.com"), "successful official resume never contacts faster mirrors");
            check(auto.IsComplete && File.ReadAllBytes(completed.FilePath).SequenceEqual(binary) && Directory.EnumerateFiles(autoStage).Count() == 1, "Auto resume completes only byte-exact official-digest file: " + officialOffline);
            resumeService.DiscardDownloadSession(auto);
        }
        var firstStage = Stage("switch-source"); var first = service.CreateDownloadSession(release, asset, firstStage); await Pause(first);
        long offset = first.BytesReceived; int requests = handler.Ranges.Count;
        using (var secondService = Create(new Handler(binary) { SharedRanges = handler.Ranges }))
        {
            var completed = await secondService.DownloadResumableAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.ThirdParty, ThirdPartySourceId: "ghfast"), first);
            check(handler.Ranges.Skip(requests).Any(range => range.Host == "ghfast.top" && range.Start == offset), "new service and selected mirror resume the exact retained offset");
            check(first.IsComplete && first.BytesReceived == binary.Length && File.ReadAllBytes(completed.FilePath).SequenceEqual(binary), "resumed result is byte exact and uses official SHA256 before completion");
            check(completed.SourceId == "ghfast" && Directory.EnumerateFiles(firstStage).Count() == 1, "successful switched-source resume removes owned obsolete partials only");
            secondService.DiscardDownloadSession(first); check(!Directory.EnumerateFiles(firstStage).Any(), "explicit cancel discards only its completed session bytes");
            try { await secondService.DownloadResumableAsync(settings, first); throw new Exception("Discarded session resumed"); }
            catch (IOException) { check(true, "discarded session cannot resume"); }
        }
        var restartStage = Stage("range-refused"); var restart = service.CreateDownloadSession(release, asset, restartStage); await Pause(restart);
        handler.IgnoreResumeRange = true; bool retained = false;
        var result = await service.DownloadResumableAsync(settings, restart, new ProgressLog(progress =>
        {
            if (progress.Stage == "restarting") retained = Directory.EnumerateFiles(restartStage, "*.part").Any() && progress.BytesReceived == 0;
        }));
        check(retained && File.ReadAllBytes(result.FilePath).SequenceEqual(binary), "server refusing Range restarts explicitly and retains original prefix until valid replacement");
        check(Directory.EnumerateFiles(restartStage).Count() == 1, "validated replacement cleans both bounded partial slots");
        service.DiscardDownloadSession(restart); handler.IgnoreResumeRange = false;
        var changedStage = Stage("tampered-partial"); var changed = service.CreateDownloadSession(release, asset, changedStage); await Pause(changed);
        var part = Directory.EnumerateFiles(changedStage).Single(); var bytes = File.ReadAllBytes(part); bytes[50] ^= 1; File.WriteAllBytes(part, bytes);
        try { await service.DownloadResumableAsync(settings, changed); throw new Exception("Tampered prefix accepted"); }
        catch (UpdateException error) { check(error.Code == "partial-changed" && !changed.IsComplete, "tampered retained prefix rejected before append or installation"); }
        // Restore exact owned bytes for explicit cleanup; no foreign content is silently deleted.
        bytes[50] ^= 1; File.WriteAllBytes(part, bytes); service.DiscardDownloadSession(changed);
        var rangeStage = Stage("bad-range"); var badRange = service.CreateDownloadSession(release, asset, rangeStage); await Pause(badRange); offset = badRange.BytesReceived;
        handler.WrongRange = true;
        try { await service.DownloadResumableAsync(settings, badRange); throw new Exception("Wrong range accepted"); }
        catch (UpdateException error) { check(error.Code == "range" && badRange.BytesReceived == offset, "incorrect ContentRange is rejected with the original prefix intact"); }
        handler.WrongRange = false; service.DiscardDownloadSession(badRange);
        var metadataStage = Stage("changed-metadata"); var metadata = service.CreateDownloadSession(release, asset, metadataStage); await Pause(metadata); offset = metadata.BytesReceived;
        handler.ChangedDigest = true;
        try { await service.DownloadResumableAsync(settings, metadata); throw new Exception("Changed metadata accepted"); }
        catch (UpdateException error) { check(error.Code == "changed" && metadata.BytesReceived == offset, "resume revalidates frozen official metadata before further bytes"); }
        handler.ChangedDigest = false; service.DiscardDownloadSession(metadata);
        var corruptStage = Stage("wrong-final-digest"); var corrupt = service.CreateDownloadSession(release, asset, corruptStage); handler.CorruptBinary = true;
        try { await service.DownloadResumableAsync(settings, corrupt); throw new Exception("Wrong digest accepted"); }
        catch (UpdateException error) { check(error.Code == "digest" && !corrupt.IsComplete && !Directory.EnumerateFiles(corruptStage).Any(), "wrong final official digest is never completed or retained as a resumable full file"); }
        handler.CorruptBinary = false; service.DiscardDownloadSession(corrupt);
        foreach (var directory in Directory.EnumerateDirectories(root)) Directory.Delete(directory, false); Directory.Delete(root, false);
    }
    private static UpdateService Create(HttpMessageHandler handler) => new(handler,
        path => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None),
        path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    private sealed class ProgressLog(Action<UpdateProgress> action) : IProgress<UpdateProgress> { public void Report(UpdateProgress progress) => action(progress); }
    private sealed class Handler(byte[] binary) : HttpMessageHandler
    {
        public bool Slow, IgnoreResumeRange, WrongRange, ChangedDigest, CorruptBinary, OfficialOffline, PreferFastMirror;
        public List<(string Host, long Start)> Ranges = new(); public List<(string Host, long Start)>? SharedRanges;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string digest; using (var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(binary)).Replace("-", "").ToLowerInvariant();
            if (request.RequestUri!.Host == "api.github.com")
            {
                var release = new { tag_name = "v1.7.0", name = "ProcessKeeper", draft = false, prerelease = false, html_url = "https://github.com/KangQiovo/ProcessKeeper/releases/tag/v1.7.0", body = "", published_at = "2026-10-03T00:00:00Z",
                    assets = new[] { new { id = 42, name = "ProcessKeeper.exe", size = binary.Length, state = "uploaded", digest = "sha256:" + (ChangedDigest ? new string('a', 64) : digest), browser_download_url = "https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.7.0/ProcessKeeper.exe" } } };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(request.RequestUri.AbsolutePath.Contains("/tags/") ? (object)release : new[] { release })) };
            }
            var range = request.Headers.Range?.Ranges.Single(); long start = range?.From ?? 0, end = range?.To ?? binary.Length - 1;
            var ranges = SharedRanges ?? Ranges; lock (ranges) ranges.Add((request.RequestUri.Host, start));
            bool probe = range is not null && start == 0 && end == 4095;
            if (OfficialOffline && request.RequestUri.Host == "github.com") throw new HttpRequestException("Fixture official source unavailable");
            if (probe && PreferFastMirror) await Task.Delay(request.RequestUri.Host == "ghfast.top" ? 10 : 100, token);
            bool partial = range is not null && (probe || !IgnoreResumeRange);
            var data = binary.ToArray(); if (CorruptBinary && !probe) data[100000] ^= 1;
            if (partial) data = data.Skip((int)start).Take((int)(end - start + 1)).ToArray();
            HttpContent content = Slow && !probe ? new StreamContent(new SlowStream(data)) : new ByteArrayContent(data); content.Headers.ContentLength = data.Length;
            if (partial) content.Headers.ContentRange = new ContentRangeHeaderValue(WrongRange && !probe ? start + 1 : start, end, binary.Length);
            return new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class SlowStream(byte[] data) : MemoryStream(data, false)
    { public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { await Task.Delay(170, token); return await base.ReadAsync(buffer, offset, count, token); } }
}
