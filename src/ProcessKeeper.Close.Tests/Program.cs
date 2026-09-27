using ProcessKeeper.Core;

L.Language = "zh-Hans";
var checks = 0;
void Check(bool condition, string label)
{ if (!condition) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
ProtectionDecision Allow(ProcessRecord _) => new(false, "fixture");
var timing = new SteamShutdownTiming(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250));
FakeHost Host() => new();
SteamShutdownService Service(FakeHost host) => new(host, timing);
try
{
    var h = Host(); h.ExitAt = TimeSpan.FromSeconds(3);
    var result = await Service(h).CloseAsync(h.Original, Allow);
    Check(result.Results.All(r => r.Success), "delayed normal exit succeeds only after client and helpers exit");
    Check(h.Clock >= TimeSpan.FromSeconds(4), "save-like delay is followed by a full stable interval");
    Check(h.Requests == 1 && h.Launchers.Single().Name == "steam.exe", "one shutdown request for entire group, never one request per helper");
    Check(h.ActiveLeases == 0 && result.PendingForce.Count == 0, "all verification handles released after success");
    Check(result.Results.All(r => r.Outcome.Contains("不代表")), "success does not promise saved games or completed cloud sync");

    h = Host(); h.ExitAt = null;
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(!result.Results.Any(r => r.Success) && result.PendingForce.Count == 2 && h.Requests == 1, "running game or sync-like timeout needs separate force confirmation");
    Check(h.Clock == timing.ExitTimeout && h.ActiveLeases == 0, "timeout is bounded and releases identity handles");
    var closer = new ProcessCloser(Service(h), h.Capture);
    h = Host(); h.ExitAt = null; closer = new ProcessCloser(Service(h), h.Capture);
    var batch = await closer.CloseDetailedAsync(h.Original, true, Allow, h.Capture());
    Check(batch.SteamPendingForce.Count == 2 && batch.Results.All(r => !r.Success) && h.Requests == 1,
        "batch force checkbox never forces a specialized Steam group");

    h = Host();
    result = await Service(h).CloseAsync([h.Original[1]], Allow);
    Check(!result.RequestSent && h.Requests == 0 && result.PendingForce.Count == 1, "partial helper selection does not shut down unselected client");
    h = Host();
    result = await Service(h).CloseAsync([h.Original[0]], Allow);
    Check(!result.RequestSent && h.Requests == 0, "partial main selection does not shut down unselected helpers");
    h = Host(); h.SnapshotTransform = (items, count) => count >= 2 ? [..items, FakeHost.Helper(203)] : items;
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(h.Requests == 0 && !result.Results.Any(r => r.Success), "new helper before launch blocks frozen group without expansion");
    h = Host(); h.SnapshotTransform = (items, _) => [..items, FakeHost.MainProcess(205) with { Path = @"D:\OtherSteam\steam.exe" }];
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(h.Requests == 0, "ambiguous multiple clients never receive a global shutdown command");

    foreach (var changed in new Func<ProcessRecord, ProcessRecord>[] {
        p => p with { StartTimeUtcTicks = p.StartTimeUtcTicks + 1 },
        p => p with { Path = @"C:\Other\steam.exe" },
        p => p with { OwnerSid = "S-1-5-18" },
        p => p with { SessionId = 22 },
        p => p with { IsSystem = true },
        p => p with { Services = [new("Steam service", "Fixture", p.Id)] },
        p => p with { StartTimeUtcTicks = 0 },
        p => p with { Path = @"C:\Steam\steamapps\common\fake\steam.exe" } })
    {
        h = Host();
        result = await Service(h).CloseAsync([changed(h.Original[0]), h.Original[1]], Allow);
        Check(h.Requests == 0 && result.Results.All(r => !r.Success), "changed or unsupported identity fails closed");
    }
    h = Host(); result = await Service(h).CloseAsync(h.Original, _ => new(true, "protected"));
    Check(h.Requests == 0 && result.PendingForce.Count == 0, "initial protection blocks group and any force suggestion");
    h = Host(); var checksOfProtection = 0;
    result = await Service(h).CloseAsync(h.Original, _ => new(++checksOfProtection >= 3, "changed"));
    Check(h.Requests == 0 && h.ActiveLeases == 0, "protection is checked again immediately before launch");
    h = Host(); h.ExitAt = null;
    result = await Service(h).CloseAsync(h.Original, _ => new(h.Requests > 0, "changed after request"));
    Check(h.Requests == 1 && result.PendingForce.Count == 0 && !result.Results.Any(r => r.Success), "protection change after shutdown request cancels further action");

    h = Host(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    result = await Service(h).CloseAsync(h.Original, Allow, cancelled.Token);
    Check(h.Requests == 0 && !result.RequestSent && result.PendingForce.Count == 0, "cancel before request is side-effect free");
    h = Host(); h.ExitAt = null; using var waiting = new CancellationTokenSource(); h.OnDelay = waiting.Cancel;
    result = await Service(h).CloseAsync(h.Original, Allow, waiting.Token);
    Check(h.Requests == 1 && result.RequestSent && result.PendingForce.Count == 0 && h.ActiveLeases == 0, "cancel while waiting does not force or promise rollback");

    h = Host(); h.RejectSignature = true;
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(h.Requests == 0 && result.PendingForce.Count == 2 && h.ActiveLeases == 0, "untrusted executable is never launched; frozen force choice remains explicit");
    h = Host(); h.FailValidateAt = 3;
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(h.Requests == 0 && result.Results.All(r => !r.Success), "last-moment native identity revalidation prevents launch");

    h = Host(); h.ExitAt = TimeSpan.FromMilliseconds(500); h.RespawnAt = TimeSpan.FromMilliseconds(750);
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(h.Requests == 1 && !result.Results.Any(r => r.Success) && result.RespawnedProcesses.Count == 1, "helper respawn is observed without retrying shutdown or killing new PID");
    Check(result.PendingForce.All(p => p.Id != 303), "respawn never enters the frozen force confirmation list");
    h = Host(); h.ExitAt = TimeSpan.FromMilliseconds(500); h.RespawnAt = TimeSpan.FromMilliseconds(750); h.RespawnExitAt = TimeSpan.FromSeconds(2);
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(!result.Results.Any(r => r.Success) && result.RespawnedProcesses.Count == 1 && h.Clock >= TimeSpan.FromSeconds(3), "brief respawn resets stability and is still reported after it exits");
    h = Host(); h.ExitAt = TimeSpan.FromSeconds(1); h.IncludeOwnRequest = true;
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(result.Results.All(r => r.Success) && result.RespawnedProcesses.Count == 0, "owned -shutdown request process is waited for but not misclassified as respawn");
    h = Host(); h.SnapshotTransform = (items, _) => h.Requests > 0 ? [] : items; h.ExitAt = null;
    result = await Service(h).CloseAsync(h.Original, Allow);
    Check(!result.Results.Any(r => r.Success), "missing snapshot metadata cannot imply exit while original identities remain alive");

    h = Host(); var baseline = h.Capture(); var old = h.Original[1]; var newborn = old with { Id = 999, StartTimeUtcTicks = old.StartTimeUtcTicks + 10 };
    ProcessSnapshot Snapshot(params ProcessRecord[] processes) => new(DateTimeOffset.UtcNow, processes, []);
    Check(ProcessRespawnObserver.Find([old], baseline, Snapshot(newborn)).Count == 1, "generic observer detects exact-path same-user/session new PID");
    Check(ProcessRespawnObserver.Find([old], baseline, Snapshot(old)).Count == 0, "existing frozen identity is not a respawn");
    Check(ProcessRespawnObserver.Find([old], Snapshot(old, newborn), Snapshot(newborn)).Count == 0, "pre-existing unselected sibling is not a respawn");
    Check(ProcessRespawnObserver.Find([old], baseline, Snapshot(newborn with { OwnerSid = "other" })).Count == 0, "other user's process is not matched");
    Check(ProcessRespawnObserver.Find([old], baseline, Snapshot(newborn with { SessionId = 2 })).Count == 0, "other session's process is not matched");
    Check(ProcessRespawnObserver.Find([old], baseline, Snapshot(newborn with { Path = @"D:\Elsewhere\steamwebhelper.exe" })).Count == 0, "same name in another install is not matched");
    Check(ProcessRespawnObserver.Find([old], baseline, Snapshot(old with { StartTimeUtcTicks = old.StartTimeUtcTicks + 10 })).Count == 1, "PID reuse is observed as a new identity without action");

    var absent = FakeHost.MainProcess(int.MaxValue) with { Name = "fixture-closed.exe", Path = @"C:\Fixture\fixture-closed.exe", ApplicationKey = "fixture" };
    var replacement = absent with { Id = 9001, StartTimeUtcTicks = absent.StartTimeUtcTicks + 100 };
    var captures = 0;
    closer = new ProcessCloser(Service(Host()), () => ++captures == 1 ? Snapshot(absent) : Snapshot(replacement));
    batch = await closer.CloseDetailedAsync([absent], false, Allow, Snapshot(absent));
    Check(batch.Results.All(r => !r.Success) && batch.RespawnedProcesses.Single().Id == replacement.Id,
        "ordinary close batch marks observed replacement as incomplete without closing it");
    absent = FakeHost.MainProcess(int.MaxValue);
    replacement = absent with { Id = 9002, StartTimeUtcTicks = absent.StartTimeUtcTicks + 100 };
    captures = 0;
    closer = new ProcessCloser(Service(Host()), () => ++captures == 1 ? Snapshot(absent) : Snapshot(replacement));
    var forcedResults = await closer.ForceConfirmedAsync([absent], Allow);
    Check(forcedResults.All(r => !r.Success) && forcedResults.Single().Outcome.Contains("9002"),
        "explicit force path reports replacement PID after its own stability observation");
    var serviceRecord = absent with { Name = "steamservice.exe", Path = @"C:\Steam\steamservice.exe" };
    forcedResults = await closer.ForceConfirmedAsync([serviceRecord], Allow);
    Check(forcedResults.All(r => !r.Success), "explicit force path rejects the Steam service name too");

    Check(SteamExecutableTrust.IsValveOrganization("Valve Corp.") && SteamExecutableTrust.IsValveOrganization("Valve Corporation"), "exact Valve organization spellings accepted");
    Check(!SteamExecutableTrust.IsValveOrganization("Valve Corp. Evil") && !SteamExecutableTrust.IsValveOrganization("Microsoft Corporation"), "publisher substring and unrelated trusted signer rejected");
    var fixtureRoot = Path.GetFullPath(args.Single()); Directory.CreateDirectory(fixtureRoot);
    var fakeExe = Path.Combine(fixtureRoot, "unsigned-steam.exe"); File.WriteAllBytes(fakeExe, [0x4d, 0x5a, 0, 0]);
    bool rejected = false; try { SteamExecutableTrust.Validate(fakeExe); } catch (Exception) { rejected = true; }
    Check(rejected, "real Windows trust API rejects owned unsigned executable without running it");
    Console.WriteLine($"{checks} checks passed. No real Steam process, service, game, UAC, or executable launch was used.");
}
catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }

