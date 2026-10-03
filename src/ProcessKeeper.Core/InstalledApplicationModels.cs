namespace ProcessKeeper.Core;

/// <summary>An installed executable candidate, not a running process or a launch guarantee.</summary>
public sealed record InstalledExecutable
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string ApplicationKey { get; init; } = "";
}

/// <summary>Original installation evidence. Presentation families retain each installation root and package record.</summary>
public sealed record InstalledApplication
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string IdentityEvidence { get; init; } = "";
    public string ApplicationKey { get; init; } = "";
    /// <summary>Explicit registered, manifest, shortcut or user-selected entries; never inferred from list order.</summary>
    public IReadOnlyList<string> EntryPaths { get; init; } = [];
    public IReadOnlyList<InstalledExecutable> Executables { get; init; } = [];
    /// <summary>Original installation records retained by a presentation family; empty for an original record.</summary>
    public IReadOnlyList<InstalledApplication> Installations { get; init; } = [];
}
