using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;

internal static class Program
{
    private static int _checks;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ProcessKeeper.Update.Tests", Guid.NewGuid().ToString("N"));
    private static readonly UpdatePreferences Official = new(SourceMode: UpdateSourceMode.Official);
    private static readonly byte[] Binary = MakeBinary();
    private static readonly string Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Binary)).ToLowerInvariant();
    private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAILED: " + label); _checks++; }
    private static void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { Check(true, label); return; } throw new Exception("ACCEPTED: " + label); }
    private static async Task<UpdateException> Error(Func<Task> action, string code, string label)
    {
        try { await action(); } catch (UpdateException ex) { Check(ex.Code == code, label + " actual=" + ex.Code); return ex; }
        throw new Exception("ACCEPTED: " + label);
    }
    private static async Task Cancelled(Func<Task> action, string label)
    {
        try { await action(); } catch (OperationCanceledException) { Check(true, label); return; } throw new Exception("NOT CANCELLED: " + label);
    }
    public static async Task Main()
    {
        Directory.CreateDirectory(Root);
        Versions(); Preferences(); await FixedRepository();
        InstallerIdentityVerification.Run(Check);
        AppCacheCleanupVerification.Run(Check);
        InstanceRedirectVerification.Run(Check);
        await UpdatePackageVerification.Run(Check);
        await UpdateResumeVerification.Run(Check);
        await Metadata(); await Sources(); await Downloads(); await Boundaries(); await AdditionalBoundaries();
        Console.WriteLine($"PASS: {_checks} update assertions | fake HTTP only | no executable launched | fixtures: {Root}");
    }
    private static void Versions()
    {
        var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0" };
        for (var i = 0; i < ordered.Length; i++) { Check(UpdateVersion.TryParse(ordered[i], out var version), "valid semver " + ordered[i]); if (i > 0) { UpdateVersion.TryParse(ordered[i - 1], out var previous); Check(version!.CompareTo(previous) > 0, "semver ordering " + ordered[i]); } }
        foreach (var invalid in new[] { "", "1.2", "release 1.2.3", "01.2.3", "1.02.3", "1.2.03", "1.2.3-01", "1.2.3-", "1.2.3+a..b", " 1.2.3", "1.2.3\n" }) Check(!UpdateVersion.TryParse(invalid, out _), "invalid semver");
        UpdateVersion.TryParse("v1.2.3+linux", out var first); UpdateVersion.TryParse("1.2.3+windows", out var second);
        Check(first!.CompareTo(second) == 0, "build metadata ignored and v accepted");
        UpdateVersion.TryParse("999999999999999999999.0.0", out first); UpdateVersion.TryParse("99999999999999999999.9.9", out second);
        Check(first!.CompareTo(second) > 0, "large numeric semver no overflow");
    }
    private static void Preferences()
    {
        Check(new UpdatePreferences().Repository == UpdatePolicy.Repository && new UpdatePreferences().AutoDesktopShortcut, "fixed repository and shortcut default");
        Check(UpdatePreferencesStore.NormalizeRepository(" https://github.com/KangQiovo/ProcessKeeper.git ") == "KangQiovo/ProcessKeeper", "repository clone URL normalized");
        foreach (var invalid in new[] { "http://github.com/a/b", "https://user@github.com/a/b", "https://github.com/a/b?token=x", "https://github.com/a/b#fragment", "a/b/c", "a/..", "https://evil.example/a/b", "a\\b" }) Reject(() => UpdatePreferencesStore.NormalizeRepository(invalid), "unsafe repository rejected");
        foreach (var invalid in new[] { "[]", "{\"Version\":\"1\"}", "{\"Version\":2}", "{\"Version\":{}}", "{\"SourceMode\":\"0\"}", "{\"SourceMode\":0}", "{\"SourceMode\":\"invalid\"}", "{\"CheckOnStartup\":1}", "{\"Authorization\":\"secret\"}", "{\"Repository\":\"a/b\",\"Repository\":\"c/d\"}", "{\"ThirdPartySourceId\":\"official\"}" }) Reject(() => UpdatePreferencesStore.ReadJson(invalid), "strict preference types/schema");
        foreach (var source in UpdateSources.All.Where(s => !s.IsOfficial)) { var preferences = new UpdatePreferences(SourceMode: UpdateSourceMode.ThirdParty, ThirdPartySourceId: source.Id); Check(UpdatePreferencesStore.ReadJson(Encoding.UTF8.GetString(UpdatePreferencesStore.Serialize(preferences))) == preferences, "node roundtrip " + source.Id); }
        var complete = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(UpdatePreferencesStore.Serialize(Official))!;
        foreach (var name in complete.Keys.ToArray()) { var missing = new Dictionary<string, JsonElement>(complete); missing.Remove(name); Reject(() => UpdatePreferencesStore.ReadJson(JsonSerializer.Serialize(missing)), "present settings cannot default missing " + name); }
        Reject(() => UpdatePreferencesStore.ReadJson("{}"), "empty updates object rejected");
        foreach (var (name, value) in new (string, object?)[] { ("Version", "1"), ("Version", 2), ("Version", new { }), ("SourceMode", "0"), ("SourceMode", 0), ("SourceMode", "Unknown"), ("SourceMode", "auto"), ("CheckOnStartup", 1), ("CheckOnStartup", null), ("AutoDesktopShortcut", "true"), ("Repository", false), ("ThirdPartySourceId", "official") })
        { var invalid = complete.ToDictionary(p => p.Key, p => (object?)p.Value); invalid[name] = value; Reject(() => UpdatePreferencesStore.ReadJson(JsonSerializer.Serialize(invalid)), "complete settings rejects wrong field value " + name); }
        var store = new UpdatePreferencesStore(Path.Combine(Root, "settings")); Check(store.Load() == new UpdatePreferences(), "missing settings defaults");
        store.Save(Official); var old = File.ReadAllBytes(store.FilePath); var changed = Official with { CheckOnStartup = false, AutoDesktopShortcut = false };
        store.Save(changed); Check(store.Load() == changed && File.ReadAllBytes(store.FilePath + ".bak").SequenceEqual(old), "atomic preferences and old backup");
        File.WriteAllText(store.FilePath, "broken JSON"); Reject(() => store.Load(), "bad config does not silently default"); Check(File.ReadAllText(store.FilePath) == "broken JSON", "bad config preserved");
        store.Save(Official); Check(File.ReadAllText(store.FilePath + ".bak") == "broken JSON", "explicit save backs up corrupt prior file");
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp").Any(), "no settings temporary leftovers");
        Check(UpdateSources.All.Count == 7 && UpdateSources.All.Count(s => !s.IsOfficial) == 6, "six verified third parties plus official");
    }
    private static async Task Metadata()
    {
        var releases = new[] { Release("v1.5.1-rc.2", title: "small"), Release("v1.4.9", title: "9999.0.0"), Release("v8.0.0", draft: true), Release("nightly", title: "9.9.9") };
        using (var fake = new Fake((r, _) => Task.FromResult(Json(releases))))
        using (var service = Service(fake))
        {
            var result = await service.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.ThirdParty), "1.5.0");
            Check(result.UpdateAvailable && result.LatestRelease!.Tag == "v1.5.1-rc.2" && result.Releases.Count == 3, "actual tags choose prerelease, ignore draft/title");
            Check(result.Releases.Single(r => r.Tag == "nightly").Version == "" && !result.Releases.Single(r => r.Tag == "nightly").Assets[0].CanAutoInstall, "unknown tag displayed without guessed version");
            Check(fake.Requests.All(r => r.Host == "api.github.com") && fake.Requests.Count == 1, "metadata always official in third-party mode");
        }
        foreach (var variant in new[] { Release(digest: ""), Release(name: "other.exe"), Release(name: "ProcessKeeper.zip"), Release(size: UpdateService.MaximumDownloadBytes + 1), Release(state: "new"), Release(tag: "v65536.0.0"), Release(tag: "v1.65536.0"), Release(tag: "v1.0.65536") })
        {
            using var service = Service(new Fake((_, _) => Task.FromResult(Json(new[] { variant }))));
            Check(!(await service.CheckAsync(Official, "1.5.0")).LatestRelease!.Assets[0].CanAutoInstall, "ineligible asset cannot automatically install");
        }
        foreach (var status in new[] { 404, 500, 403, 302 })
        {
            using var service = Service(new Fake((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
            await Error(() => service.CheckAsync(Official, "1.5.0"), status == 404 ? "not-found" : status == 302 ? "redirect" : "http-" + status, "honest HTTP status");
        }
        using (var service = Service(new Fake((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.Forbidden); r.Headers.Add("X-RateLimit-Remaining", "0"); r.Headers.Add("X-RateLimit-Reset", "1800000000"); return Task.FromResult(r); })))
        { var ex = await Error(() => service.CheckAsync(Official, "1.5.0"), "rate-limit", "rate limit separate from missing release"); Check(ex.RetryAfterUtc == DateTimeOffset.FromUnixTimeSeconds(1800000000), "official reset timestamp retained"); }
        using (var service = Service(new Fake((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))))) await Error(() => service.CheckAsync(Official, "1.5.0"), "network", "network error is not up to date");
        using (var service = Service(new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid") })))) await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata", "invalid JSON");
        using (var service = Service(new Fake((_, _) => { var r = Json(Array.Empty<object>()); r.Content.Headers.ContentLength = 4L * 1024 * 1024 + 1; return Task.FromResult(r); }))) await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata-size", "metadata length ceiling");
        using (var service = Service(new Fake((_, _) => { var r = Json(Array.Empty<object>()); r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/meta"); return Task.FromResult(r); }))) await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata", "effective metadata redirect refused");
        Reject(() => _ = Official with { Repository = "" }, "empty repository rejected at construction");
    }
    private static async Task FixedRepository()
    {
        Check(UpdatePolicy.Repository == "KangQiovo/ProcessKeeper", "one shipped repository authority");
        foreach (var accepted in new[] { "KangQiovo/ProcessKeeper", "kangqiovo/processkeeper", " KANGQIOVO/PROCESSKEEPER ", "https://github.com/KangQiovo/ProcessKeeper", "https://GITHUB.COM/kangqiovo/processkeeper.git/" })
        { var settings = new UpdatePreferences(accepted); Check(settings.Repository == UpdatePolicy.Repository && UpdatePreferencesStore.ReadJson(Encoding.UTF8.GetString(UpdatePreferencesStore.Serialize(settings))).Repository == UpdatePolicy.Repository, "fixed repository aliases normalize canonically"); }
        var defaults = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(UpdatePreferencesStore.Serialize(Official))!;
        var badRepositories = new[] { "", "fork/ProcessKeeper", "KangQiovo/Other", "Microsoft/WinUI-Gallery", "KangQiovо/ProcessKeeper", "https://evil.example/KangQiovo/ProcessKeeper", "https://github.com.evil.example/KangQiovo/ProcessKeeper", "https://github.com@evil.example/KangQiovo/ProcessKeeper", "https://user@github.com/KangQiovo/ProcessKeeper", "http://github.com/KangQiovo/ProcessKeeper", "https://github.com:443/KangQiovo/ProcessKeeper", "https://github.com/fork/../KangQiovo/ProcessKeeper", "https://github.com/KangQiovo/Other/../ProcessKeeper", "https://github.com/%4BangQiovo/ProcessKeeper", "https://github.com/KangQiovo%2fProcessKeeper", "https://github.com/KangQiovo/ProcessKeeper?repo=fork", "https://github.com/KangQiovo/ProcessKeeper#fork", "https://github.com/KangQiovo/ProcessKeeper//", "KangQiovo\\ProcessKeeper" };
        foreach (var repository in badRepositories)
        {
            Reject(() => new UpdatePreferences(repository), "constructor rejects foreign or malformed repository");
            Reject(() => _ = Official with { Repository = repository }, "init setter rejects foreign repository");
            var altered = defaults.ToDictionary(p => p.Key, p => (object?)p.Value); altered["Repository"] = repository;
            Reject(() => UpdatePreferencesStore.ReadJson(JsonSerializer.Serialize(altered)), "import refuses foreign repository instead of replacing it");
        }
        Reject(() => new UpdatePreferences(null!), "null repository cannot default silently");

        // Simulate a caller bypassing ordinary construction. Every external effect
        // boundary must revalidate rather than trust a previously built record.
        var corrupt = new UpdatePreferences();
        typeof(UpdatePreferences).GetField("_repository", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(corrupt, "fork/ProcessKeeper");
        var settingsDirectory = Folder("fixed-repo-settings"); var store = new UpdatePreferencesStore(settingsDirectory); store.Save(Official);
        var settingsBefore = File.ReadAllBytes(store.FilePath);
        Reject(() => store.Save(corrupt), "save revalidates tampered record");
        Check(File.ReadAllBytes(store.FilePath).SequenceEqual(settingsBefore) && Directory.EnumerateFiles(settingsDirectory).Count() == 1, "rejected save has no write or backup side effects");
        var noDirectory = Path.Combine(Root, "fixed-repo-never-created");
        Reject(() => new UpdatePreferencesStore(noDirectory).Save(corrupt), "bad save rejected before directory creation");
        Check(!Directory.Exists(noDirectory), "invalid save creates no directory");
        var badFile = Encoding.UTF8.GetString(settingsBefore).Replace(UpdatePolicy.Repository, "fork/ProcessKeeper"); File.WriteAllText(store.FilePath, badFile);
        Reject(() => store.Load(), "persisted foreign repository rejected"); Check(File.ReadAllText(store.FilePath) == badFile, "rejected load does not rewrite input");
        using (var fake = new DownloadFake())
        using (var service = Service(fake))
        {
            var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; fake.Requests.Clear();
            await RejectTask(() => service.CheckAsync(corrupt, "1.5.0"), "check rejects tampered record before HTTP");
            await RejectTask(() => service.ProbeAsync(corrupt, release, release.Assets[0]), "probe rejects tampered record before HTTP");
            await RejectTask(() => service.DownloadAsync(corrupt, release, release.Assets[0], noDirectory), "download rejects tampered record before staging");
            Check(fake.Requests.Count == 0 && !Directory.Exists(noDirectory), "invalid settings cause zero HTTP and filesystem effects");
            var fork = release with { Repository = "fork/ProcessKeeper" }; var stage = Folder("fixed-repo-foreign-release");
            await Error(() => service.ProbeAsync(Official, fork, fork.Assets[0]), "changed", "caller foreign release cannot select API repository");
            await Error(() => service.DownloadAsync(Official, fork, fork.Assets[0], stage), "changed", "caller foreign release cannot download");
            Check(fake.Requests.Count == 0 && !Directory.EnumerateFiles(stage).Any(), "foreign release rejected before requests or files");
        }
        var badUrls = new[] { "https://github.com/fork/ProcessKeeper/releases/download/v1.5.1/ProcessKeeper.exe", "https://github.com/KangQiovo/Other/releases/download/v1.5.1/ProcessKeeper.exe", "https://github.com.evil.example/KangQiovo/ProcessKeeper/releases/download/v1.5.1/ProcessKeeper.exe", "https://github.com/fork/../KangQiovo/ProcessKeeper/releases/download/v1.5.1/ProcessKeeper.exe", "https://github.com/%4BangQiovo/ProcessKeeper/releases/download/v1.5.1/ProcessKeeper.exe" };
        foreach (var bad in badUrls)
        {
            using var fake = new Fake((_, _) => Task.FromResult(Json(new[] { Release(download: bad) }))); using var service = Service(fake);
            await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata", "HTTP foreign/malformed asset repository rejected");
            Check(fake.Requests.Count == 1 && fake.Requests.Single().AbsoluteUri == "https://api.github.com/repos/" + UpdatePolicy.Repository + "/releases?per_page=20", "metadata endpoint fixed independently of response");
        }
        var wrongPage = JsonSerializer.Serialize(new[] { Release() }).Replace("/KangQiovo/ProcessKeeper/releases/tag/", "/fork/ProcessKeeper/releases/tag/");
        using (var fake = new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wrongPage) })))
        using (var service = Service(fake)) await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata", "HTTP foreign release page rejected");
    }
    private static async Task RejectTask(Func<Task> action, string label)
    { try { await action(); } catch (InvalidDataException) { Check(true, label); return; } throw new Exception("ACCEPTED: " + label); }
    private static async Task Sources()
    {
        using var fake = new DownloadFake(); using var service = Service(fake);
        var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!;
        var log = new Log(); var probes = await service.ProbeAsync(new UpdatePreferences(), release, release.Assets[0], log);
        Check(probes.Count == 7 && probes.All(p => p.Success && p.LatencyMilliseconds >= 0), "all sources use GET fragment probes");
        Check(fake.MaximumProbes == 3 && fake.ActiveProbes == 0, "probe concurrency bounded at three");
        Check(fake.ProbeBytes == 7 * 4096, "small ranged samples only");
        var third = await service.ProbeAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.ThirdParty), release, release.Assets[0]);
        Check(third.Count == 6 && third.All(p => p.SourceId != "official"), "third party auto excludes official binary source");
        var selected = await service.ProbeAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.ThirdParty, ThirdPartySourceId: "llkk"), release, release.Assets[0]);
        Check(selected.Count == 1 && selected[0].SourceId == "llkk", "explicit node only");
        fake.BadProbeHost = "ghfast.top";
        probes = await service.ProbeAsync(new UpdatePreferences(), release, release.Assets[0], log);
        Check(!probes.Single(p => p.SourceId == "ghfast").Success && log.Items.Any(p => p.Stage == "source-failed" && p.SourceId == "ghfast"), "failed probe visible and excluded");
        fake.BadProbeHost = null; var directory = Folder("fastest");
        var result = await service.DownloadAsync(new UpdatePreferences(), release, release.Assets[0], directory, log);
        Check(result.SourceId == "ghfast" && File.ReadAllBytes(result.FilePath).SequenceEqual(Binary), "actual lowest successful latency selected");
        Check(result.Sha256 == Digest.Substring(7) && log.Items.Any(p => p.Stage == "completed"), "official SHA256 and completed progress");
        Check(fake.Requests.Where(r => r.AbsolutePath.Contains("/releases/tags/")).All(r => r.Host == "api.github.com"), "every probe/download rechecks official tag metadata");
    }
    private static async Task Downloads()
    {
        foreach (var mode in new[] { "digest", "truncated", "oversized", "wrong-machine", "changed", "missing-digest" })
        {
            using var fake = new DownloadFake { Mode = mode == "wrong-machine" ? mode : "" }; using var service = Service(fake); var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!;
            fake.Mode = mode; var directory = Folder(mode);
            await Error(() => service.DownloadAsync(Official, release, release.Assets[0], directory), mode == "changed" || mode == "missing-digest" ? "changed" : mode == "digest" ? "digest" : mode == "wrong-machine" ? "executable" : "size", "download refuses " + mode);
            Check(!Directory.EnumerateFiles(directory).Any(), "partial cleanup " + mode);
            if (mode is "changed" or "missing-digest") Check(fake.BinaryRequests == 0, "metadata mutation blocks before binary GET");
        }
        using (var fake = new DownloadFake())
        using (var service = Service(fake))
        {
            var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!;
            await Error(() => service.DownloadAsync(Official, release, release.Assets[0] with { Digest = "sha256:" + new string('a', 64) }, Folder("forged")), "changed", "caller forged digest not trusted");
            Check(fake.BinaryRequests == 0, "caller forge no download");
        }
        foreach (var uri in new[] { "http://github.com/file", "https://127.0.0.1/file", "https://user:pass@github.com/file", "https://evil.example/file", "https://github.com.evil.example/file", "https://ghfast.top:444/file" })
        {
            using var fake = new DownloadFake { Redirect = uri }; using var service = Service(fake); var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!;
            var probes = await service.ProbeAsync(Official, release, release.Assets[0]); Check(probes.Count == 1 && !probes[0].Success, "unsafe redirect rejected"); Check(fake.Requests.All(r => r.AbsoluteUri != uri), "unsafe redirect not requested");
        }
        using (var fake = new DownloadFake { Redirect = "https://release-assets.githubusercontent.com/safe" })
        using (var service = Service(fake))
        { var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; Check((await service.ProbeAsync(Official, release, release.Assets[0]))[0].Success, "official signed asset CDN redirect allowed"); }
    }
    private static async Task Boundaries()
    {
        using (var cancelled = new CancellationTokenSource())
        using (var fake = new DownloadFake())
        using (var service = Service(fake))
        { cancelled.Cancel(); await Cancelled(() => service.CheckAsync(Official, "1.5.0", cancelled.Token), "pre-cancelled check"); Check(fake.Requests.Count == 0, "no request after pre-cancellation"); }
        using (var fake = new DownloadFake())
        using (var service = Service(fake))
        using (var cancel = new CancellationTokenSource())
        {
            var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; fake.Mode = "slow-stream"; var directory = Folder("cancel");
            var task = service.DownloadAsync(Official, release, release.Assets[0], directory, new Log(p => { if (p.Stage == "downloading") cancel.CancelAfter(30); }), cancel.Token);
            await Cancelled(() => task, "cancel during actual stream"); Check(!Directory.EnumerateFiles(directory).Any(), "stream cancellation removes partial files");
        }
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var fake = new Fake(async (_, token) => { entered.TrySetResult(true); await finish.Task.WaitAsync(token); return Json(Array.Empty<object>()); }))
        using (var first = Service(fake))
        using (var secondFake = new DownloadFake())
        using (var second = Service(secondFake))
        using (var cancel = new CancellationTokenSource())
        {
            var running = first.CheckAsync(Official, "1.5.0"); await entered.Task; var queued = second.CheckAsync(Official, "1.5.0", cancel.Token); cancel.Cancel(); await Cancelled(() => queued, "queued concurrent check can cancel"); Check(secondFake.Requests.Count == 0, "operations globally serialized"); finish.SetResult(true); await running;
        }
        using (var fake = new DownloadFake())
        using (var service = new UpdateService(fake, _ => throw new UnauthorizedAccessException("fixture denied")))
        {
            var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; var directory = Folder("denied");
            try { await service.DownloadAsync(Official, release, release.Assets[0], directory); throw new Exception("file permission bypassed"); } catch (UnauthorizedAccessException) { Check(!Directory.EnumerateFiles(directory).Any(), "cannot bypass protected staging write failure"); }
        }
    }
    private static async Task AdditionalBoundaries()
    {
        foreach (var malformed in new[] { JsonSerializer.Serialize(new[] { Release() }).Replace("\"id\":42", "\"id\":\"42\""), JsonSerializer.Serialize(new[] { Release() }).Replace("\"size\":8192", "\"size\":\"8192\"") })
        {
            using var service = Service(new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(malformed) })));
            await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata", "wrong numeric metadata type reports invalid metadata");
        }
        using (var service = Service(new Fake((_, _) => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.Add("Retry-After", "60"); return Task.FromResult(r); })))
        { var started = DateTimeOffset.UtcNow; var ex = await Error(() => service.CheckAsync(Official, "1.5.0"), "rate-limit", "HTTP429 retry-after"); Check(ex.RetryAfterUtc >= started.AddSeconds(59) && ex.RetryAfterUtc < started.AddSeconds(65), "retry-after seconds converted honestly"); }
        using (var service = Service(new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new RepeatingStream(4L * 1024 * 1024 + 16384)) }))))
            await Error(() => service.CheckAsync(Official, "1.5.0"), "metadata-size", "unknown metadata length limited while streaming");
        foreach (var mode in new[] { "bad-range", "html", "encoded", "short-probe" })
        {
            using var fake = new DownloadFake { Mode = mode }; using var service = Service(fake); var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!;
            Check(!(await service.ProbeAsync(Official, release, release.Assets[0]))[0].Success, "unusable source fragment " + mode);
        }
        using (var fake = new DownloadFake { Mode = "ignore-range" })
        using (var service = Service(fake))
        { var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; Check((await service.ProbeAsync(Official, release, release.Assets[0]))[0].Success && fake.ReadBytes == 4096, "range-ignored response consumed only 4KiB"); }
        using (var fake = new DownloadFake { Mode = "fallback" })
        using (var service = Service(fake))
        {
            var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; var log = new Log();
            var result = await service.DownloadAsync(new UpdatePreferences(), release, release.Assets[0], Folder("fallback"), log);
            Check(result.SourceId != "ghfast" && log.Items.Any(p => p.Stage == "source-failed" && p.SourceId == "ghfast"), "corrupt fastest source visibly falls back");
            Check(Directory.EnumerateFiles(Path.GetDirectoryName(result.FilePath)!).Count() == 1 && File.ReadAllBytes(result.FilePath).SequenceEqual(Binary), "fallback retains only verified executable");
        }
        using (var fake = new DownloadFake { Mode = "missing-digest" })
        using (var service = Service(fake))
        { var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; await Error(() => service.DownloadAsync(Official, release, release.Assets[0], Folder("unsigned")), "unsupported-asset", "no official digest no download authority"); Check(fake.BinaryRequests == 0, "missing digest never probes binary"); }
        using (var fake = new DownloadFake { Mode = "loop" })
        using (var service = Service(fake))
        { var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; Check(!(await service.ProbeAsync(Official, release, release.Assets[0]))[0].Success && fake.BinaryRequests == 6, "redirect loop bounded at six requests"); }
        using (var fake = new DownloadFake { Mode = "timeout" })
        using (var service = Service(fake))
        {
            var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; var watch = System.Diagnostics.Stopwatch.StartNew();
            var results = await service.ProbeAsync(Official, release, release.Assets[0]);
            Check(!results[0].Success && results[0].Message.Length > 0 && watch.Elapsed.TotalSeconds >= 7 && watch.Elapsed.TotalSeconds < 12, "real per-source 8-second timeout returns failure");
        }
        using (var fake = new DownloadFake { Mode = "timeout" })
        using (var service = Service(fake))
        using (var cancel = new CancellationTokenSource())
        { var release = (await service.CheckAsync(Official, "1.5.0")).LatestRelease!; cancel.CancelAfter(40); await Cancelled(() => service.ProbeAsync(new UpdatePreferences(), release, release.Assets[0], cancellationToken: cancel.Token), "cancellation stops running and queued source probes"); Check(fake.MaximumProbes <= 3 && fake.ActiveProbes == 0, "cancelled parallel probes release slots"); }
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant" })
        {
            L.Language = language;
            using var service = Service(new Fake((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
            var ex = await Error(() => service.CheckAsync(Official, "1.5.0"), "not-found", "localized missing repository " + language);
            Check(language != "en" || !ex.Message.Contains('未'), "English update error translated");
        }
        L.Language = "en";
    }
    private static UpdateService Service(HttpMessageHandler handler) => new(handler, path => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    private static string Folder(string name) { var path = Path.Combine(Root, name); Directory.CreateDirectory(path); return path; }
    private static byte[] MakeBinary() { var value = new byte[8192]; new Random(75).NextBytes(value); value[0] = (byte)'M'; value[1] = (byte)'Z'; BitConverter.GetBytes(64).CopyTo(value, 60); BitConverter.GetBytes(0x00004550).CopyTo(value, 64); BitConverter.GetBytes((ushort)0x14c).CopyTo(value, 68); BitConverter.GetBytes((ushort)2).CopyTo(value, 86); BitConverter.GetBytes((ushort)0x10b).CopyTo(value, 88); BitConverter.GetBytes((ushort)2).CopyTo(value, 156); return value; }
    private static object Release(string tag = "v1.5.1", string title = "ProcessKeeper", bool draft = false, string? digest = null, string name = "ProcessKeeper.exe", long? size = null, string state = "uploaded", string? download = null) => new
    {
        tag_name = tag, name = title, draft, prerelease = tag.Contains('-'), published_at = "2026-09-27T00:00:00Z", body = "Fixture release notes", html_url = "https://github.com/KangQiovo/ProcessKeeper/releases/tag/" + Uri.EscapeDataString(tag),
        assets = new[] { new { id = 42, name, size = size ?? Binary.Length, state, digest = digest ?? Digest, browser_download_url = download ?? "https://github.com/KangQiovo/ProcessKeeper/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name) } }
    };
    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    private sealed class Log(Action<UpdateProgress>? action = null) : IProgress<UpdateProgress> { public ConcurrentQueue<UpdateProgress> Items { get; } = new(); public void Report(UpdateProgress p) { Items.Enqueue(p); action?.Invoke(p); } }
    private sealed class Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    { public ConcurrentQueue<Uri> Requests { get; } = new(); protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Requests.Enqueue(request.RequestUri!); return callback(request, token); } }
    private sealed class DownloadFake : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new(); public string Mode = ""; public string? Redirect; public string? BadProbeHost; public int ActiveProbes; public int MaximumProbes; public int ProbeBytes; public int BinaryRequests; public int ReadBytes;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!; Requests.Enqueue(uri);
            if (uri.Host == "api.github.com")
            {
                var body = Release(digest: Mode is "changed" or "missing-digest" ? Mode == "missing-digest" ? "" : "sha256:" + new string('a', 64) : Mode == "wrong-machine" ? "sha256:" + Convert.ToHexString(SHA256.HashData(WrongMachine())).ToLowerInvariant() : null);
                return Json(uri.AbsolutePath.Contains("/tags/") ? body : new[] { body });
            }
            Interlocked.Increment(ref BinaryRequests);
            if (Mode == "loop") { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = uri; return r; }
            if (Redirect is not null && uri.Host == "github.com") { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new Uri(Redirect); return r; }
            if (request.Headers.Range is not null)
            {
                var active = Interlocked.Increment(ref ActiveProbes); lock (Requests) MaximumProbes = Math.Max(MaximumProbes, active);
                try
                {
                    await Task.Delay(Mode == "timeout" ? Timeout.Infinite : uri.Host == "ghfast.top" ? 12 : 110, token);
                    var data = Binary.Take(4096).ToArray(); if (BadProbeHost == uri.Host) data[0] = (byte)'<'; Interlocked.Add(ref ProbeBytes, data.Length);
                    if (Mode == "ignore-range") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CountingStream(Binary, count => Interlocked.Add(ref ReadBytes, count))) };
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Mode == "short-probe" ? data.Take(200).ToArray() : data) }; response.Content.Headers.ContentRange = new ContentRangeHeaderValue(Mode == "bad-range" ? 1 : 0, data.Length - 1, Binary.Length);
                    if (Mode == "html") response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html"); if (Mode == "encoded") response.Content.Headers.ContentEncoding.Add("gzip"); return response;
                }
                finally { Interlocked.Decrement(ref ActiveProbes); }
            }
            var bytes = Mode == "wrong-machine" ? WrongMachine() : Binary.ToArray(); if (Mode == "digest" || Mode == "fallback" && uri.Host == "ghfast.top") bytes[7000] ^= 1;
            if (Mode == "truncated") bytes = bytes.Take(bytes.Length - 1).ToArray(); if (Mode == "oversized") bytes = bytes.Concat(new byte[] { 1 }).ToArray();
            HttpContent content = Mode == "slow-stream" ? new StreamContent(new SlowStream(bytes)) : new ByteArrayContent(bytes); content.Headers.ContentLength = Binary.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        private static byte[] WrongMachine() { var b = Binary.ToArray(); b[68] = 0x64; b[69] = 0x86; return b; }
    }
    private sealed class SlowStream(byte[] data) : MemoryStream(data, false)
    { public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { await Task.Delay(200, token); return await base.ReadAsync(buffer, offset, count, token); } }
    private sealed class CountingStream(byte[] data, Action<int> observed) : MemoryStream(data, false)
    { public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { var read = await base.ReadAsync(buffer, offset, count, token); observed(read); return read; } }
    private sealed class RepeatingStream(long remaining) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = (int)Math.Min(count, remaining); Array.Fill(buffer, (byte)' ', offset, read); remaining -= read; return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Read(buffer, offset, count)); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
