namespace ProcessKeeper.Core;

public enum RunCategory { Visible, Minimized, Background, System }
public enum RuleKind { Application, ExecutablePath, ProcessName, Directory }

public sealed record WindowRecord(nint Handle, string Title, bool IsVisible, bool IsMinimized, bool IsCloaked, bool IsForeground)
{
    public string ClassName { get; init; } = "";
    public bool IsTopLevel { get; init; } = true;
    public bool IsToolWindow { get; init; }
    public bool HasUsableBounds { get; init; }
    public bool HasCaption { get; init; }
    public bool HasAppWindowStyle { get; init; }
}
public sealed record ServiceRecord(string Name, string DisplayName, int ProcessId);

public sealed record ProcessRecord
{
    public int Id { get; init; }
    public int ParentId { get; init; }
    public int SessionId { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string ProductName { get; init; } = "";
    public string Company { get; init; } = "";
    public string Description { get; init; } = "";
    public string OwnerSid { get; init; } = "";
    public string PackageFamilyName { get; init; } = "";
    public bool ParentIdentityVerified { get; init; }
    public string ParentEvidence { get; init; } = "";
    public string CollectionWarning { get; init; } = "";
    public string ApplicationKey { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public string IdentityEvidence { get; init; } = "";
    public string RoleDescription { get; init; } = "";
    public string SystemReason { get; init; } = "";
    public long MemoryBytes { get; init; }
    public bool IsSystem { get; init; }
    /// <summary>Native critical-process query result; null means unverified. Independent of display/name classification.</summary>
    public bool? NativeCritical { get; init; }
    /// <summary>Collection could not query critical status. Close protection only; never Windows display ownership.</summary>
    public bool CriticalStatusUnknown { get; init; }
    public bool IsSelf { get; init; }
    public IReadOnlyList<WindowRecord> Windows { get; init; } = [];
    public IReadOnlyList<ServiceRecord> Services { get; init; } = [];
    public bool HasVisibleWindow => Windows.Any(w => w.IsVisible && !w.IsCloaked && !w.IsMinimized);
    public bool HasMinimizedWindow => Windows.Any(w => w.IsVisible && !w.IsCloaked && w.IsMinimized);
    public bool IsForeground => Windows.Any(w => w.IsForeground);
    public RunCategory Category => IsSystem ? RunCategory.System :
        HasVisibleWindow ? RunCategory.Visible : HasMinimizedWindow ? RunCategory.Minimized : RunCategory.Background;
}

public sealed record ApplicationGroup
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Company { get; init; } = "";
    public string Description { get; init; } = "";
    public string IdentityEvidence { get; init; } = "";
    public IReadOnlyList<ProcessRecord> Processes { get; init; } = [];
    public long MemoryBytes => Processes.Sum(p => p.MemoryBytes);
    public RunCategory Category => Processes.All(p => p.Category == RunCategory.System) ? RunCategory.System :
        Processes.Any(p => p.Category == RunCategory.Visible) ? RunCategory.Visible :
        Processes.Any(p => p.Category == RunCategory.Minimized) ? RunCategory.Minimized : RunCategory.Background;
    public bool IsForeground => Processes.Any(p => p.IsForeground);
}

public sealed record ProcessSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<ProcessRecord> Processes, IReadOnlyList<ApplicationGroup> Applications)
{
    public static ProcessSnapshot Empty { get; } = new(DateTimeOffset.MinValue, [], []);
}

public sealed record WhitelistRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public RuleKind Kind { get; init; }
    public string Value { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool IncludeDescendants { get; init; }
}

public sealed record ProtectionDecision(bool Protected, string Reason, string? RuleId = null);
public sealed record CloseResult(int ProcessId, string ProcessName, string Outcome, bool Success);
