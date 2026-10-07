namespace ProcessKeeper.Core;

/// <summary>Healthy Store/System package attribution read from exact OS registration. Display filtering only;
/// never grants protection, process-close, launch, update or uninstall authority.</summary>
public sealed record VerifiedMicrosoftPackageEvidence(string FamilyName, string FullName, string InstallLocation,
    IReadOnlyList<string> ExecutablePaths);

/// <summary>Healthy registered non-Store package identity awaiting an independent package-signature chain check.
/// Registration metadata alone is not positive Microsoft ownership.</summary>
public sealed record MicrosoftPackageSignatureCandidate(string FamilyName, string FullName, string InstallLocation,
    string Publisher, string SignaturePath, IReadOnlyList<string> ExecutablePaths);

/// <summary>An installed executable candidate, not a running process or a launch guarantee.</summary>
public sealed record InstalledExecutable
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    // Cached alongside FileDescription in the bounded inventory read. These
    // hints describe a product; they never establish executable trust.
    public string ProductName { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string FileVersion { get; init; } = "";
    public string ApplicationKey { get; init; } = "";
}

/// <summary>Original installation evidence. Presentation families retain each installation root and package record.</summary>
public sealed record InstalledApplication
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    /// <summary>OS-registered external content directory for a healthy, non-development package; never inferred from its display name.</summary>
    public string ExternalInstallLocation { get; init; } = "";
    public string IdentityEvidence { get; init; } = "";
    public string ApplicationKey { get; init; } = "";
    /// <summary>Preloaded package ownership, independent of localized Publisher/Name strings and standalone EXE signatures.</summary>
    public VerifiedMicrosoftPackageEvidence? MicrosoftPackageEvidence { get; init; }
    public MicrosoftPackageSignatureCandidate? MicrosoftPackageSignatureCandidate { get; init; }
    /// <summary>Exact OS registration associations for presentation only. Never replaces installation or mutation identities.</summary>
    public IReadOnlyList<string> PresentationRegistrationLinks { get; init; } = [];
    /// <summary>Exact runtime entries associated with their registered installer by provider, release, architecture and cached metadata. Display roles only.</summary>
    public IReadOnlyList<string> RegisteredRuntimeMainPaths { get; init; } = [];
    /// <summary>Registered cached installers retained as components after an exact runtime association. Never authorizes installer execution.</summary>
    public IReadOnlyList<string> RegisteredRuntimeInstallerPaths { get; init; } = [];
    /// <summary>Explicit registered, manifest, shortcut or user-selected entries; never inferred from list order.</summary>
    public IReadOnlyList<string> EntryPaths { get; init; } = [];
    public IReadOnlyList<InstalledExecutable> Executables { get; init; } = [];
    /// <summary>Original installation records retained by a presentation family; empty for an original record.</summary>
    public IReadOnlyList<InstalledApplication> Installations { get; init; } = [];
}
