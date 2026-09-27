using ProcessKeeper.Core;

internal sealed class FakeSpecialWindowHost : ISpecialWindowHost
{
    internal const string Sid = "S-1-5-21-100-200-300-1001";
    internal const string VmId = "703da8ed-e937-4245-a9ea-bc64f8afaf05";
    internal const string OtherVmId = "73b34637-a8bc-42df-bc4e-e62c65f3142a";
    internal const string VmPath = @"C:\Fixture VMs\Alpha\Alpha.vmx";
    internal const long Epoch = 638800000000000000;

    internal List<ProcessRecord> Processes { get; } = [];
    internal Dictionary<int, IReadOnlyList<string>> Arguments { get; } = [];
    internal HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> GuiFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<SpecialWindowPlan> Launches { get; } = [];
    internal Action<ProcessRecord, bool>? OnValidate { get; set; }
    internal Action<SpecialWindowPlan>? OnLaunch { get; set; }
    internal Func<ProcessSnapshot>? OnCapture { get; set; }
    internal Func<ProcessRecord, SpecialWindowKind, string, VirtualMachineTarget?>? OnReadVm { get; set; }
    internal int Captures { get; private set; }
    internal int Validations { get; private set; }

    internal ProcessRecord Add(string name, string? path = null, int id = 501, params string[] arguments)
    {
        path ??= @"C:\Fixture Apps\" + Path.GetFileNameWithoutExtension(name) + "\\" + name;
        var process = new ProcessRecord
        {
            Id = id, Name = name, Path = path, SessionId = 1,
            OwnerSid = Sid, StartTimeUtcTicks = Epoch + id
        };
        Processes.Add(process);
        Arguments[id] = [path, .. arguments];
        AddFile(path);
        return process;
    }

    internal void AddFile(string path, bool gui = true)
    {
        path = Path.GetFullPath(path);
        Files.Add(path);
        if (gui) GuiFiles.Add(path);
        Hashes[path] = "fixture-sha256-" + path;
    }

    internal SpecialWindowPlan Prepare(ProcessRecord source) =>
        SpecialWindowPolicy.Prepare(source, Arguments[source.Id], this) ??
            throw new InvalidOperationException("The fixture expected a launch plan.");

    internal static ProcessSnapshot Snapshot(params ProcessRecord[] processes) => new(DateTimeOffset.UtcNow, processes, []);
    internal static WindowRecord Window(string title = "Fixture main window", nint handle = 101) =>
        new(handle, title, true, false, false, false)
        {
            ClassName = "FixtureMainWindow", IsTopLevel = true, HasUsableBounds = true, HasCaption = true
        };

    public ProcessSnapshot Capture()
    {
        Captures++;
        return OnCapture?.Invoke() ?? Snapshot(Processes.ToArray());
    }

    public void ValidateSource(ProcessRecord source, bool hyperV)
    {
        Validations++;
        OnValidate?.Invoke(source, hyperV);
        var live = Processes.SingleOrDefault(p => p.Id == source.Id);
        if (live is null || live.StartTimeUtcTicks != source.StartTimeUtcTicks ||
            live.SessionId != source.SessionId || live.OwnerSid != source.OwnerSid ||
            !SpecialWindowPolicy.SamePath(live.Path, source.Path))
            throw new InvalidOperationException("The fixture source identity changed.");
    }

    public IReadOnlyList<string> ReadArguments(ProcessRecord source) => Arguments.GetValueOrDefault(source.Id) ?? [];
    public bool FileExists(string path) => Files.Contains(Path.GetFullPath(path));
    public bool IsGuiExecutable(string path) => GuiFiles.Contains(Path.GetFullPath(path));
    public string FileHash(string path) => Hashes.GetValueOrDefault(Path.GetFullPath(path)) ?? throw new InvalidOperationException("Fixture file is missing.");
    public VirtualMachineTarget? ReadVirtualMachine(ProcessRecord source, SpecialWindowKind kind, string targetId) =>
        OnReadVm?.Invoke(source, kind, targetId);

    public void Launch(SpecialWindowPlan plan)
    {
        Launches.Add(plan);
        OnLaunch?.Invoke(plan);
    }
}
