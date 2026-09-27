using ProcessKeeper.Launcher;

internal static class StartupElevationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var calls = new List<string>();
        var accepted = StartupElevationFlow.Run(false,
            () => { calls.Add("request"); return BrokerReply.Started; },
            admin => { calls.Add(admin ? "admin-cache" : "permission-page"); return 19; });
        check(accepted == 0 && calls.SequenceEqual(["request"]),
            "normal startup requests UAC before extracting any cache and skips the access page after acceptance");

        calls.Clear();
        var elevated = StartupElevationFlow.Run(true,
            () => throw new InvalidOperationException("An elevated child must not request UAC again."),
            admin => { calls.Add(admin ? "admin-cache" : "permission-page"); return 23; });
        check(elevated == 23 && calls.SequenceEqual(["admin-cache"]),
            "administrator startup and the elevated child go directly to the protected application cache without another UAC request");

        foreach (var reply in new[] { BrokerReply.Cancelled, BrokerReply.Failed, BrokerReply.Unavailable })
        {
            calls.Clear();
            var fallback = StartupElevationFlow.Run(false,
                () => { calls.Add("request"); return reply; },
                admin => { calls.Add(admin ? "admin-cache" : "permission-page"); return 29; });
            check(fallback == 29 && calls.SequenceEqual(["request", "permission-page"]),
                $"{reply} shows the non-elevated permission page once without another automatic prompt");
        }

        calls.Clear();
        try
        {
            StartupElevationFlow.Run(false,
                () => { calls.Add("request"); return BrokerReply.Cancelled; },
                _ => throw new IOException("fixture access-page launch failed"));
            check(false, "access-page failure cannot be mistaken for a successful launch");
        }
        catch (IOException exception) when (exception.Message == "fixture access-page launch failed")
        {
            check(calls.SequenceEqual(["request"]),
                "access-page startup failure propagates to the startup error handler without another UAC prompt");
        }
    }
}
