using System.Reflection;
using System.Text.Json;
using ProcessKeeper.Core;

// Synthetic queries and isolated files only. Shared by modern and x86 Framework tests.
internal static class ReleaseBoundaryVerification
{
    internal static void Run(Action<bool, string> Check)
    {
        void Run(string name, Action test) => test();
Run("incomplete backup does not hide another recoverable entry", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
    var store = new AutorunBackupStore(path);
    var locator = new AutorunLocator { Hive = "HKCU", View = 32, Path = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Fixture" };
    var original = new AutorunState(true, true, "fixture raw command");
    var entry = new AutorunEntry { Id = AutorunIdentity.Id(AutorunSourceKind.RegistryRun, locator), SourceKind = AutorunSourceKind.RegistryRun,
        Locator = locator, Fingerprint = original.Fingerprint };
    var record = new AutorunBackupRecord(entry, original, new(false, false, ""));
    store.Save(record);
    var badId = new string('A', 64); var corrupt = Path.Combine(path, badId + ".json");
    foreach (var bad in new[] { "{}", "{\"Entry\":null}", JsonSerializer.Serialize(record with { Original = null! }),
        JsonSerializer.Serialize(record with { Disabled = null! }), JsonSerializer.Serialize(record with { Entry = entry with { Locator = null! } }) })
    {
        File.WriteAllText(corrupt, bad);
        bool rejected = false; try { store.Load(badId); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "incomplete backup is reported as invalid data");
        Check(store.ReadAll().SequenceEqual(new[] { record }), "other backup remains available");
        Check(File.ReadAllText(corrupt) == bad, "corrupt backup preserved");
    }
});
Run("portable asset file name uses an absolute end anchor", () =>
{
    var method = typeof(UpdateService).GetMethod("PortableAssetName", BindingFlags.Static | BindingFlags.NonPublic)!;
    foreach (var name in new[] { "ProcessKeeper.exe\n", "ProcessKeeper.exe\r", "ProcessKeeper.exe\0", "ProcessKeeper.exe\t" })
        Check(!(bool)method.Invoke(null, new object[] { name })!, "control suffix is rejected");
    Check((bool)method.Invoke(null, new object[] { "ProcessKeeper-1.5.1.exe" })!, "ordinary versioned exe remains valid");
});
Run("timed out provider work cannot accumulate", () =>
{
    var method = typeof(SpecialWindowHost).GetMethod("QueryVirtualMachine", BindingFlags.Static | BindingFlags.NonPublic)!;
    VirtualMachineTarget? Query(Func<VirtualMachineTarget?> query, int timeout = 200) {
        try { return (VirtualMachineTarget?)method.Invoke(null, new object[] { query, TimeSpan.FromMilliseconds(timeout) }); }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
    using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
    int calls = 0; bool timedOut = false;
    try {
        try { Query(() => { Interlocked.Increment(ref calls); entered.Set(); release.Wait(); ended.Set(); return null; }); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut && entered.Wait(2000), "caller times out while provider remains blocked");
        for (int i = 0; i < 6; i++) {
            bool busy = false; try { Query(() => { Interlocked.Increment(ref calls); return null; }); } catch (TimeoutException) { busy = true; }
            Check(busy, "busy provider returns an explicit timeout");
        }
        Check(calls == 1, "retries never queue or create another provider worker");
    } finally { release.Set(); ended.Wait(5000); }
    var expected = new VirtualMachineTarget("fixture", "fixture", 1);
    bool recovered = false;
    for (int i = 0; i < 100 && !recovered; i++) { try { recovered = Query(() => expected) == expected; } catch (TimeoutException) { Thread.Sleep(10); } }
    Check(recovered, "provider slot recovers when original worker actually exits");
    bool failedQuery = false; try { Query(() => throw new InvalidDataException("fixture")); } catch (InvalidDataException) { failedQuery = true; }
    Check(failedQuery && Query(() => expected) == expected, "failed provider releases the slot and preserves failure");
});

    }
}
