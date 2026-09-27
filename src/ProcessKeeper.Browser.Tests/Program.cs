using System.Net;
using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

var suite = new FixtureSuite();

await suite.Case("owned loopback CDP produces repeated real image frames with screenshot-only commands", async () =>
{
    await using var browser = new MockBrowser();
    var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
    var found = await service.DiscoverAsync([host.Source]);
    suite.Check(found.Targets.Count == 1 && found.Notices.Count == 0, "one verified page");
    BrowserPageTarget page = found.Targets.Single();
    suite.Check(page.Id == "page-1" && page.Source == host.Source && page.Port == browser.Port, "source and page identity pinned");
    await using var session = await service.ConnectAsync(page);
    var first = await session.CaptureFrameAsync(); var second = await session.CaptureFrameAsync();
    suite.Check(first.SequenceEqual(MockBrowser.Png) && second.SequenceEqual(first), "image bytes reached caller");
    suite.Check(browser.Commands.SequenceEqual(new[] { "Page.captureScreenshot", "Page.captureScreenshot" }), "no script, activation, navigation or browser lifecycle commands");
    suite.Check(host.ValidationCalls >= 12, "identity repeatedly revalidated");
});

await suite.Case("port zero discovers only selected process listeners", async () =>
{
    await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port);
    host.Arguments = [host.Source.Path, "--headless", "--remote-debugging-port=0"];
    var result = await new ChromiumLiveService(host).DiscoverAsync([host.Source]);
    suite.Check(result.Targets.Count == 1 && result.Targets[0].Port == browser.Port, "ephemeral declared port resolved by ownership");
});

foreach (string mode in new[] { "redirect-version", "redirect-list", "external-page", "wrong-port", "workers-only", "duplicate-page", "oversized-list" })
    await suite.Case("discovery rejects " + mode, async () =>
    {
        await using var browser = new MockBrowser { Mode = mode }; var host = new FixtureHost(browser.Port);
        var result = await new ChromiumLiveService(host).DiscoverAsync([host.Source]);
        suite.Check(result.Targets.Count == 0 && result.Notices.Count > 0, "unsafe response fails closed");
        suite.Check(!browser.Requests.Contains("/redirect-sentinel"), "HTTP redirect never followed");
        suite.Check(result.Notices.All(message => !message.Contains("fixture-secret")), "notices omit sensitive URL data");
    });

foreach (string invalid in new[] { "no-cdp", "child", "pipe", "visible", "duplicate-port", "bad-port", "public-address" })
    await suite.Case("arguments reject " + invalid + " before HTTP", async () =>
    {
        await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port);
        string[] valid = host.Arguments.ToArray();
        host.Arguments = invalid switch
        {
            "no-cdp" => [host.Source.Path, "--headless"],
            "child" => [.. valid, "--type=renderer"],
            "pipe" => [.. valid, "--remote-debugging-pipe"],
            "visible" => [host.Source.Path, "--remote-debugging-port=" + browser.Port],
            "duplicate-port" => [.. valid, "--remote-debugging-port=0"],
            "bad-port" => [host.Source.Path, "--headless", "--remote-debugging-port=-1"],
            "public-address" => [.. valid, "--remote-debugging-address=0.0.0.0"],
            _ => throw new InvalidOperationException()
        };
        var result = await new ChromiumLiveService(host).DiscoverAsync([host.Source]);
        suite.Check(result.Targets.Count == 0 && browser.Requests.IsEmpty, "argument rejection performs no network access");
    });

foreach (string binding in new[] { "0.0.0.0", "::", "192.0.2.1", "changed-owner", "shared-port" })
    await suite.Case("listener ownership rejects " + binding + " before HTTP", async () =>
    {
        await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port);
        host.Listeners = binding switch
        {
            "changed-owner" => [new(browser.Port, 42043, false) { Address = "127.0.0.1" }],
            "shared-port" => [new(browser.Port, 42042, false) { Address = "127.0.0.1" }, new(browser.Port, 42043, true) { Address = "::1" }],
            _ => [new(browser.Port, 42042, binding.Contains(':')) { Address = binding }]
        };
        var result = await new ChromiumLiveService(host).DiscoverAsync([host.Source]);
        suite.Check(result.Targets.Count == 0 && browser.Requests.IsEmpty, "untrusted binding performs no HTTP");
    });

await suite.Case("current-user identity rejection is sanitized and has no network side effect", async () =>
{
    await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port) { IdentityValid = false };
    var result = await new ChromiumLiveService(host).DiscoverAsync([host.Source]);
    suite.Check(result.Targets.Count == 0 && browser.Requests.IsEmpty, "foreign or stale source rejected");
    suite.Check(result.Notices.All(message => !message.Contains("fixture-secret")), "host error detail is not exposed");
});

await suite.Case("connect rejects PID reuse before requesting a page", async () =>
{
    await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
    var target = (await service.DiscoverAsync([host.Source])).Targets.Single(); int requests = browser.Requests.Count;
    host.IdentityValid = false;
    await suite.Throws(() => service.ConnectAsync(target), "identity changed");
    suite.Check(browser.Requests.Count == requests && browser.Commands.IsEmpty, "no connect after identity loss");
});

await suite.Case("WebSocket handshake redirects are disabled", async () =>
{
    await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
    var target = (await service.DiscoverAsync([host.Source])).Targets.Single(); browser.Mode = "redirect-websocket";
    await suite.Throws(() => service.ConnectAsync(target), "websocket redirect");
    suite.Check(!browser.Requests.Contains("/redirect-sentinel"), "WebSocket redirect never followed");
});

