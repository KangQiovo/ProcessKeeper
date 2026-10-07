using ProcessKeeper.Core;

internal static class AppCacheCleanupVerification
{
    internal static async Task RunStartup(Action<bool, string> check)
    {
        var fields = new[] { "PKLC1", new string('a', 32), "", new string('a', 64), "1.8.0", "", new string('a', 64), "", new string('a', 64), "123", "1", "" };
        var context = new LauncherContext(fields, Path.GetTempPath());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try { await AppCacheCleanupService.ClearPendingPayloadsAfterStartupAsync(context, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "cancelled startup cleanup refuses before any helper or protected-path access");
        var prior = Environment.GetEnvironmentVariable("PROCESSKEEPER_LAUNCH_CONTEXT");
        try
        {
            Environment.SetEnvironmentVariable("PROCESSKEEPER_LAUNCH_CONTEXT", null);
            var result = await AppCacheCleanupService.ClearPendingPayloadsAfterStartupAsync(context, CancellationToken.None);
            check(!result.Success && result.RemovedFiles == 0 && result.RemovedBytes == 0, "untrusted startup context cannot start helper or invent removals");
        }
        finally { Environment.SetEnvironmentVariable("PROCESSKEEPER_LAUNCH_CONTEXT", prior); }
        using var duringWait = new CancellationTokenSource();
        int waits = 0, requests = 0;
        cancelled = false;
        try
        {
            AppCacheCleanupService.WaitForPendingCleanupExit(milliseconds =>
            {
                check(milliseconds == 250, "startup cleanup waits with a short cancellation-aware interval");
                if (++waits == 2) duringWait.Cancel();
                return waits == 4;
            }, () => requests++, duringWait.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && requests == 1 && waits == 4, "startup cancellation signals once and waits for owned worker exit without killing it");
        waits = 0;
        AppCacheCleanupService.WaitForPendingCleanupExit(_ => ++waits == 2, () => throw new Exception("unexpected cancellation"), CancellationToken.None);
        check(waits == 2, "uncancelled cleanup waits for native completion before parsing counts");
    }
    internal static void Run(Action<bool, string> check)
    {
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant" })
        {
            L.Language = language;
            var result = AppCacheCleanupService.ParseResponse("PKCACHESTATUS1\ncompleted\n120\n10485760\n2\n");
            check(result.Success && result.RemovedFiles == 120 && result.RemovedBytes == 10485760 && result.RetainedTrees == 2, "cache cleanup reports actual bounded counts " + language);
            check(result.Message.Contains("10.00 MiB"), "cache cleanup displays precise released size " + language);
            var empty = AppCacheCleanupService.ParseResponse("PKCACHESTATUS1\ncompleted\n0\n0\n0\n");
            check(empty.Success && empty.RemovedFiles == 0 && empty.Message.Length > 0, "cache cleanup zero is successful without invented deletion " + language);
        }
        foreach (var response in new[] { "", "PKCACHESTATUS1\ncompleted\n-1\n0\n0\n", "PKCACHESTATUS1\ncompleted\n1\n17179869185\n0\n", "PKCACHESTATUS1\ncompleted\n1\n1\n0\nextra", "PKCACHESTATUS1\nfailed\n0\n0\n0\n", "PKCACHESTATUS1\ncompleted\n1\n1\n-1\n", new string('a', 1025) })
        {
            bool refused = false;
            try { AppCacheCleanupService.ParseResponse(response); } catch (InvalidDataException) { refused = true; }
            check(refused, "cache cleanup rejects malformed, failure or unbounded responses");
        }
        L.Language = "en";
    }
}
