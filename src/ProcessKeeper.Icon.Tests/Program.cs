using ProcessKeeper.App;

var passed = 0;
void Check(bool value, string description)
{
    if (!value) throw new InvalidOperationException("FAIL | " + description);
    passed++; Console.WriteLine("PASS | " + description);
}
async Task Until(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!predicate()) await Task.Delay(5, timeout.Token);
}
bool Dispatch(Action action) { _ = Task.Run(action); return true; }

var factoryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var active = 0;
var peak = 0;
var calls = 0;
using (var cache = new BoundedAsyncIconCache<string>(256, 2, 64, async (key, cancellation) =>
{
    var running = Interlocked.Increment(ref active);
    Interlocked.Increment(ref calls);
    int previous;
    do { previous = Volatile.Read(ref peak); if (previous >= running) break; }
    while (Interlocked.CompareExchange(ref peak, running, previous) != previous);
    try { await factoryGate.Task.WaitAsync(cancellation); await Task.Yield(); return key; }
    finally { Interlocked.Decrement(ref active); }
}, Dispatch))
{
    var requests = Enumerable.Range(0, 2000).Select(i => cache.GetAsync("C:\\Apps\\" + i + ".exe")).ToArray();
    await Until(() => Volatile.Read(ref active) == 2);
    Check(cache.Diagnostics is { Active: 2, Queued: 64, InFlight: 66, Cached: 0 }, "burst of 2000 unique rows retains exactly 2 active + 64 queued requests");
    Check(requests.Skip(66).All(t => t.IsFaulted && t.Exception!.InnerException is IconRequestDeferredException), "overflow is an explicit retryable admission failure, not silent loss");
    Check(ReferenceEquals(requests[0], cache.GetAsync("c:\\apps\\0.exe")), "same in-flight path with different casing shares the request");
    Check(ReferenceEquals(requests[10], cache.GetAsync("C:\\Apps\\10.exe")), "same queued key shares one task even when admission is full");
    factoryGate.SetResult();
    await Task.WhenAll(requests.Take(66));
    Check(peak == 2, "whole pipeline, including await/decode stage, never exceeds two operations");
    Check(calls == 66, "rejected requests did not start hidden work");
    Check(await cache.GetAsync("C:\\Apps\\1000.exe") == "C:\\Apps\\1000.exe", "a deferred key succeeds when the visible row retries later");
    var before = calls;
    var cached = cache.GetAsync("C:\\Apps\\1000.exe");
    Check(await cached == "C:\\Apps\\1000.exe" && calls == before, "successful cached key does not decode again");
    for (var i = 2000; i < 5000; i++) await cache.GetAsync(i.ToString());
    Check(cache.Diagnostics is { Cached: 256, Active: 0, Queued: 0, InFlight: 0 }, "3000 completed requests retain at most 256 cached results");
    before = calls;
    await cache.GetAsync("C:\\Apps\\0.exe");
    Check(calls == before + 1, "evicted icons load again rather than becoming permanently absent");
}

var lruCalls = 0;
using (var lru = new BoundedAsyncIconCache<string>(2, 1, 4, (key, _) => { lruCalls++; return Task.FromResult(key); }, action => { action(); return true; }))
{
    await lru.GetAsync("a"); await lru.GetAsync("b"); await lru.GetAsync("A"); await lru.GetAsync("c");
    Check(lruCalls == 3, "cache access promotes case-insensitive LRU entry");
    await lru.GetAsync("a");
    Check(lruCalls == 3, "recent entry survives eviction");
    await lru.GetAsync("b");
    Check(lruCalls == 4, "least-recent entry is evicted");
}

var attempts = 0;
using (var failing = new BoundedAsyncIconCache<string>(4, 1, 4, (key, _) => ++attempts == 1
    ? Task.FromException<string>(new IOException("fixture failure")) : Task.FromResult(key), Dispatch))
{
    try { await failing.GetAsync("retry"); throw new Exception("Expected failure"); } catch (IOException) { }
    Check(await failing.GetAsync("retry") == "retry" && attempts == 2, "failed work is removed and can be retried");
}

var allowDispatch = false;
using (var undispatched = new BoundedAsyncIconCache<string>(4, 1, 2, (key, _) => Task.FromResult(key), action =>
{ if (!allowDispatch) return false; action(); return true; }))
{
    try { await undispatched.GetAsync("lost-dispatcher"); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
    Check(undispatched.Diagnostics is { Active: 0, Queued: 0, InFlight: 0 }, "rejected owner dispatch leaves no hanging request");
    allowDispatch = true;
    Check(await undispatched.GetAsync("lost-dispatcher") == "lost-dispatcher", "dispatcher rejection is not permanently cached");
}

var nativeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var started = 0;
var disposing = new BoundedAsyncIconCache<string>(4, 2, 8, async (key, _) =>
{ Interlocked.Increment(ref started); await nativeGate.Task; return key; }, Dispatch);
var cancelledRequests = Enumerable.Range(0, 10).Select(i => disposing.GetAsync(i.ToString())).ToArray();
await Until(() => Volatile.Read(ref started) == 2);
disposing.Dispose();
Check(cancelledRequests.All(t => t.IsCanceled), "window disposal immediately cancels both active callers and all pending callers");
Check(disposing.Diagnostics is { Cached: 0, Queued: 0, InFlight: 0 }, "window disposal drops cached and queued references");
Check(disposing.GetAsync("after-close").IsCanceled, "new requests after disposal cannot restart work");
nativeGate.SetResult();
await Until(() => disposing.Diagnostics.Active == 0);
Check(disposing.Diagnostics.Cached == 0 && started == 2, "late native completion cannot repopulate cache or dispatch pending work");
disposing.Dispose();
Check(true, "disposal is idempotent");
Console.WriteLine($"Icon scheduler fixture | {passed} assertions passed | 0 skipped | native heap crash not reproduced by this fixture.");
