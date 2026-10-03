using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); checks++; }
var rules = new[] { new WhitelistRule { Id = "steam", Name = "Steam", Kind = RuleKind.Application, Value = "known:steam", IncludeDescendants = true },
    new WhitelistRule { Id = "qq", Name = "QQ", Kind = RuleKind.ProcessName, Value = "qq.exe" },
    new WhitelistRule { Id = "package", Name = "Package", Kind = RuleKind.Application, Value = "package:Sample.Package_123456789abcd" } };
byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
string BlobSha(byte[] bytes) { using var sha = SHA1.Create(); var header = Bytes("blob " + bytes.Length + "\0"); return BitConverter.ToString(sha.ComputeHash(header.Concat(bytes).ToArray())).Replace("-", "").ToLowerInvariant(); }
var json = RuleFileCodec.Serialize(rules); var bytes = Bytes(json); var blob = BlobSha(bytes);
object Item(string file = "shared.json", string type = "file", string? sha = null, long? size = null, string? path = null) => new
{ name = file, path = path ?? CloudWhitelistProfiles.DirectoryPath + file, type, sha = sha ?? blob, size = size ?? bytes.Length };
string Listing(params object[] entries) => JsonSerializer.Serialize(entries.Length == 0 ? new[] { Item() } : entries);
string Content(byte[]? payload = null, string? sha = null, string name = "shared.json", string? path = null, string type = "file", string encoding = "base64", long? size = null) =>
    JsonSerializer.Serialize(new { name, path = path ?? CloudWhitelistProfiles.DirectoryPath + name, type, sha = sha ?? blob,
        size = size ?? (payload ?? bytes).Length, encoding, content = Convert.ToBase64String(payload ?? bytes) });
HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
async Task Fails(Func<Task> run, CloudProfileFailure failure, string name)
{ try { await run(); throw new Exception("FAIL: " + name); } catch (CloudWhitelistProfilesException error) { Check(error.Failure == failure, name); } }

var handler = new RecordingHandler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/shared.json", StringComparison.Ordinal) ? Content() : Listing())));
using var client = new HttpClient(handler);
using var cloud = new CloudWhitelistProfiles(client);
Check(handler.Requests.Count == 0, "construction never fetches network data");
var descriptors = await cloud.ListAsync();
Check(descriptors.Count == 1 && descriptors[0].Name == "shared" && descriptors[0].Sha == blob && descriptors[0].Size == bytes.Length, "listing yields immutable original file identity");
Check(descriptors[0].Path == "community/profiles/shared.json" && descriptors[0].FileName == "shared.json", "directory is fixed");
var preview = await cloud.ReadAsync(descriptors[0]);
Check(preview.Rules.SequenceEqual(rules) && RuleFileCodec.Parse(preview.Json).SequenceEqual(rules), "preview strictly retains portable rule content");
Check(handler.Requests.Count == 3, "read rechecks current directory before the selected file");
Check(handler.Requests.All(request => request.Uri.Scheme == "https" && request.Uri.Host == "api.github.com" && request.Uri.AbsolutePath.StartsWith("/repos/KangQiovo/ProcessKeeper/contents/community/profiles", StringComparison.Ordinal)
    && !request.Auth && !request.Cookie && request.Method == "GET"), "only fixed unauthenticated public contents routes are requested");
Check(handler.Requests.All(request => request.Accept == "application/vnd.github+json" && request.ApiVersion == "2022-11-28"), "API media type and compatibility version are explicit");
var refreshedDescriptors = await cloud.ListAsync();
await Fails(async () => { await cloud.ReadAsync(descriptors[0]); }, CloudProfileFailure.InvalidDescriptor, "a replaced list releases old issued descriptors");
Check((await cloud.ReadAsync(refreshedDescriptors[0])).Rules.SequenceEqual(rules), "the current list still produces a verified preview");

