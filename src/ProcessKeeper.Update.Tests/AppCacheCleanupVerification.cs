using ProcessKeeper.Core;

internal static class AppCacheCleanupVerification
{
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