sealed class FakeHost : ISteamShutdownHost
{
    public string CurrentUserSid => "S-1-5-21-fixture";
    public int CurrentSessionId => 11;
    public TimeSpan Clock { get; private set; }
    public TimeSpan? ExitAt { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan? RespawnAt { get; set; }
    public TimeSpan? RespawnExitAt { get; set; }
    public bool IncludeOwnRequest { get; set; }
    public bool RejectSignature { get; set; }
    public int FailValidateAt { get; set; }
    public int Requests, Captures, Validations, ActiveLeases;
    public List<ProcessRecord> Launchers { get; } = [];
    public Action? OnDelay { get; set; }
    public Func<ProcessRecord[], int, ProcessRecord[]>? SnapshotTransform { get; set; }
    public ProcessRecord[] Original { get; } = [MainProcess(201), Helper(202)];
    public static ProcessRecord MainProcess(int id) => new() { Id = id, StartTimeUtcTicks = 638900000000000000 + id,
        OwnerSid = "S-1-5-21-fixture", SessionId = 11, Name = "steam.exe", Path = @"C:\Steam\steam.exe", ApplicationKey = "known:steam" };
    public static ProcessRecord Helper(int id) => MainProcess(id) with { Name = "steamwebhelper.exe", Path = @"C:\Steam\bin\cef\steamwebhelper.exe" };
    private ProcessRecord[] Current()
    {
        var items = Requests == 0 || ExitAt is null || Clock < ExitAt ? Original.ToList() : [];
        if (Requests > 0 && IncludeOwnRequest && (ExitAt is null || Clock < ExitAt)) items.Add(MainProcess(401));
        if (Requests > 0 && RespawnAt is { } respawn && Clock >= respawn && (RespawnExitAt is null || Clock < RespawnExitAt)) items.Add(Helper(303));
        return items.ToArray();
    }
    public ProcessSnapshot Capture()
    { var items = Current(); Captures++; return new(DateTimeOffset.UtcNow, SnapshotTransform?.Invoke(items, Captures) ?? items, []); }
    public IDisposable HoldVerifiedProcess(ProcessRecord process) { ValidateProcess(process); ActiveLeases++; return new Lease(this); }
    public IDisposable HoldExecutable(ProcessRecord launcher)
    { if (RejectSignature) throw new InvalidOperationException("untrusted fixture"); ActiveLeases++; return new Lease(this); }
    public void ValidateProcess(ProcessRecord process)
    {
        if (++Validations == FailValidateAt || !Current().Any(p => ProcessRespawnObserver.SameIdentity(p, process)))
            throw new InvalidOperationException("fixture identity mismatch");
    }
    public bool HasExited(ProcessRecord process) => !Current().Any(p => ProcessRespawnObserver.SameIdentity(p, process));
    public ProcessRecord? RequestShutdown(ProcessRecord launcher) { Requests++; Launchers.Add(launcher); return IncludeOwnRequest ? MainProcess(401) : null; }
    public Task Delay(TimeSpan interval, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Clock += interval; OnDelay?.Invoke(); return Task.CompletedTask; }
    private sealed class Lease(FakeHost host) : IDisposable { public void Dispose() => host.ActiveLeases--; }
}
