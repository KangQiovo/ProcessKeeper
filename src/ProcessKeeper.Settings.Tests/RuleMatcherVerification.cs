using ProcessKeeper.Core;

internal static class RuleMatcherVerification
{
    internal static void Run(Action<bool, string> check)
    {
        ProcessRecord P(int id, int parent = 0, long start = 100, string key = "known:other") => new()
        {
            Id = id, ParentId = parent, StartTimeUtcTicks = start, SessionId = 1, OwnerSid = "S-1-5-21-fixture",
            Name = $"fixture{id}.exe", Path = $@"C:\Fixture\fixture{id}.exe", ApplicationKey = key
        };
        RuleProcessMatcher Matcher(params ProcessRecord[] processes) => new(new ProcessSnapshot(DateTimeOffset.UtcNow, processes, []));
        var root = P(40, key: "known:codex");
        var child = P(41, 40, 200);
        var grandchild = P(42, 41, 300);
        var unrelated = P(43, 0, 400);
        var rule = new WhitelistRule { Id = "default-codex", Name = "Codex", Kind = RuleKind.Application, Value = "known:codex", IncludeDescendants = true };
        var matches = Matcher(root, child, grandchild, unrelated).Match(rule);
        check(matches.Select(m => m.Process.Id).SequenceEqual([40, 41, 42]), "rule matching includes direct and verified multigeneration children only");
        check(matches[0] is { IsInherited: false, MatchedAncestorId: null }, "direct rule match has no invented ancestor");
        check(matches[1] is { IsInherited: true, MatchedAncestorId: 40 } && matches[2].MatchedAncestorId == 40, "inherited match names the actual matching ancestor");
        check(Matcher(root, child).Match(rule with { Enabled = false }).Count == 2, "disabled rule can preview associations without enabling itself");
        check(Matcher(root, child).Match(rule with { IncludeDescendants = false }).Count == 1, "descendant opt out limits preview to direct matches");
        check(Matcher(root, child with { StartTimeUtcTicks = 50 }).Match(rule).Count == 1, "PID reuse with younger parent cannot create inherited association");
        check(Matcher(root, child with { StartTimeUtcTicks = 0 }).Match(rule).Count == 1, "unknown creation time cannot establish inherited association");
        check(Matcher(root, child with { OwnerSid = "S-1-5-21-other" }).Match(rule).Count == 1, "different owner does not inherit rule association");
        check(Matcher(root, child with { SessionId = 2 }).Match(rule).Count == 1, "different session does not inherit rule association");
        check(Matcher(root, child with { Path = "relative.exe" }).Match(rule).Count == 1, "relative path cannot establish inherited association");
        check(Matcher(root, child with { OwnerSid = "" }).Match(rule).Count == 1, "missing owner cannot establish inherited association");
        check(Matcher(root, root with { ApplicationKey = "other" }, child).Match(rule).Count == 1, "ambiguous parent PID is never chosen for inheritance");
        check(Matcher(root, child with { ParentId = 999 }).Match(rule).Count == 1, "exited or absent parent does not imply inheritance");
        var avd = P(44, 40, 200, "known:avd") with { Name = "emulator.exe" };
        var avdWorker = P(45, 44, 300);
        check(Matcher(root, avd, avdWorker).Match(rule).Count == 1, "default Codex rule excludes AVD and its descendants");
        check(Matcher(root, avd, avdWorker).Match(rule with { Id = "custom-codex" }).Count == 3, "explicit custom descendant rule keeps its requested scope");
        var dock = P(46, 40, 200, "known:mydock");
        check(Matcher(root, dock).Match(rule).Count == 1, "default Codex rule excludes MyDock identity");
        var systemChild = child with { IsSystem = true };
        check(Matcher(root, systemChild).Match(rule).Count == 2, "system process rule association remains available to explicit system view");
        var policy = new ProtectionPolicy(1, root.OwnerSid, 999);
        var snapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, [root, systemChild], []);
        check(policy.Evaluate(systemChild, snapshot, [rule]).RuleId is null, "system association does not override independent system protection");
        check(!policy.Evaluate(child, new ProcessSnapshot(DateTimeOffset.UtcNow, [root, child], []), [rule with { Enabled = false }]).Protected,
            "disabled association preview never enables actual process protection");
        var byName = rule with { Kind = RuleKind.ProcessName, Value = "FIXTURE41.EXE", IncludeDescendants = false };
        check(Matcher(root, child).Match(byName).Single().Process.Id == child.Id, "process name preview uses case insensitive exact identity");
        var byPath = rule with { Kind = RuleKind.ExecutablePath, Value = @"c:\fixture\fixture41.exe", IncludeDescendants = false };
        check(Matcher(root, child).Match(byPath).Single().Process.Id == child.Id, "executable preview uses normalized full path");
        var directory = rule with { Kind = RuleKind.Directory, Value = @"C:\Fixture", IncludeDescendants = false };
        check(Matcher(root, child with { Path = @"C:\FixtureOther\file.exe" }).Match(directory).Count == 1, "directory preview respects directory boundary");
        var cycleA = P(50, 51, 500);
        var cycleB = P(51, 50, 500);
        check(Matcher(root, cycleA, cycleB).Match(rule).Count == 1, "parent cycle terminates without invented association");
        var deep = Enumerable.Range(0, 140).Select(index => P(1000 + index, index == 0 ? 0 : 999 + index,
            1000 + index, index == 0 ? "known:codex" : "known:other")).ToArray();
        check(new RuleProcessMatcher(new ProcessSnapshot(DateTimeOffset.UtcNow, deep, [])).Match(rule).Count == 129,
            "rule association search keeps the same 128 ancestor depth limit as protection");
    }
}