async Task ListingFailure(string list, CloudProfileFailure failure, string name)
{ using var fake = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(list)))); using var service = new CloudWhitelistProfiles(fake); await Fails(async () => { await service.ListAsync(); }, failure, name); }
await ListingFailure(Listing(Item(type: "dir")), CloudProfileFailure.InvalidResponse, "directories are rejected");
await ListingFailure(Listing(Item(type: "symlink")), CloudProfileFailure.InvalidResponse, "symlinks are rejected");
await ListingFailure(JsonSerializer.Serialize(new[] { new { name = "shared.json", path = "community/profiles/shared.json", type = "file", sha = blob, size = bytes.Length, submodule_git_url = "https://other.invalid/repo" } }), CloudProfileFailure.InvalidResponse, "file-shaped submodules are rejected");
await ListingFailure(Listing(Item(file: "../shared.json")), CloudProfileFailure.InvalidResponse, "path traversal is rejected");
await ListingFailure(Listing(Item(path: "outside/shared.json")), CloudProfileFailure.InvalidResponse, "directory replacement is rejected");
await ListingFailure(Listing(Item(size: RuleFileCodec.MaximumFileBytes + 1)), CloudProfileFailure.TooLarge, "oversized rules are rejected in listing");
await ListingFailure(Listing(Item(sha: new string('g', 40))), CloudProfileFailure.InvalidResponse, "invalid git SHA is rejected");
await ListingFailure(JsonSerializer.Serialize(new[] { new { name = "shared.json", path = "community/profiles/shared.json", type = "file", sha = blob, size = "not-a-size" } }), CloudProfileFailure.InvalidResponse, "non-numeric file metadata is rejected");
await ListingFailure(Listing(Item(file: new string('a', 81) + ".json")), CloudProfileFailure.InvalidResponse, "JSON profile names fit the local 80-character name limit");
await ListingFailure(Listing(Item(file: ".json")), CloudProfileFailure.InvalidResponse, "empty JSON profile stems are rejected");
await ListingFailure(Listing(Item(file: "name .json")), CloudProfileFailure.InvalidResponse, "JSON names do not contain hidden trailing stem spaces");
await ListingFailure(Listing(Item(), Item()), CloudProfileFailure.InvalidResponse, "duplicate filenames are rejected");
await ListingFailure(JsonSerializer.Serialize(Enumerable.Range(0, 101).Select(i => Item(file: i + ".json"))), CloudProfileFailure.TooLarge, "too many JSON files fail without silent truncation");
await ListingFailure(JsonSerializer.Serialize(Enumerable.Range(0, 201).Select(i => Item(file: i + ".txt"))), CloudProfileFailure.TooLarge, "too many total entries are bounded");
await ListingFailure("{\"entries\":[]}", CloudProfileFailure.InvalidResponse, "unexpected directory shape is rejected");
using (var fake = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response("[]")))))
using (var service = new CloudWhitelistProfiles(fake)) Check((await service.ListAsync()).Count == 0, "an empty valid directory is shown honestly");
using (var fake = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(Listing(Item(file: "README.md"), Item()))))))
using (var service = new CloudWhitelistProfiles(fake)) Check((await service.ListAsync()).Count == 1, "normal documentation is not treated as a profile");

async Task ReadFailure(string fileBody, CloudProfileFailure failure, string name, string? refreshedListing = null)
{
    var count = 0;
    using var fake = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/shared.json", StringComparison.Ordinal)
        ? fileBody : ++count > 1 && refreshedListing is not null ? refreshedListing : Listing()))));
    using var service = new CloudWhitelistProfiles(fake); var entry = (await service.ListAsync())[0];
    await Fails(async () => { await service.ReadAsync(entry); }, failure, name);
}
await ReadFailure(Content(), CloudProfileFailure.Changed, "changed listing SHA is rejected before preview", Listing(Item(sha: new string('a', 40))));
await ReadFailure(Content(), CloudProfileFailure.InvalidResponse, "new symlink cannot replace the original selected file", Listing(Item(type: "symlink")));
await ReadFailure(Content(sha: new string('a', 40)), CloudProfileFailure.Changed, "changed file SHA is rejected");
await ReadFailure(Content(Bytes(json.Replace("Steam", "Steem"))), CloudProfileFailure.Changed, "equal-length original bytes are independently verified by git blob SHA");
await ReadFailure(Content(path: "community/profiles/other.json"), CloudProfileFailure.InvalidResponse, "selected path metadata is rechecked");
await ReadFailure(Content(encoding: "none"), CloudProfileFailure.InvalidResponse, "unsupported contents encoding is rejected");
await ReadFailure(Content(size: RuleFileCodec.MaximumFileBytes + 1), CloudProfileFailure.TooLarge, "oversized decoded file is rejected");

async Task PayloadFailure(string payload, CloudProfileFailure failure, string name)
{
    var raw = Bytes(payload); var digest = BlobSha(raw);
    using var fake = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/shared.json", StringComparison.Ordinal)
        ? Content(raw, digest) : Listing(Item(sha: digest, size: raw.Length))))));
    using var service = new CloudWhitelistProfiles(fake); var entry = (await service.ListAsync())[0];
    await Fails(async () => { await service.ReadAsync(entry); }, failure, name);
}
await PayloadFailure(json.Replace("known:steam", "path:E:\\\\Apps\\\\steam.exe"), CloudProfileFailure.NotPortable, "application path identities cannot leak another machine's directories");
await PayloadFailure(RuleFileCodec.Serialize(new[] { rules[0] with { Kind = RuleKind.Directory, Value = "E:\\Apps" } }), CloudProfileFailure.NotPortable, "directory rules are not community portable");
await PayloadFailure(RuleFileCodec.Serialize(new[] { rules[0] with { Kind = RuleKind.ExecutablePath, Value = "E:\\Apps\\steam.exe" } }), CloudProfileFailure.NotPortable, "executable paths are not community portable");
await PayloadFailure(json.Replace("known:steam", "known:steam*"), CloudProfileFailure.NotPortable, "application key wildcards are rejected");
await PayloadFailure(json.Replace("\"Version\": 1", "\"Version\": 2"), CloudProfileFailure.InvalidResponse, "unsupported rule schema is rejected");
await PayloadFailure(json.Replace("\"Rules\":", "\"Profiles\":{},\"Rules\":"), CloudProfileFailure.InvalidResponse, "profile metadata cannot write local settings");

foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
{ using var fake = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response("{}", status)))); using var service = new CloudWhitelistProfiles(fake); await Fails(async () => { await service.ListAsync(); }, CloudProfileFailure.HttpError, "HTTP " + (int)status + " is a real error"); }
using (var fake = new HttpClient(new RecordingHandler((_, _) => { var response = Response("{}", HttpStatusCode.Forbidden); response.Headers.Add("X-RateLimit-Remaining", "0"); return Task.FromResult(response); })))
using (var service = new CloudWhitelistProfiles(fake)) await Fails(async () => { await service.ListAsync(); }, CloudProfileFailure.RateLimited, "GitHub rate limiting is shown honestly");
var redirectCalls = 0;
using (var fake = new HttpClient(new RecordingHandler((_, _) => { redirectCalls++; var response = Response("", HttpStatusCode.Redirect); response.Headers.Location = new Uri("https://evil.invalid/profile.json"); return Task.FromResult(response); })))
using (var service = new CloudWhitelistProfiles(fake)) { await Fails(async () => { await service.ListAsync(); }, CloudProfileFailure.RedirectBlocked, "foreign redirects are rejected before the next request"); Check(redirectCalls == 1, "blocked redirects never reach another host"); }
using (var cancelled = new CancellationTokenSource())
using (var fake = new HttpClient(new RecordingHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response("[]"); })))
using (var service = new CloudWhitelistProfiles(fake)) { cancelled.Cancel(); try { await service.ListAsync(cancelled.Token); throw new Exception("FAIL cancellation"); } catch (OperationCanceledException) { checks++; } }

using (var fake = new HttpClient(new RecordingHandler((_, _) => throw new TaskCanceledException("controlled client timeout"))))
using (var service = new CloudWhitelistProfiles(fake)) await Fails(async () => { await service.ListAsync(); }, CloudProfileFailure.Timeout, "client timeouts are not reported as an empty directory");
using (var fake = new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response("[]")))))
{
    fake.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "TEST-NOT-A-SECRET");
    using var service = new CloudWhitelistProfiles(fake);
    await Fails(async () => { await service.ListAsync(); }, CloudProfileFailure.InvalidResponse, "injected default credentials are rejected");
}
using (var fake = new HttpClient(new RecordingHandler((_, _) => { var response = Response("[]"); response.Content.Headers.ContentLength = 3 * RuleFileCodec.MaximumFileBytes; return Task.FromResult(response); })))
using (var service = new CloudWhitelistProfiles(fake)) await Fails(async () => { await service.ListAsync(); }, CloudProfileFailure.TooLarge, "advertised envelope lengths are bounded before reading");
await ListingFailure(new string(' ', 2 * RuleFileCodec.MaximumFileBytes + 1), CloudProfileFailure.TooLarge, "streamed response bodies are bounded");

var badUtf8 = new byte[] { 0xff, 0xfe, 0xff }; var badDigest = BlobSha(badUtf8);
using (var fake = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/shared.json", StringComparison.Ordinal)
    ? Content(badUtf8, badDigest) : Listing(Item(sha: badDigest, size: badUtf8.Length)))))))
using (var service = new CloudWhitelistProfiles(fake)) { var entry = (await service.ListAsync())[0]; await Fails(async () => { await service.ReadAsync(entry); }, CloudProfileFailure.InvalidResponse, "raw rule UTF8 rejects invalid bytes after SHA verification"); }

var templateBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures-input", "kangqi-default.json"));
var templateRules = RuleFileCodec.Parse(Encoding.UTF8.GetString(templateBytes)); var templateSha = BlobSha(templateBytes);
Check(templateRules.Count == 7 && templateRules.All(rule => rule.Enabled && rule.IncludeDescendants && rule.Kind == RuleKind.Application) &&
    templateRules.Select(rule => rule.Value).OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(new[] { "known:clash", "known:codex", "known:huorong", "known:qq", "known:steam", "known:translucenttb", "known:uu" }), "real community template contains exactly seven portable application rules");
