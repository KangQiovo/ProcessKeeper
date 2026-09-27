using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProcessKeeper.Core;

public sealed record WhitelistProfile(string Id, string Name, IReadOnlyList<WhitelistRule> Rules);
public sealed record WhitelistProfilesSnapshot(string ActiveId, IReadOnlyList<WhitelistProfile> Profiles, string Revision)
{
    public WhitelistProfile ActiveProfile => Profiles.Single(profile => profile.Id == ActiveId);
    public IReadOnlyList<WhitelistRule> ActiveRules => ActiveProfile.Rules;
}
public sealed record WhitelistProfilesResetResult(WhitelistProfilesSnapshot Snapshot, string? BackupPath);

/// <summary>
/// One atomic whitelist document: its root Rules are authoritative for the active profile.
/// Inactive rules live in optional Profiles metadata; the active profile never duplicates Rules.
/// </summary>
public sealed class WhitelistProfilesStore
{
    public const int MaximumProfiles = 5;
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    public string FilePath { get; }

    public WhitelistProfilesStore(string? directory = null) => FilePath = new WhitelistStore(directory).FilePath;

    /// <summary>Old files become one virtual profile without any write or loss of their bytes.</summary>
    public WhitelistProfilesSnapshot Load() => ReadState(FilePath).Snapshot;
    public WhitelistProfilesSnapshot ExportSnapshot() => Load();
    public WhitelistProfile Preview(string id) => Find(Load(), id);

    /// <summary>Explicit recovery only: reset every profile after caller confirmation, retaining raw original bytes.</summary>
    public WhitelistProfilesResetResult ResetToDefaults()
    {
        using var operation = WhitelistProfileOperationGate.EnterMutation();
        using var fileLock = LockFile(FilePath);
        var bytes = RuleFileCodec.SerializeBytes(Array.Empty<WhitelistRule>());
        var temporary = Path.Combine(Path.GetDirectoryName(FilePath)!, ".whitelist-" + Guid.NewGuid().ToString("N") + ".tmp");
        string? backup = null;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(flushToDisk: true); }
            if (File.Exists(FilePath))
            {
                // Do not replace the previous good .bak with a damaged file during recovery.
                backup = FilePath + ".reset-" + Guid.NewGuid().ToString("N") + ".bak";
                File.Replace(temporary, FilePath, backup, ignoreMetadataErrors: false);
            }
            else File.Move(temporary, FilePath);
            return new(Decode(bytes).Snapshot, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public WhitelistProfilesSnapshot Create(string name, string expectedRevision) => Change(expectedRevision, snapshot =>
    {
        if (snapshot.Profiles.Count >= MaximumProfiles) throw new InvalidDataException(L.T("最多只能保存 5 套白名单配置。"));
        var items = snapshot.Profiles.Concat(new[] { new WhitelistProfile(Guid.NewGuid().ToString("N"), name, Array.Empty<WhitelistRule>()) }).ToArray();
        return snapshot with { Profiles = Array.AsReadOnly(items) };
    });

    public WhitelistProfilesSnapshot Rename(string id, string name, string expectedRevision) => Change(expectedRevision, snapshot =>
    {
        _ = Find(snapshot, id);
        return snapshot with { Profiles = Array.AsReadOnly(snapshot.Profiles.Select(profile => profile.Id == id ? profile with { Name = name } : profile).ToArray()) };
    });

    public WhitelistProfilesSnapshot Delete(string id, string expectedRevision) => Change(expectedRevision, snapshot =>
    {
        _ = Find(snapshot, id);
        if (snapshot.Profiles.Count <= 1 || snapshot.ActiveId == id)
            throw new InvalidDataException(L.T("不能删除正在使用的配置或最后一套配置。"));
        return snapshot with { Profiles = Array.AsReadOnly(snapshot.Profiles.Where(profile => profile.Id != id).ToArray()) };
    });

    public WhitelistProfilesSnapshot Save(string id, IReadOnlyList<WhitelistRule> rules, string expectedRevision)
    {
        var copy = FreezeRules(rules);
        return Change(expectedRevision, snapshot =>
        {
            _ = Find(snapshot, id);
            return snapshot with { Profiles = Array.AsReadOnly(snapshot.Profiles.Select(profile => profile.Id == id ? profile with { Rules = copy } : profile).ToArray()) };
        });
    }

    public WhitelistProfilesSnapshot Activate(string id, string expectedRevision) => Change(expectedRevision, snapshot =>
    {
        _ = Find(snapshot, id);
        return snapshot with { ActiveId = id };
    });

    /// <summary>Reads the optional catalogue of an all-settings whitelist. A plain rules file returns null.</summary>
    public static WhitelistProfilesSnapshot? ReadProfiles(string document)
    {
        var state = Decode(Encoding.UTF8.GetBytes(document));
        return state.HasProfiles ? state.Snapshot : null;
    }

    /// <summary>Validates a complete snapshot, including equality with its authoritative active rules.</summary>
    public static byte[] SerializeDocument(IReadOnlyList<WhitelistRule> rules, WhitelistProfilesSnapshot? profiles)
    {
        var active = FreezeRules(rules);
        if (profiles is null) return RuleFileCodec.SerializeBytes(active);
        ValidateProfiles(profiles);
        if (!profiles.ActiveRules.SequenceEqual(active)) throw new InvalidDataException(L.T("活动配置与当前白名单不一致，请重新读取后再保存。"));
        var items = new List<object>();
        foreach (var profile in profiles.Profiles)
        {
            if (profile.Id == profiles.ActiveId) items.Add(new { profile.Id, profile.Name });
            else items.Add(new { profile.Id, profile.Name, Rules = profile.Rules });
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, Rules = active,
            Profiles = new { Version = 1, profiles.ActiveId, Items = items }
        }, RuleFileCodec.Options);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException(L.T("全部白名单配置不能超过 5 MiB。"));
        return bytes;
    }

