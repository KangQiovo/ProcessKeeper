using System.Text.Json;

namespace ProcessKeeper.Core;

public sealed record WhitelistScopePreferences(bool Running = true, bool Installed = true, bool Autoruns = false, bool Uninstall = false);

/// <summary>Independent action scopes; old backups that omit this file keep local scopes.</summary>
public sealed class WhitelistScopePreferencesStore
{
    public string FilePath { get; }
    public WhitelistScopePreferencesStore(string directory) => FilePath = Path.Combine(Path.GetFullPath(directory), "whitelist-scope.json");
    public WhitelistScopePreferences Load()
    {
        if (!File.Exists(FilePath)) return new();
        using var stream = File.OpenRead(FilePath);
        if (stream.Length > 8192) throw new InvalidDataException("Whitelist scope file is too large");
        using var json = JsonDocument.Parse(stream);
        return Parse(json.RootElement);
    }
    public static WhitelistScopePreferences Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid whitelist scope");
        var allowed = new HashSet<string>(new[] { "Version", "Running", "Installed", "Autoruns", "Uninstall" }, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in root.EnumerateObject())
            if (!allowed.Contains(item.Name) || !seen.Add(item.Name)) throw new InvalidDataException("Unknown or duplicate whitelist scope field");
        if (seen.Count != allowed.Count || !root.GetProperty("Version").TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Invalid whitelist scope version or missing field");
        bool Read(string key) => root.GetProperty(key).ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException("Invalid whitelist scope flag") };
        return new(Read("Running"), Read("Installed"), Read("Autoruns"), Read("Uninstall"));
    }
    public static byte[] SerializeBytes(WhitelistScopePreferences value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, value.Running, value.Installed, value.Autoruns, value.Uninstall }, new JsonSerializerOptions { WriteIndented = true });
    }
    public void Save(WhitelistScopePreferences value)
    {
        var bytes = SerializeBytes(value); var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".whitelist-scope-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak", false);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
