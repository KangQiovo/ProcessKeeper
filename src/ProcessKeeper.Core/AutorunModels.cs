using System.Security.Cryptography;
using System.Text;

namespace ProcessKeeper.Core;

public enum AutorunSourceKind
{
    RegistryRun, RegistryRunOnce, PolicyRun, StartupFolder, ScheduledTask, Service, Driver,
    AdvancedRegistry, WmiSubscription, PackagedStartup
}

/// <summary>Display attribution only; Unknown is visible by default and never weakens mutation protection.</summary>
public enum AutorunOwnership { Unknown, Windows, ThirdParty }

public sealed record AutorunLocator
{
    public string Hive { get; init; } = "";
    public int View { get; init; } = 64;
    public string Path { get; init; } = "";
    public string ValueName { get; init; } = "";
}

public sealed record AutorunEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public AutorunSourceKind SourceKind { get; init; }
    public string Scope { get; init; } = "";
    public string Location { get; init; } = "";
    public string Command { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public bool? Enabled { get; init; }
    public bool IsSystem { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AutorunOwnership Ownership { get; init; }
    public bool IsHidden { get; init; }
    public bool CanChange { get; init; }
    public string ServiceName { get; init; } = "";
    public string TriggerSummary { get; init; } = "";
    public string ReadOnlyReason { get; init; } = "";
    public string Details { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public AutorunLocator Locator { get; init; } = new();
}

public sealed record AutorunSnapshot
{
    public IReadOnlyList<AutorunEntry> Entries { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public bool Truncated { get; init; }
}

public sealed record AutorunChangeResult(bool Success, string Message, string BackupPath = "", bool RestartRequired = false);

/// <summary>Exact serialized native configuration; never a command to execute.</summary>
public sealed record AutorunState(bool Exists, bool? Enabled, string Payload)
{
    public string Fingerprint => AutorunIdentity.Hash($"{Exists}|{Enabled}|{Payload}");
}

public sealed record AutorunBackupRecord(AutorunEntry Entry, AutorunState Original, AutorunState Disabled);

/// <summary>Apply must compare the current native state with expected again before writing.</summary>
public interface IAutorunBackend
{
    AutorunState Read(AutorunEntry entry, CancellationToken cancellationToken = default);
    AutorunState CreateDisabledState(AutorunEntry entry, AutorunState current);
    void Apply(AutorunEntry entry, AutorunState expected, AutorunState desired);
}

public interface IAutorunBackupStore
{
    string Save(AutorunBackupRecord backup);
    AutorunBackupRecord? Load(string id);
    IReadOnlyList<AutorunBackupRecord> ReadAll();
}

public static class AutorunIdentity
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Id(AutorunSourceKind source, AutorunLocator locator) => Hash($"{source}|{locator.Hive}|{locator.View}|{locator.Path.ToUpperInvariant()}|{locator.ValueName.ToUpperInvariant()}");
}
