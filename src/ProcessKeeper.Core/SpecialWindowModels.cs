namespace ProcessKeeper.Core;

public enum SpecialWindowKind { TrayApplication, VirtualBox, HyperV, VMware }
public sealed record VirtualMachineTarget(string Id, string Name, int ProcessId);
public sealed record SpecialWindowPlan(SpecialWindowKind Kind, string Name, string Explanation,
    ProcessRecord Source, IReadOnlyList<string> SourceArguments, string ExecutablePath,
    IReadOnlyList<string> Arguments, string TargetId, string TargetTitle, string ExecutableHash);
public sealed record SpecialWindowDiscovery(IReadOnlyList<SpecialWindowPlan> Plans, IReadOnlyList<string> Notices);
public sealed record SpecialWindowResult(bool Success, string Message, bool LaunchRequested,
    ProcessRecord? Process = null, WindowRecord? Window = null);
public sealed record SpecialWindowTiming(TimeSpan WindowTimeout, TimeSpan PollInterval, int StableSamples = 3)
{
    public static SpecialWindowTiming Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500));
}

public interface ISpecialWindowHost
{
    ProcessSnapshot Capture();
    void ValidateSource(ProcessRecord source, bool hyperV);
    IReadOnlyList<string> ReadArguments(ProcessRecord source);
    bool FileExists(string path);
    bool IsGuiExecutable(string path);
    string FileHash(string path);
    VirtualMachineTarget? ReadVirtualMachine(ProcessRecord source, SpecialWindowKind kind, string targetId);
    void Launch(SpecialWindowPlan plan);
}
