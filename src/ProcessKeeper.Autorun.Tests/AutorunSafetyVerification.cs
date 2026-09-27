using ProcessKeeper.Core;

internal static class AutorunSafetyVerification
{
    internal static void Run(Action<bool, string> check)
    {
        VerifyNativeFolderSafety(check);
        var original = new AutorunState(true, true, "{\"Kind\":2,\"Value\":\"%APPDATA%\\\\Vendor\\\\client.exe --literal %TOKEN% | \\u4e2d\\u6587\"}");
        var locator = new AutorunLocator { Hive = "HKCU", View = 32, Path = @"Software\Microsoft\Windows\CurrentVersion\RunOnce", ValueName = "!*Fixture Startup" };
        var entry = new AutorunEntry { Id = AutorunIdentity.Id(AutorunSourceKind.RegistryRunOnce, locator), SourceKind = AutorunSourceKind.RegistryRunOnce,
            Locator = locator, Name = "Fixture Startup", CanChange = true, Enabled = true, Fingerprint = original.Fingerprint };
        var disabled = new AutorunState(false, false, "");
        AutorunEntry Refresh(AutorunEntry value, AutorunState state) => value with { Enabled = state.Enabled, Fingerprint = state.Fingerprint };

        {
            var taskState = new AutorunState(true, false, "fixture task XML including actions, triggers and principal");
            var taskLocator = new AutorunLocator { Path = @"\Fixture\Task" };
            var taskEntry = entry with { Id = AutorunIdentity.Id(AutorunSourceKind.ScheduledTask, taskLocator),
                SourceKind = AutorunSourceKind.ScheduledTask, Locator = taskLocator, Enabled = false, Fingerprint = taskState.Fingerprint };
            var backend = new MemoryBackend(taskState);
            var store = new MemoryBackups();
            var manager = new AutorunManager(backend, store);
            var result = manager.ChangeEnabled(taskEntry, true);
            check(result.Success && !result.RestartRequired && backend.State == taskState with { Enabled = true } &&
                store.Record?.Original == taskState && store.Saves == 1,
                "an externally disabled task can be enabled after backing up its exact definition and disabled state");
            result = manager.ChangeEnabled(Refresh(taskEntry, backend.State), false);
            check(result.Success && backend.State == taskState && store.Record?.Original.Enabled == true && store.Saves == 2,
                "task toggling changes only Enabled and preserves actions, triggers and principal in both directions");
        }

        foreach (var kind in new[] { AutorunSourceKind.PolicyRun, AutorunSourceKind.Driver, AutorunSourceKind.AdvancedRegistry, AutorunSourceKind.WmiSubscription, AutorunSourceKind.PackagedStartup })
        {
            var backend = new MemoryBackend(original);
            var store = new MemoryBackups();
            var result = new AutorunManager(backend, store).ChangeEnabled(entry with { SourceKind = kind }, false);
            check(!result.Success && backend.Reads == 0 && backend.Writes == 0 && store.Saves == 0, "restricted startup source cannot mutate even when caller sets CanChange: " + kind);
        }
        foreach (var blocked in new[] { entry with { IsSystem = true }, entry with { CanChange = false } })
        {
            var backend = new MemoryBackend(original);
            check(!new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(blocked, false).Success && backend.Reads == 0,
                "system and read-only entries are rejected before native reads or writes");
        }
        {
            var backend = new MemoryBackend(original with { Payload = "external replacement" });
            var store = new MemoryBackups();
            check(!new AutorunManager(backend, store).ChangeEnabled(entry, false).Success && backend.Writes == 0 && store.Saves == 0,
                "a stale snapshot cannot disable an externally replaced entry");
        }
        {
            var backend = new MemoryBackend(original);
            var store = new MemoryBackups { SaveError = new IOException("fixture backup full") };
            check(!new AutorunManager(backend, store).ChangeEnabled(entry, false).Success && backend.Writes == 0 && backend.State == original,
                "backup failure leaves native startup state untouched");
        }
        {
            var backend = new MemoryBackend(original);
            var replacement = original with { Payload = "changed while backup was saved" };
            var store = new MemoryBackups { AfterSave = () => backend.State = replacement };
            check(!new AutorunManager(backend, store).ChangeEnabled(entry, false).Success && backend.Writes == 0 && backend.State == replacement,
                "an edit during backup is caught by the second state check");
        }
        {
            var backend = new MemoryBackend(original);
            var replacement = original with { Payload = "changed immediately before native mutation" };
            backend.BeforeApply = () => backend.State = replacement;
            check(!new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false).Success && backend.Writes == 0 && backend.State == replacement,
                "the final backend compare prevents overwriting a concurrent edit after confirmation");
        }
        {
            var backend = new MemoryBackend(original);
            var store = new MemoryBackups();
            var manager = new AutorunManager(backend, store);
            var result = manager.ChangeEnabled(entry, false);
            check(result.Success && result.RestartRequired && backend.State == disabled && store.Record?.Original == original,
                "disable persists an exact original snapshot before changing only startup configuration");
            check(store.Record!.Entry.Locator == locator && store.Record.Original.Payload == original.Payload,
                "backup retains the registry view, !/* RunOnce name, value kind, unexpanded variables and Unicode payload");
            check(manager.ChangeEnabled(Refresh(entry, backend.State), false).Success && backend.Writes == 1 && store.Saves == 1,
                "repeated disable is idempotent and does not overwrite the recovery snapshot");
            result = manager.ChangeEnabled(Refresh(entry, backend.State), true);
            check(result.Success && backend.State == original && backend.Writes == 2,
                "enable restores the exact original configuration instead of reconstructing a command");
            check(manager.ChangeEnabled(Refresh(entry, backend.State), true).Success && backend.Writes == 2,
                "repeated enable performs no additional write");
        }
        {
            var state = original with { Enabled = null };
            var backend = new MemoryBackend(state);
            check(!new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(Refresh(entry, state), false).Success && backend.Writes == 0,
                "an unknown enabled state cannot be treated as enabled");
        }
        {
            var backend = new MemoryBackend(original) { DisabledFactory = _ => original };
            check(!new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false).Success && backend.Writes == 0,
                "a malformed disable plan is rejected before backup or mutation");
        }
        var disabledEntry = Refresh(entry, disabled);
        var validBackup = new AutorunBackupRecord(entry, original, disabled);
        var invalidBackups = new (string Name, AutorunBackupRecord? Backup)[]
        {
            ("missing original backup", null),
            ("different entry ID", validBackup with { Entry = entry with { Id = new string('0', 64) } }),
            ("different source kind", validBackup with { Entry = entry with { SourceKind = AutorunSourceKind.RegistryRun } }),
            ("different registry view", validBackup with { Entry = entry with { Locator = locator with { View = 64 } } }),
            ("different registry path", validBackup with { Entry = entry with { Locator = locator with { Path = @"Software\Other\Run" } } }),
            ("different value name", validBackup with { Entry = entry with { Locator = locator with { ValueName = "Other Entry" } } }),
            ("different disabled snapshot", validBackup with { Disabled = disabled with { Payload = "different disabled state" } }),
            ("missing original value", validBackup with { Original = original with { Exists = false } }),
            ("disabled original state", validBackup with { Original = original with { Enabled = false } }),
            ("tampered original command", validBackup with { Original = original with { Payload = "replaced original command" } })
        };
        foreach (var (name, backup) in invalidBackups)
        {
            var backend = new MemoryBackend(disabled);
            var store = new MemoryBackups { Record = backup };
            check(!new AutorunManager(backend, store).ChangeEnabled(disabledEntry, true).Success && backend.Writes == 0,
                "restoration refuses " + name);
        }
        {
            var token = new CancellationTokenSource(); token.Cancel();
            var backend = new MemoryBackend(original);
            var cancelled = false;
            try { new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false, token.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && backend.Reads == 0 && backend.Writes == 0, "pre-cancelled operation performs no native access");
        }
        {
            var token = new CancellationTokenSource();
            var backend = new MemoryBackend(original);
            var store = new MemoryBackups { AfterSave = token.Cancel };
            var cancelled = false;
            try { new AutorunManager(backend, store).ChangeEnabled(entry, false, token.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && backend.Writes == 0 && store.Record is not null, "cancellation after backup but before mutation leaves the original state unchanged");
        }
        {
            var token = new CancellationTokenSource();
            var backend = new MemoryBackend(original) { AfterApply = token.Cancel };
            var result = new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false, token.Token);
            check(result.Success && backend.State == disabled && !backend.LastReadToken.CanBeCanceled,
                "cancellation after native mutation cannot skip read-back or conceal a completed change");
        }
        {
            var backend = new MemoryBackend(original) { ApplyError = new IOException("fixture failure before write") };
            check(!new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false).Success && backend.State == original,
                "write failure before mutation is reported as unchanged");
        }
        {
            var backend = new MemoryBackend(original) { AfterApply = () => throw new IOException("fixture failure after write") };
            check(new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false).Success && backend.State == disabled,
                "verified final state is authoritative when the native writer throws after committing");
        }
        {
            var backend = new MemoryBackend(original);
            var foreign = original with { Payload = "concurrent third-party edit after write" };
            backend.AfterApply = () => backend.State = foreign;
            check(!new AutorunManager(backend, new MemoryBackups()).ChangeEnabled(entry, false).Success && backend.State == foreign && backend.Writes == 1,
                "post-write conflict preserves the external edit instead of blindly rolling back over it");
        }
    }

    private static void VerifyNativeFolderSafety(Action<bool, string> check)
    {
        // Ordinary text files underneath this test executable only: never an actual Startup directory.
        var fixture = Path.Combine(AppContext.BaseDirectory, "autorun-file-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var path = Path.Combine(fixture, "ordinary-fixture.txt");
        var type = typeof(AutorunWindowsBackend).Assembly.GetType("ProcessKeeper.Core.AutorunFileSafety")!;
        object? Invoke(string method, params object[] arguments)
        {
            try { return type.GetMethod(method, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, arguments); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        AutorunState Read() => (AutorunState)Invoke("Read", path)!;
        var absent = new AutorunState(false, false, "");
        try
        {
            File.WriteAllText(path, "Independent startup restoration fixture | Unicode 中文");
            File.SetCreationTimeUtc(path, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(path, new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc));
            File.SetAttributes(path, FileAttributes.Archive | FileAttributes.Hidden);
            var original = Read();
            Invoke("Apply", path, original, absent);
            check(!File.Exists(path) && Read() == absent, "native fixture removes the exact confirmed file through its handle");
            Invoke("Apply", path, absent, original);
            var restored = Read();
            if (restored.Fingerprint != original.Fingerprint)
            {
                Console.WriteLine("Fixture restore mismatch: exists=" + restored.Exists + " | Files=" + string.Join("|", Directory.EnumerateFiles(fixture).Select(Path.GetFileName)));
                if (restored.Exists)
                {
                    using var left = System.Text.Json.JsonDocument.Parse(original.Payload);
                    using var right = System.Text.Json.JsonDocument.Parse(restored.Payload);
                    Console.WriteLine("Fixture differing fields: " + string.Join("|", left.RootElement.EnumerateObject().Where(item => item.Value.GetRawText() != right.RootElement.GetProperty(item.Name).GetRawText()).Select(item => item.Name)));
                }
            }
            check(restored.Fingerprint == original.Fingerprint,
                "native fixture restores exact bytes, owner and ACL, creation time, write time and attributes");
            if (!restored.Exists) return;
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
            var rejected = false;
            try { Invoke("Apply", path, original, absent); } catch (IOException) { rejected = true; }
            check(rejected && File.Exists(path), "native deletion rejects identical content with changed file metadata");
            var changed = Read();
            using (var edit = new FileStream(path, FileMode.Open, FileAccess.Write))
            { edit.SetLength(0); edit.Write(System.Text.Encoding.UTF8.GetBytes("external edit")); }
            rejected = false;
            try { Invoke("Apply", path, changed, absent); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == "external edit", "native deletion preserves concurrently replaced content");
            rejected = false;
            try { Invoke("Apply", path, absent, original); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == "external edit", "native restore never overwrites an existing file");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            rejected = false;
            try { _ = Read(); } catch (IOException) { rejected = true; }
            check(rejected, "non-restorable file attributes are refused before any disable is offered");
            File.SetAttributes(path, FileAttributes.Normal);
            using var parents = (IDisposable)Invoke("LockParents", path)!;
            rejected = false;
            try { Directory.Move(fixture, fixture + "-replaced"); } catch (IOException) { rejected = true; }
            check(rejected && Directory.Exists(fixture), "ancestor handles prevent directory replacement during an operation");
            if (!rejected) Directory.Move(fixture + "-replaced", fixture);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(fixture)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
            Directory.Delete(fixture);
        }
    }

    private sealed class MemoryBackend(AutorunState initial) : IAutorunBackend
    {
        internal AutorunState State = initial;
        internal int Reads, Writes;
        internal Action? BeforeApply, AfterApply;
        internal Exception? ApplyError;
        internal Func<AutorunState, AutorunState>? DisabledFactory;
        internal CancellationToken LastReadToken;
        public AutorunState Read(AutorunEntry entry, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); LastReadToken = cancellationToken; Reads++; return State; }
        public AutorunState CreateDisabledState(AutorunEntry entry, AutorunState current) => DisabledFactory?.Invoke(current) ?? new(false, false, "");
        public void Apply(AutorunEntry entry, AutorunState expected, AutorunState desired)
        {
            BeforeApply?.Invoke();
            if (State.Fingerprint != expected.Fingerprint) throw new IOException("fixture compare rejected stale state");
            if (ApplyError is not null) throw ApplyError;
            Writes++; State = desired; AfterApply?.Invoke();
        }
    }
    private sealed class MemoryBackups : IAutorunBackupStore
    {
        internal AutorunBackupRecord? Record;
        internal int Saves;
        internal Action? AfterSave;
        internal Exception? SaveError;
        public string Save(AutorunBackupRecord record)
        { if (SaveError is not null) throw SaveError; Saves++; Record = record; AfterSave?.Invoke(); return "synthetic-backup"; }
        public AutorunBackupRecord? Load(string id) => Record;
        public IReadOnlyList<AutorunBackupRecord> ReadAll() => Record is null ? [] : [Record];
    }
}