Check(RuleFileCodec.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures-input", "default-rules.json"))).Count == 0, "shipped default rules remain empty");
using (var fake = new HttpClient(new RecordingHandler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath.EndsWith("/kangqi-default.json", StringComparison.Ordinal)
    ? Content(templateBytes, templateSha, "kangqi-default.json") : Listing(Item(file: "kangqi-default.json", sha: templateSha, size: templateBytes.Length)))))))
using (var service = new CloudWhitelistProfiles(fake)) { var entry = (await service.ListAsync())[0]; Check((await service.ReadAsync(entry)).Rules.SequenceEqual(templateRules), "the real published template passes byte SHA and portable preview validation"); }

var fixtureRoot = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? throw new Exception("TEMP must be explicitly set on E"), "cloud-profile-" + Guid.NewGuid().ToString("N"));
Check(Path.GetPathRoot(fixtureRoot)!.Equals("E:\\", StringComparison.OrdinalIgnoreCase), "store fixtures stay on E");
try
{
    var store = new WhitelistProfilesStore(fixtureRoot); var original = store.Load();
    Check(original.ActiveRules.Count == 0 && WhitelistStore.CreateDefaults().Count == 0, "new local defaults remain empty until an explicit import");
    var imported = store.ImportProfile("Community", preview.Rules, original.Revision);
    Check(imported.Profiles.Count == 2 && imported.ActiveProfile.Name == "Community" && imported.ActiveRules.SequenceEqual(rules), "one import atomically creates and activates its original rules");
    Check(imported.Profiles.Single(p => p.Id == original.ActiveId).Rules.Count == 0, "existing profile remains intact");
    var savedBytes = File.ReadAllBytes(store.FilePath);
    try { store.ImportProfile("Stale", rules, original.Revision); throw new Exception("FAIL stale revision"); } catch (InvalidDataException) { Check(File.ReadAllBytes(store.FilePath).SequenceEqual(savedBytes), "stale imports preserve exact original bytes"); }
    try { store.ImportProfile("Community", rules, imported.Revision); throw new Exception("FAIL duplicate name"); } catch (InvalidDataException) { Check(File.ReadAllBytes(store.FilePath).SequenceEqual(savedBytes), "duplicate names never leave an empty partial profile"); }
    using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { store.ImportProfile("Locked", rules, imported.Revision); throw new Exception("FAIL atomic replace lock"); }
        catch (IOException) { Check(File.ReadAllBytes(store.FilePath).SequenceEqual(savedBytes), "a native atomic replace failure preserves the original document"); }
    }
    Check(store.Load().Profiles.Count == 2 && !Directory.EnumerateFiles(fixtureRoot, ".whitelist-*.tmp").Any(), "failed atomic import leaves no empty profile or temporary document");
    using (WhitelistProfileOperationGate.EnterClose())
    {
        try { store.ImportProfile("DuringClose", rules, imported.Revision); throw new Exception("FAIL close gate"); }
        catch (InvalidOperationException) { Check(File.ReadAllBytes(store.FilePath).SequenceEqual(savedBytes), "an active close operation blocks imports without a partial write"); }
    }
    var inactive = store.ImportProfile("Inactive", rules, imported.Revision, activate: false);
    Check(inactive.ActiveId == imported.ActiveId && inactive.Profiles.Single(p => p.Name == "Inactive").Rules.SequenceEqual(rules), "inactive import is also one complete transaction");
    var fourth = store.ImportProfile("Fourth", rules, inactive.Revision); var fifth = store.ImportProfile("Fifth", rules, fourth.Revision); savedBytes = File.ReadAllBytes(store.FilePath);
    try { store.ImportProfile("Sixth", rules, fifth.Revision); throw new Exception("FAIL maximum count"); } catch (InvalidDataException) { Check(File.ReadAllBytes(store.FilePath).SequenceEqual(savedBytes) && store.Load().Profiles.Count == 5, "max five failure leaves no sixth or empty profile"); }
    var invalidDirectory = Path.Combine(fixtureRoot, "invalid"); var invalidStore = new WhitelistProfilesStore(invalidDirectory);
    try { invalidStore.ImportProfile("Invalid", new[] { rules[0] with { Value = "" } }, "missing"); throw new Exception("FAIL invalid rules"); } catch (InvalidDataException) { Check(!Directory.Exists(invalidDirectory), "invalid rules fail before creating local storage"); }
}
finally { if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true); }

Console.WriteLine($"PASS cloud whitelist profiles: {checks} checks | fake public HTTP only | atomic E-only profile imports");
internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    internal sealed record Observed(Uri Uri, string Method, bool Auth, bool Cookie, string Accept, string ApiVersion);
    internal List<Observed> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Requests.Add(new(request.RequestUri!, request.Method.Method, request.Headers.Authorization is not null, request.Headers.Contains("Cookie"), string.Join(",", request.Headers.Accept), request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) ? versions.Single() : "")); return send(request, token); }
}
