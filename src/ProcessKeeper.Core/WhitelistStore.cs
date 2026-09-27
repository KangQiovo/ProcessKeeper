using System.Text;

namespace ProcessKeeper.Core;

/// <summary>Stores active rules while retaining optional inactive profiles in the same atomic file.</summary>
public sealed class WhitelistStore
{
    public string FilePath { get; }
    public WhitelistStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        FilePath = Path.Combine(Path.GetFullPath(directory), "whitelist.json");
    }

    public IReadOnlyList<WhitelistRule> Load() => WhitelistProfilesStore.LoadActive(FilePath);
    public void Save(IReadOnlyList<WhitelistRule> rules) => WhitelistProfilesStore.SaveActive(FilePath, rules);

    /// <summary>Validates and prepares active replacement without writing, retaining local inactive profiles.</summary>
    public byte[] PrepareSave(IReadOnlyList<WhitelistRule> rules) => WhitelistProfilesStore.PrepareActiveDocument(FilePath, rules);

    /// <summary>Only portable single-profile files are accepted. Use all-settings import for profile catalogues.</summary>
    public static IReadOnlyList<WhitelistRule> ReadImport(string path)
    {
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(path));
            if (stream.Length > RuleFileCodec.MaximumFileBytes) throw new InvalidDataException(L.T("白名单文件不能超过 1 MiB。"));
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return RuleFileCodec.Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidDataException(L.T("白名单配置无法读取，已保留原文件并停止修改。") + ex.Message, ex); }
    }

    /// <summary>Exports the current rules only, never inactive profiles or local profile metadata.</summary>
    public static void ExportToFile(string path, IReadOnlyList<WhitelistRule> rules)
    {
        var bytes = RuleFileCodec.SerializeBytes(rules);
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".whitelist-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(flushToDisk: true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Retains existing rules when kind, value, and inheritance match; imported ID collisions are re-keyed.</summary>
    public static IReadOnlyList<WhitelistRule> MergeRules(IReadOnlyList<WhitelistRule> existing, IReadOnlyList<WhitelistRule> incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        RuleFileCodec.Validate(existing);
        RuleFileCodec.Validate(incoming);
        var result = new List<WhitelistRule>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in existing.Concat(incoming))
        {
            if (!keys.Add(MergeKey(rule))) continue;
            var candidate = rule;
            while (!ids.Add(candidate.Id)) candidate = candidate with { Id = Guid.NewGuid().ToString("N") };
            result.Add(candidate);
        }
        RuleFileCodec.Validate(result);
        return result.AsReadOnly();
    }

    private static string MergeKey(WhitelistRule rule) => $"{rule.Kind}:{rule.IncludeDescendants}:{rule.Value}";

    /// <summary>New installations have no personal whitelist. Existing files are never reset.</summary>
    public static IReadOnlyList<WhitelistRule> CreateDefaults() => Array.Empty<WhitelistRule>();

    // Keep the display translation for profiles migrated from historical versions.
    public static string GetDisplayName(WhitelistRule rule) =>
        rule.Id == "default-codex" && rule.Kind == RuleKind.Application && rule.Value == "known:codex" &&
        rule.Name == "Codex及其工具子进程" ? L.T("Codex及其工具子进程") : rule.Name;
}