using System.Buffers.Binary;
using ProcessKeeper.Core;

internal static class AutorunApprovalVerification
{
    internal static void Run(Action<bool, string> check)
    {
        check(AutorunStartupApproval.Interpret(null, false) == new AutorunApproval(false, null), "absent Windows approval does not invent a disabled state");
        foreach (var code in new uint[] { 2, 3, 6, 7, 0, 100 })
        {
            var value = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(value, code);
            var actual = AutorunStartupApproval.Interpret(value, true);
            bool? expected = code is 2 or 6 ? true : code is 3 or 7 ? false : null;
            check(actual.HasRecord && actual.Enabled == expected, "Windows startup approval encoding remains explicit or unknown: " + code);
        }
        check(AutorunStartupApproval.Interpret(new byte[2], true).Enabled is null, "short startup approval records stay unknown");
        check(AutorunStartupApproval.Interpret("3", true).Enabled is null, "wrong startup approval value types stay unknown");
        var locator = new AutorunLocator { Path = @"\Fixture\DisabledElsewhere" };
        var state = new AutorunState(true, false, "Full normalized task XML including account, actions and triggers");
        var task = new AutorunEntry { Id = AutorunIdentity.Id(AutorunSourceKind.ScheduledTask, locator), Locator = locator,
            SourceKind = AutorunSourceKind.ScheduledTask, Enabled = false, CanChange = true, Fingerprint = state.Fingerprint };
        var backend = new TaskBackend(state); var store = new MemoryStore();
        var result = new AutorunManager(backend, store).ChangeEnabled(task, true);
        check(result.Success && backend.State.Enabled == true && backend.State.Payload == state.Payload,
            "an externally disabled task can be enabled without changing its definition or guessing startup settings");
        check(store.Saved?.Original == state && store.Saved.Disabled.Enabled == true, "task enable records its exact original disabled state before mutation");
        check(backend.Writes == 1, "task enable applies the Boolean change exactly once");
        var backupFixtureRoot = Path.Combine(Environment.GetEnvironmentVariable("PROCESSKEEPER_AUTORUN_TEST_ROOT") ?? Path.GetTempPath(),
            "approval-backup-fixture-" + Guid.NewGuid().ToString("N"));
        foreach (var kind in new[] { AutorunSourceKind.RegistryRun, AutorunSourceKind.StartupFolder })
        foreach (var code in new uint[] { 2, 3, 6, 7 })
        {
            var raw = new AutorunState(true, true, "Exact original command or startup file bytes");
            var bytes = new byte[12]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, code);
            var approved = new AutorunState(true, code is 2 or 6, "StartupApproved/v1|" + System.Text.Json.JsonSerializer.Serialize(
                new { Original = raw, Approval = new AutorunLocator { Hive = "HKCU", Path = "fixture-only", ValueName = "sample" }, Data = bytes }));
            var row = task with { SourceKind = kind, Enabled = approved.Enabled, Fingerprint = approved.Fingerprint };
            var approvalBackend = new TaskBackend(approved); var approvalStore = new MemoryStore();
            var changed = new AutorunManager(approvalBackend, approvalStore).ChangeEnabled(row, approved.Enabled != true);
            check(changed.Success && approvalBackend.Writes == 1 && approvalBackend.State.Enabled != approved.Enabled,
                "Windows approval state is manageable without an app-owned disable backup: " + kind + " " + code);
            check(approvalStore.Saved?.Original == approved, "exact approval and original startup configuration are backed up first");
            using var json = System.Text.Json.JsonDocument.Parse(approvalBackend.State.Payload.Substring("StartupApproved/v1|".Length));
            check(json.RootElement.GetProperty("Original").GetProperty("Payload").GetString() == raw.Payload,
                "changing approval preserves original startup data without deleting the entry");
            var written = json.RootElement.GetProperty("Data").GetBytesFromBase64();
            check(written.Length == 12 && BinaryPrimitives.ReadUInt32LittleEndian(written) / 4 == code / 4,
                "approval changes preserve known encoding family");
            var blocked = new TaskBackend(approved);
            check(!new AutorunManager(blocked, new MemoryStore()).ChangeEnabled(row with { IsSystem = true }, approved.Enabled != true).Success && blocked.Writes == 0,
                "approval changes cannot bypass system-entry protection");
            var stale = new TaskBackend(approved with { Payload = approved.Payload + " " });
            check(!new AutorunManager(stale, new MemoryStore()).ChangeEnabled(row, approved.Enabled != true).Success && stale.Writes == 0,
                "a concurrent approval/configuration change blocks the write");
            // Production JSON/atomic backup IO, with only a synthetic native backend.
            // Each known encoding is exercised in both directions, including an
            // original state disabled outside ProcessKeeper. No registry is opened.
            var persistedRow = row with { Id = AutorunIdentity.Id(kind, row.Locator) };
            var fixtureDirectory = Path.Combine(backupFixtureRoot, kind + "-" + code);
            var realStore = new AutorunBackupStore(fixtureDirectory);
            var persistedBackend = new TaskBackend(approved);
            var realManager = new AutorunManager(persistedBackend, realStore);
            var firstToggle = realManager.ChangeEnabled(persistedRow, approved.Enabled != true);
            var firstState = persistedBackend.State;
            var firstRecord = new AutorunBackupRecord(persistedRow, approved, firstState);
            check(firstToggle.Success && File.Exists(firstToggle.BackupPath) && new AutorunBackupStore(fixtureDirectory).Load(persistedRow.Id) == firstRecord,
                "real backup reload exactly preserves forward approval transition: " + kind + " " + code);
            check(realStore.Load(persistedRow.Id)!.Original.Payload == approved.Payload && realStore.Load(persistedRow.Id)!.Original.Fingerprint == approved.Fingerprint,
                "real backup retains all original bytes, native configuration and fingerprint");
            var reverseRow = persistedRow with { Enabled = firstState.Enabled, Fingerprint = firstState.Fingerprint };
            var secondToggle = realManager.ChangeEnabled(reverseRow, approved.Enabled == true);
            var reverseRecord = new AutorunBackupRecord(reverseRow, firstState, persistedBackend.State);
            check(secondToggle.Success && persistedBackend.Writes == 2 && new AutorunBackupStore(fixtureDirectory).Load(reverseRow.Id) == reverseRecord,
                "real backup reload exactly preserves reverse approval transition: " + kind + " " + code);
            check(realStore.ReadAll().Count == 1 && realStore.ReadAll()[0] == reverseRecord && !Directory.EnumerateFiles(fixtureDirectory, "*.tmp").Any(),
                "real approval backup enumeration matches exact committed record without temporary leftovers");
        }
        var unknown = new AutorunState(true, false, "StartupApproved/v1|{}");
        var unknownBackend = new TaskBackend(unknown);
        check(!new AutorunManager(unknownBackend, new MemoryStore()).ChangeEnabled(task with { SourceKind = AutorunSourceKind.RegistryRun, Fingerprint = unknown.Fingerprint }, true).Success
            && unknownBackend.Writes == 0, "malformed approval snapshots never mutate configuration");
        foreach (AutorunSourceKind kind in Enum.GetValues(typeof(AutorunSourceKind)))
        {
            var row = new AutorunEntry { SourceKind = kind, CanChange = false };
            check(AutorunPresentation.Matches(row, false, 0) &&
                (AutorunPresentation.Matches(row, false, 1) != AutorunPresentation.Matches(row, false, 2)),
                "simple common/hidden filters partition non-system entries including read-only entries: " + kind);
            check(!AutorunPresentation.Matches(row with { IsSystem = true }, false, 0) &&
                AutorunPresentation.Matches(row with { IsSystem = true }, true, 0), "advanced mode includes protected system entries: " + kind);
        }
    }
    private sealed class TaskBackend(AutorunState state) : IAutorunBackend
    {
        internal AutorunState State = state; internal int Writes;
        public AutorunState Read(AutorunEntry entry, CancellationToken cancellationToken = default) => State;
        public AutorunState CreateDisabledState(AutorunEntry entry, AutorunState current) => current with { Enabled = false };
        public void Apply(AutorunEntry entry, AutorunState expected, AutorunState desired)
        { if (State != expected) throw new InvalidOperationException(); State = desired; Writes++; }
    }
    private sealed class MemoryStore : IAutorunBackupStore
    {
        internal AutorunBackupRecord? Saved;
        public string Save(AutorunBackupRecord backup) { Saved = backup; return "fixture"; }
        public AutorunBackupRecord? Load(string id) => Saved;
        public IReadOnlyList<AutorunBackupRecord> ReadAll() => Saved is null ? [] : [Saved];
    }
}