    internal static byte[] PrepareActiveDocument(string path, IReadOnlyList<WhitelistRule> rules)
    {
        var state = ReadState(path);
        if (!state.HasProfiles) return RuleFileCodec.SerializeBytes(rules);
        var snapshot = WithActiveRules(state.Snapshot, rules);
        return SerializeDocument(snapshot.ActiveRules, snapshot);
    }

    internal static void SaveActive(string path, IReadOnlyList<WhitelistRule> rules)
    {
        var copy = FreezeRules(rules); // Invalid rules never create directories or a lock file.
        using var operation = WhitelistProfileOperationGate.EnterMutation();
        using var fileLock = LockFile(path);
        var state = ReadState(path); // A damaged inactive profile also prevents ordinary saves.
        var snapshot = WithActiveRules(state.Snapshot, copy);
        var bytes = SerializeDocument(copy, state.HasProfiles ? snapshot : null);
        AtomicWrite(path, bytes, state.Snapshot.Revision);
    }

    internal static IReadOnlyList<WhitelistRule> LoadActive(string path) => ReadState(path).Snapshot.ActiveRules;

    private WhitelistProfilesSnapshot Change(string expectedRevision, Func<WhitelistProfilesSnapshot, WhitelistProfilesSnapshot> transform)
    {
        using var operation = WhitelistProfileOperationGate.EnterMutation();
        using var fileLock = LockFile(FilePath);
        var state = ReadState(FilePath);
        if (string.IsNullOrEmpty(expectedRevision) || !string.Equals(state.Snapshot.Revision, expectedRevision, StringComparison.Ordinal))
            throw new InvalidDataException(L.T("白名单配置已被修改，请重新读取后再保存。"));
        var changed = transform(state.Snapshot);
        var bytes = SerializeDocument(changed.ActiveRules, changed);
        AtomicWrite(FilePath, bytes, expectedRevision);
        return Decode(bytes).Snapshot;
    }

    private static WhitelistProfilesSnapshot WithActiveRules(WhitelistProfilesSnapshot snapshot, IReadOnlyList<WhitelistRule> rules) => snapshot with
    {
        Profiles = Array.AsReadOnly(snapshot.Profiles.Select(profile => profile.Id == snapshot.ActiveId ? profile with { Rules = FreezeRules(rules) } : profile).ToArray())
    };

    private static FileStream LockFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static State ReadState(string path)
    {
        try { return Decode(ReadBytes(path)); }
        catch (FileNotFoundException) { return Missing(); }
        catch (DirectoryNotFoundException) { return Missing(); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException(L.T("白名单配置无法读取，已保留原文件并停止修改。") + ex.Message, ex); }
    }

