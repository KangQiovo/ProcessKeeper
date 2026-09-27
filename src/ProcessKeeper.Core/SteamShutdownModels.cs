namespace ProcessKeeper.Core;

public sealed record CloseBatchReport(IReadOnlyList<CloseResult> Results,
    IReadOnlyList<ProcessRecord> SteamPendingForce, IReadOnlyList<ProcessRecord> RespawnedProcesses);

public sealed record SteamShutdownResult(IReadOnlyList<CloseResult> Results,
    IReadOnlyList<ProcessRecord> PendingForce, IReadOnlyList<ProcessRecord> RespawnedProcesses, bool RequestSent);

public sealed record SteamShutdownTiming(TimeSpan ExitTimeout, TimeSpan StablePeriod, TimeSpan PollInterval)
{
    public static SteamShutdownTiming Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(350));
}

public interface ISteamShutdownHost
{
    string CurrentUserSid { get; }
    int CurrentSessionId { get; }
    TimeSpan Clock { get; }
    ProcessSnapshot Capture();
    IDisposable HoldVerifiedProcess(ProcessRecord process);
    IDisposable HoldExecutable(ProcessRecord launcher);
    void ValidateProcess(ProcessRecord process);
    bool HasExited(ProcessRecord process);
    ProcessRecord? RequestShutdown(ProcessRecord launcher);
    Task Delay(TimeSpan interval, CancellationToken token);
}

public static class ProcessRespawnObserver
{
    public static IReadOnlyList<ProcessRecord> Find(IReadOnlyList<ProcessRecord> frozen,
        ProcessSnapshot baseline, ProcessSnapshot current) => current.Processes.Where(candidate =>
            candidate.StartTimeUtcTicks > 0 && candidate.OwnerSid.Length > 0 && candidate.SessionId > 0 &&
            !baseline.Processes.Any(original => SameIdentity(original, candidate)) &&
            frozen.Any(original => original.OwnerSid == candidate.OwnerSid && original.SessionId == candidate.SessionId &&
                SamePath(original.Path, candidate.Path)))
        .DistinctBy(p => (p.Id, p.StartTimeUtcTicks)).ToArray();

    public static bool SameIdentity(ProcessRecord left, ProcessRecord right) => left.Id == right.Id &&
        left.StartTimeUtcTicks == right.StartTimeUtcTicks && left.StartTimeUtcTicks > 0 &&
        left.OwnerSid == right.OwnerSid && left.SessionId == right.SessionId && SamePath(left.Path, right.Path);

    public static bool SamePath(string left, string right) => ProtectionPolicy.TryNormalizePath(left, out var a) &&
        ProtectionPolicy.TryNormalizePath(right, out var b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
