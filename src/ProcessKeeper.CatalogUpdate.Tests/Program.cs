using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcessKeeper.Core;

internal static class Program
{
    static int _checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    static async Task Reject(Func<Task> action, string name) { try { await action(); } catch (Exception) { Check(true, name); return; } throw new Exception("Accepted: " + name); }
    static byte[] Payload(long revision, Action<JsonObject>? modify = null)
    {
        var root = JsonNode.Parse("""
            {"schema":1,"repository":"KangQiovo/ProcessKeeper","revision":2026093100,"version":"2026.09.30.3","publishedUtc":"2026-09-29T00:00:00Z","checkedOn":"2026-09-30","matching":"exact-name-v1","rules":[{"id":"example-watch","basis":"watchlist","names":["Example PUA"],"reason":{"en":"A user review preference; not a malware verdict.","zh-Hans":"用户关注名单，仅供核对。","zh-Hant":"用戶關注名單，僅供核對。"},"sourceUrl":"","sourcePublishedOn":""}]}
            """)!.AsObject(); root["revision"] = revision; modify?.Invoke(root); return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
    static async Task<int> Main()
    {
        try
        {
            L.Language = "en";
            var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? throw new Exception("Explicit E TEMP required"), "catalog-tests-" + Guid.NewGuid().ToString("N"));
            if (Path.GetPathRoot(Path.GetFullPath(root))!.Equals(@"C:\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Tests require a non-C output directory");
            Directory.CreateDirectory(root);
            using var rsa = CatalogUpdater.CreateRsa(); rsa.KeySize = 3072;
            var key = rsa.ExportParameters(false);
            byte[] Sign(byte[] bytes) => rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            const long revision = CatalogUpdater.BuiltInRevision + 100;
            var bytes = Payload(revision); var transport = new Transport(bytes, Sign(bytes));
            var activated = new List<CatalogData>();
            var updater = new CatalogUpdater(root, transport, key, CatalogUpdater.BuiltInMetadata(), activated.Add);
            await updater.LoadAsync(); Check(updater.Current.BuiltIn && !Directory.Exists(Path.Combine(root, "recommendation-catalog")), "missing cache is read-only and keeps built-ins");
            var offer = (await updater.CheckAsync())!;
            Check(offer.Metadata.Revision == revision && updater.Current.BuiltIn && activated.Count == 0, "check verifies but does not activate or write");
            Check(transport.Requests.SequenceEqual(new[] { CatalogUpdater.OfficialDirectory + "catalog.json", CatalogUpdater.OfficialDirectory + "catalog.sig" }), "fixed official endpoints only");
            await updater.ApplyAsync(offer); var cache = Path.Combine(root, "recommendation-catalog", "verified.catalog"); var saved = File.ReadAllBytes(cache);
            Check(updater.Current.Revision == revision && activated.Count == 1 && File.Exists(cache), "verified higher revision atomically committed then activated");
            var restarted = new CatalogUpdater(root, transport, key, CatalogUpdater.BuiltInMetadata()); await restarted.LoadAsync();
            Check(restarted.Current.Revision == revision && !restarted.Current.BuiltIn, "signed cache reverified on reload");
            Check(await updater.CheckAsync() is null, "same signed revision/hash is a no-op");
            await Reject(() => updater.ApplyAsync(offer), "same offer never reapplied");
            foreach (var mutation in new Action<JsonObject>[] {
                node=>node["schema"]=2, node=>node["repository"]="attacker/other", node=>node["matching"]="regex", node=>node["unknown"]=true,
                node=>node["publishedUtc"]="9999-01-01T00:00:00Z", node=>node["rules"]![0]!["basis"]="virus", node=>node["rules"]![0]!["sourceUrl"]="https://example.invalid",
                node=>node["rules"]![0]!["names"]!.AsArray().Add("example pua"), node=>node["rules"]![0]!["names"]![0]=new string('x',161),
                node=>node["rules"]![0]!["names"]![0]="Example\nPUA", node=>node["rules"]![0]!["reason"]!.AsObject().Remove("en"),
                node=>{node["rules"]![0]!["basis"]="published-report";node["rules"]![0]!["sourceUrl"]="http://www.huorong.cn/report";},
                node=>{node["rules"]![0]!["basis"]="published-report";node["rules"]![0]!["sourceUrl"]="https://www.huorong.cn.evil.invalid/report";}
            })
            { var invalid = Payload(revision + 1, mutation); await Reject(() => Task.Run(() => CatalogUpdater.Verify(invalid, Sign(invalid), key)), "strict signed schema/content refused"); }
            var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"schema\":1", "\"schema\":1,\"schema\":1"));
            await Reject(() => Task.Run(() => CatalogUpdater.Verify(duplicate, Sign(duplicate), key)), "duplicate JSON property refused");
            var badUtf8 = new byte[] { 0xc0, 0xaf }; await Reject(() => Task.Run(() => CatalogUpdater.Verify(badUtf8, Sign(badUtf8), key)), "invalid signed UTF8 refused");
            var tampered = (byte[])bytes.Clone(); tampered[4] ^= 1;
            await Reject(() => Task.Run(() => CatalogUpdater.Verify(tampered, Sign(bytes), key)), "payload tampering refused before JSON");
            await Reject(() => Task.Run(() => CatalogUpdater.Verify(bytes, new byte[384], key)), "forged signature refused");
            await Reject(() => Task.Run(() => CatalogUpdater.Verify(new byte[CatalogUpdater.MaximumPayloadBytes + 1], new byte[384], key)), "oversized payload refused before crypto/parse");
            transport.Set(Payload(revision - 1), rsa); await Reject(async () => { await updater.CheckAsync(); }, "signed older revision refused");
            transport.Set(Payload(revision, n => n["version"]="2026.09.30.99"), rsa); await Reject(async () => { await updater.CheckAsync(); }, "same revision different signed content refused");
            transport.Failure = new IOException("offline fixture"); await Reject(async () => { await updater.CheckAsync(); }, "network failure surfaced"); transport.Failure = null;
            Check(File.ReadAllBytes(cache).SequenceEqual(saved) && updater.Current.Revision == revision, "all failed checks retain previous cache and catalog");

            transport.Set(Payload(revision + 1), rsa); var next = (await updater.CheckAsync())!;
            using (var locked = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Reject(() => updater.ApplyAsync(next), "replacement failure preserves old cache");
            Check(File.ReadAllBytes(cache).SequenceEqual(saved) && updater.Current.Revision == revision && Directory.GetFiles(Path.GetDirectoryName(cache)!, "*.tmp").Length == 0, "atomic failure removes only owned temporary file");
            using (var locked = new FileStream(Path.Combine(Path.GetDirectoryName(cache)!, "update.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await Reject(() => updater.ApplyAsync(next), "cross-process writer lock fails promptly");
            File.WriteAllBytes(cache, new byte[] { 1,2,3 });
            await Reject(() => restarted.LoadAsync(), "corrupt cache does not replace trusted state");
            await updater.ApplyAsync(next);
            Check(updater.Current.Revision == revision + 1, "valid higher signed update repairs corrupt cache");
            File.WriteAllBytes(cache, saved); transport.Set(Payload(revision + 2), rsa); var newest = (await updater.CheckAsync())!;
            await updater.ApplyAsync(newest);
            Check(updater.Current.Revision == revision + 2, "valid higher offer repairs stale signed cache without lowering trusted revision");
            await Reject(() => restarted.ApplyAsync(next), "reread committed higher revision blocks stale offer from another instance");
            var last = File.ReadAllBytes(cache); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Reject(() => updater.ApplyAsync(newest, cancel.Token), "cancelled update never commits");
            Check(File.ReadAllBytes(cache).SequenceEqual(last), "cancellation preserves exact prior envelope");
            var official = CatalogUpdater.Verify(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"catalog.json")), File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"catalog.sig")), CatalogUpdater.PublicKey());
            Check(official.Metadata.Sha256 == CatalogUpdater.BuiltInHash && official.Rules.Count == 68 && official.Rules.Sum(r=>r.Names.Count) == 117, "shipped 68-rule 117-alias catalog verifies with embedded public key and baseline hash");
            Console.WriteLine($"PASS | {_checks} catalog update checks | {Environment.Version} | process {IntPtr.Size*8}-bit");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    sealed class Transport(byte[] payload, byte[] signature) : ICatalogTransport
    {
        public readonly List<string> Requests = new(); public Exception? Failure;
        public void Set(byte[] data, RSA rsa) { payload=data; signature=rsa.SignData(data,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1); }
        public Task<byte[]> DownloadAsync(Uri uri,int maximumBytes,CancellationToken token)
        { token.ThrowIfCancellationRequested(); if(Failure is not null)throw Failure; Requests.Add(uri.AbsoluteUri); var data=uri.AbsoluteUri.EndsWith(".sig",StringComparison.Ordinal)?signature:payload; return Task.FromResult((byte[])data.Clone()); }
    }
}