foreach (string change in new[] { "identity", "ownership", "arguments" })
    await suite.Case("frame capture revalidates " + change, async () =>
    {
        await using var browser = new MockBrowser(); var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
        var target = (await service.DiscoverAsync([host.Source])).Targets.Single();
        await using var session = await service.ConnectAsync(target);
        if (change == "identity") host.IdentityValid = false;
        if (change == "ownership") host.OwnershipValid = false;
        if (change == "arguments") host.Arguments = [.. host.Arguments, "--fixture-changed"];
        await suite.Throws(() => session.CaptureFrameAsync(), "capture evidence changed");
        suite.Check(browser.Commands.IsEmpty, "no screenshot after source evidence changes");
    });

foreach (string mode in new[] { "huge-image", "invalid-base64", "wrong-id", "remote-error", "oversized-frame" })
    await suite.Case("capture rejects " + mode + " without leaking endpoint detail", async () =>
    {
        await using var browser = new MockBrowser { Mode = mode }; var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
        var target = (await service.DiscoverAsync([host.Source])).Targets.Single();
        await using var session = await service.ConnectAsync(target);
        Exception error = await suite.Throws(() => session.CaptureFrameAsync(), "unsafe frame response");
        suite.Check(!error.ToString().Contains("fixture-secret"), "remote error and malformed image text omitted");
    });

await suite.Case("bounded unsolicited CDP event does not displace matching screenshot", async () =>
{
    await using var browser = new MockBrowser { Mode = "event-first" }; var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
    var target = (await service.DiscoverAsync([host.Source])).Targets.Single();
    await using var session = await service.ConnectAsync(target);
    suite.Check((await session.CaptureFrameAsync()).SequenceEqual(MockBrowser.Png), "matching frame survives event");
});

await suite.Case("cancellation interrupts a pending WebSocket screenshot and disposal only closes our connection", async () =>
{
    await using var browser = new MockBrowser { Mode = "stall" }; var host = new FixtureHost(browser.Port); var service = new ChromiumLiveService(host);
    var target = (await service.DiscoverAsync([host.Source])).Targets.Single();
    var session = await service.ConnectAsync(target); using var cancellation = new CancellationTokenSource();
    var pending = session.CaptureFrameAsync(cancellation.Token);
    await browser.ScreenshotRequested.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
    Exception error = await suite.Throws(() => pending, "cancelled capture");
    suite.Check(error is OperationCanceledException, "caller cancellation preserved");
    await session.DisposeAsync(); await session.DisposeAsync();
    suite.Check(browser.Commands.SequenceEqual(new[] { "Page.captureScreenshot" }), "dispose sent no page/browser close command");
    suite.Check((await service.DiscoverAsync([host.Source])).Targets.Count == 1, "fixture browser endpoint still running");
});

await suite.Case("fixture shutdown waits for the cancelled accept loop before stopping its listener", async () =>
{
    var acceptDraining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseAccept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var browser = new MockBrowser(async () => { acceptDraining.SetResult(); await releaseAccept.Task; });
    var disposal = browser.DisposeAsync().AsTask();
    try
    {
        await acceptDraining.Task.WaitAsync(TimeSpan.FromSeconds(2));
        suite.Check(browser.ListenerIsBound, "listener remains bound while cancellation continuation drains");
    }
    finally { releaseAccept.TrySetResult(); await disposal; }
    suite.Check(!browser.ListenerIsBound, "listener is closed after the accept loop completes");
});

await suite.Case("endpoint policy rejects external hosts, userinfo, fragments, query, encoded paths and wrong page IDs", () =>
{
    foreach (string url in new[]
    {
        "ws://example.invalid:9222/devtools/page/a", "ws://user@127.0.0.1:9222/devtools/page/a",
        "ws://127.0.0.1:9222/devtools/page/a#fragment", "ws://127.0.0.1:9222/devtools/page/a?token=secret",
        "ws://127.0.0.1:9222/devtools/page/%61", "ws://127.0.0.1:9222/devtools/page/b",
        "wss://127.0.0.1:9222/devtools/page/a", "ws://127.0.0.1:9223/devtools/page/a"
    })
    {
        bool rejected = false;
        try { ChromiumLivePolicy.ValidateWebSocket(url, 9222, "/devtools/page/a"); }
        catch (InvalidOperationException) { rejected = true; }
        suite.Check(rejected, "untrusted WebSocket URI rejected");
    }
    suite.Check(ChromiumLivePolicy.RequireBinding([new(9222, 42, true) { Address = "::1" }], 9222, 42).Equals(IPAddress.IPv6Loopback), "literal IPv6 loopback supported");
    return Task.CompletedTask;
});

suite.Finish();

internal sealed class FixtureSuite
{
    private int _cases, _checks, _failures;
    internal async Task Case(string name, Func<Task> action)
    {
        _cases++;
        try { await action().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failures++; Console.WriteLine("FAIL " + name + " | " + error.Message); }
    }
    internal void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
    internal async Task<Exception> Throws(Func<Task> action, string message)
    {
        _checks++;
        try { await action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure: " + message);
    }
    internal void Finish()
    {
        Console.WriteLine($"Browser fixture tests: {_cases} cases, {_checks} checks, {_failures} failures. Only self-owned loopback fixtures were used.");
        Environment.ExitCode = _failures == 0 ? 0 : 1;
    }
}
