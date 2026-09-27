using ProcessKeeper.Core;

internal enum OldExitBehavior { ExitAll, NeverExit, EngineOnly, LauncherOnly }

/// <summary>No method reads or mutates any OS process, file, port, window, or user configuration.</summary>
internal sealed class FakeAvdHost : IAvdRestartHost
{
    internal const string AvdName = "Pixel_Fixture_API_35";
    internal const int Port = 5554;
    internal const string LauncherPath = @"C:\FixtureSdk\emulator\emulator.exe";
    internal const string EnginePath = @"C:\FixtureSdk\emulator\qemu\windows-x86_64\qemu-system-x86_64-headless.exe";
    internal const string GuiPath = @"C:\FixtureSdk\emulator\qemu\windows-x86_64\qemu-system-x86_64.exe";
    internal const string UserProfile = @"C:\FixtureUser";
    internal const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    internal ProcessRecord OldLauncher { get; set; }
    internal ProcessRecord OldEngine { get; set; }
    internal ProcessRecord NewLauncher { get; set; }
    internal ProcessRecord NewEngine { get; set; }
    internal IReadOnlyList<string> SourceArguments { get; set; }
    internal IReadOnlyDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = UserProfile,
        ["ANDROID_AVD_HOME"] = UserProfile + @"\.android\avd"
    };
    internal string OldAvdName { get; set; } = AvdName;
    internal string NewAvdName { get; set; } = AvdName;
    internal string CurrentHash { get; set; } = Hash;
    internal HashSet<string> MissingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool RefuseShutdown { get; set; }
    internal bool FailLaunch { get; set; }
    internal OldExitBehavior ExitBehavior { get; set; } = OldExitBehavior.ExitAll;
    internal bool RetainPortAfterExit { get; set; }
    internal IReadOnlyList<AvdPortListener> AdditionalListenersAfterExit { get; set; } = [];
    internal bool SecondListenerOwner { get; set; }
    internal bool OmitListener { get; set; }
    internal int? ListenerOwnerOverride { get; set; }
    internal bool StartedRootHasExited { get; set; }
    internal long? StartedRootExitTime { get; set; }
    internal Func<int, IReadOnlyList<ProcessRecord>>? NewCapture { get; set; }
    internal IReadOnlyList<ProcessRecord> AdditionalOldProcesses { get; set; } = [];
    internal Dictionary<int, IReadOnlyList<string>> ArgumentOverrides { get; } = [];
    internal Action? OnShutdown { get; set; }
    internal Task? ShutdownGate { get; set; }
    internal Action? OnLaunch { get; set; }
    internal Action<int>? OnCaptureAfterLaunch { get; set; }
    internal int ShutdownCount { get; private set; }
    internal int LaunchCount { get; private set; }
    internal int NameReadCount { get; private set; }
    internal int CapturesAfterLaunch { get; private set; }
    internal int DisposeCount { get; private set; }
    internal List<string> Operations { get; } = [];
    internal List<DateTimeOffset> CaptureTimes { get; } = [];
    internal AvdRestartPlan? LastLaunchedPlan { get; private set; }
    private bool _shutdown;
    private bool _launched;

    internal FakeAvdHost()
    {
        long start = DateTime.UtcNow.AddMinutes(-2).Ticks;
        OldLauncher = Process(12001, LauncherPath, start);
        OldEngine = Process(12002, EnginePath, start + TimeSpan.TicksPerSecond) with
        { ParentId = OldLauncher.Id, ParentIdentityVerified = true };
        NewLauncher = Process(22001, LauncherPath, DateTime.UtcNow.Ticks);
        NewEngine = Process(22002, GuiPath, NewLauncher.StartTimeUtcTicks + TimeSpan.TicksPerMillisecond) with
        {
            ParentId = NewLauncher.Id, ParentIdentityVerified = true,
            Windows = [GuiWindow()]
        };
        SourceArguments = [LauncherPath, "-avd", AvdName, "-port", Port.ToString(), "-no-window"];
    }

    internal static ProcessRecord Process(int pid, string path, long created) => new()
    {
        Id = pid, StartTimeUtcTicks = created, Path = path, Name = Path.GetFileName(path),
        SessionId = 1, OwnerSid = "S-1-5-21-111-222-333-1001", ApplicationKey = "known:avd",
        ApplicationName = "Android Emulator", ProductName = "Android Emulator"
    };

    internal static WindowRecord GuiWindow(nint handle = 4001) => new(handle,
        $"Android Emulator | {AvdName}:{Port}", true, false, false, false)
    {
        ClassName = "Qt6QWindowIcon", IsTopLevel = true, HasUsableBounds = true,
        HasCaption = true, HasAppWindowStyle = true
    };

    public ProcessSnapshot Capture()
    {
        IReadOnlyList<ProcessRecord> processes;
        if (_launched)
        {
            CapturesAfterLaunch++;
            CaptureTimes.Add(DateTimeOffset.UtcNow);
            OnCaptureAfterLaunch?.Invoke(CapturesAfterLaunch);
            processes = NewCapture?.Invoke(CapturesAfterLaunch) ?? [NewLauncher, NewEngine];
        }
        else if (!_shutdown || ExitBehavior == OldExitBehavior.NeverExit)
            processes = new[] { OldLauncher, OldEngine }.Concat(AdditionalOldProcesses).ToArray();
        else if (ExitBehavior == OldExitBehavior.EngineOnly) processes = [OldLauncher];
        else if (ExitBehavior == OldExitBehavior.LauncherOnly) processes = [OldEngine];
        else processes = [];
        return new ProcessSnapshot(DateTimeOffset.UtcNow, processes, []);
    }

    public IReadOnlyList<string> ReadArguments(ProcessRecord process)
    {
        if (ArgumentOverrides.TryGetValue(process.Id, out var arguments)) return arguments;
        if (process.Id == OldLauncher.Id) return SourceArguments;
        if (process.Id == OldEngine.Id) return [OldEngine.Path, "-avd", OldAvdName, "-port", Port.ToString(), "-no-window"];
        return [process.Path, "-avd", AvdName, "-port", Port.ToString()];
    }

    public IReadOnlyDictionary<string, string> ReadEnvironment(ProcessRecord process) => Environment;

    public void ValidateIdentity(ProcessRecord process)
    {
        var candidates = _launched ? new[] { NewLauncher, NewEngine } : new[] { OldLauncher, OldEngine }.Concat(AdditionalOldProcesses);
        if (!candidates.Any(current => IdentityEquals(current, process)))
            throw new InvalidOperationException("Fixture identity changed.");
        if (!_launched && _shutdown && ExitBehavior == OldExitBehavior.ExitAll)
            throw new InvalidOperationException("Fixture old identity exited.");
    }

    internal static bool IdentityEquals(ProcessRecord left, ProcessRecord right) =>
        left.Id == right.Id && left.StartTimeUtcTicks == right.StartTimeUtcTicks &&
        left.Path.Equals(right.Path, StringComparison.OrdinalIgnoreCase) &&
        left.SessionId == right.SessionId && left.OwnerSid == right.OwnerSid;

    public IReadOnlyList<AvdPortListener> ReadListeners()
    {
        if (OmitListener) return [];
        if (_shutdown && !_launched && ExitBehavior is OldExitBehavior.ExitAll or OldExitBehavior.EngineOnly)
            return RetainPortAfterExit ? [new(Port, 32001, false) { Address = "127.0.0.1" }, .. AdditionalListenersAfterExit] : AdditionalListenersAfterExit;
        int owner = ListenerOwnerOverride ?? (_launched ? NewEngine.Id : OldEngine.Id);
        var result = new List<AvdPortListener> { new(Port, owner, false) { Address = "127.0.0.1" } };
        if (SecondListenerOwner) result.Add(new(Port, 32001, true) { Address = "::1" });
        return result;
    }

    public Task<string> GetAvdNameAsync(ProcessRecord process, int port, string userProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NameReadCount++;
        return Task.FromResult(_launched ? NewAvdName : OldAvdName);
    }

    public Task ShutdownAsync(ProcessRecord process, int port, string avdName, string userProfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShutdownCount++;
        Operations.Add("shutdown");
        if (RefuseShutdown) throw new InvalidOperationException("Fixture graceful shutdown refused.");
        _shutdown = true;
        OnShutdown?.Invoke();
        return ShutdownGate ?? Task.CompletedTask;
    }

    public string FileHash(string path) => CurrentHash;
    public bool FileExists(string path) => !MissingFiles.Contains(path);

    public IAvdStartedProcess Launch(AvdRestartPlan plan)
    {
        LaunchCount++;
        Operations.Add("launch");
        if (FailLaunch) throw new IOException("Fixture launch failed.");
        LastLaunchedPlan = plan;
        _launched = true;
        OnLaunch?.Invoke();
        return new Started(this);
    }

    private sealed class Started(FakeAvdHost owner) : IAvdStartedProcess
    {
        private bool _disposed;
        public ProcessRecord Identity => owner.NewLauncher;
        public bool HasExited => owner.StartedRootHasExited;
        public long? ExitTimeUtcTicks => owner.StartedRootExitTime;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.DisposeCount++;
            owner.Operations.Add("dispose-root-handle");
        }
    }
}