    private static State Missing() => new(new("default", Array.AsReadOnly(new[]
    { new WhitelistProfile("default", L.T("默认配置"), Array.Empty<WhitelistRule>()) }), "missing"), false);

    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException(L.T("全部白名单配置不能超过 5 MiB。"));
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static State Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException(L.T("全部白名单配置不能超过 5 MiB。"));
        try
        {
            // Existing files saved by an external Windows editor may contain a UTF-8 BOM.
            // Its original bytes still participate in the revision hash and exact backup.
            var jsonBytes = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? bytes.Skip(3).ToArray() : bytes;
            using var document = JsonDocument.Parse(jsonBytes);
            var root = document.RootElement;
            var hasProfiles = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Profiles", out _);
            RuleFileCodec.RequireFields(root, hasProfiles ? new[] { "Version", "Rules", "Profiles" } : new[] { "Version", "Rules" });
            var activeRules = RuleFileCodec.ReadRules(root);
            var revision = Hash(bytes);
            if (!hasProfiles) return new(new("default", Array.AsReadOnly(new[] { new WhitelistProfile("default", L.T("默认配置"), activeRules) }), revision), false);
            var data = root.GetProperty("Profiles");
            RuleFileCodec.RequireFields(data, "Version", "ActiveId", "Items");
            if (data.GetProperty("Version").ValueKind != JsonValueKind.Number || !data.GetProperty("Version").TryGetInt32(out var version) || version != 1)
                throw new InvalidDataException(L.T("白名单配置版本无效。"));
            var activeId = data.GetProperty("ActiveId").GetString() ?? "";
            var items = data.GetProperty("Items");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() < 1 || items.GetArrayLength() > MaximumProfiles)
                throw new InvalidDataException(L.T("最多只能保存 5 套白名单配置。"));
            var profiles = new List<WhitelistProfile>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Id", out var idValue)) throw new InvalidDataException(L.T("白名单配置格式无效。"));
                var id = idValue.GetString() ?? "";
                RuleFileCodec.RequireFields(item, id == activeId ? new[] { "Id", "Name" } : new[] { "Id", "Name", "Rules" });
                var rules = id == activeId ? activeRules : RuleFileCodec.Parse("{\"Version\":1,\"Rules\":" + item.GetProperty("Rules").GetRawText() + "}");
                profiles.Add(new(id, item.GetProperty("Name").GetString() ?? "", rules));
            }
            var snapshot = new WhitelistProfilesSnapshot(activeId, profiles.AsReadOnly(), revision);
            ValidateProfiles(snapshot);
            return new(snapshot, true);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException(L.T("白名单配置格式无效。"), ex); }
    }

    private static void ValidateProfiles(WhitelistProfilesSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Profiles is null || snapshot.Profiles.Count < 1 || snapshot.Profiles.Count > MaximumProfiles)
            throw new InvalidDataException(L.T("最多只能保存 5 套白名单配置。"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in snapshot.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 64 || profile.Id.Any(c => !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_') || !ids.Add(profile.Id))
                throw new InvalidDataException(L.T("白名单配置标识无效或重复。"));
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Name != profile.Name.Trim() || profile.Name.Any(char.IsControl) || !names.Add(profile.Name))
                throw new InvalidDataException(L.T("配置名称应为 1 至 80 个字符，且不能重复或包含控制字符。"));
            RuleFileCodec.Validate(profile.Rules);
        }
        if (!ids.Contains(snapshot.ActiveId)) throw new InvalidDataException(L.T("活动白名单配置不存在。"));
    }

    private static IReadOnlyList<WhitelistRule> FreezeRules(IReadOnlyList<WhitelistRule> rules)
    {
        RuleFileCodec.Validate(rules);
        return Array.AsReadOnly(rules.ToArray());
    }

    private static WhitelistProfile Find(WhitelistProfilesSnapshot snapshot, string id) =>
        snapshot.Profiles.FirstOrDefault(profile => profile.Id == id) ?? throw new InvalidDataException(L.T("白名单配置不存在，请重新读取。"));

    private static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    private static void AtomicWrite(string path, byte[] bytes, string expectedRevision)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".whitelist-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(flushToDisk: true); }
            if (!string.Equals(ReadState(path).Snapshot.Revision, expectedRevision, StringComparison.Ordinal))
                throw new InvalidDataException(L.T("白名单配置已被修改，请重新读取后再保存。"));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record State(WhitelistProfilesSnapshot Snapshot, bool HasProfiles);
}
