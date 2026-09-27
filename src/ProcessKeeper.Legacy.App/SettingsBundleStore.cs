// Compatibility copy of modern settings/lifecycle contract; JSON formats remain shared.
using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed record SettingsBundle(IReadOnlyList<WhitelistRule> Rules, AppearancePreferences Appearance, ViewPreferences View, UpdatePreferences? Updates = null, WhitelistProfilesSnapshot? Profiles = null);

public sealed class SettingsCommitException : IOException
{
    public bool RollbackSucceeded { get; }
    public string BackupDirectory { get; }

    internal SettingsCommitException(string message, Exception inner, bool rollbackSucceeded, string backupDirectory)
        : base(message, inner) { RollbackSucceeded = rollbackSucceeded; BackupDirectory = backupDirectory; }
}

/// <summary>Exports configuration only. Multi-file commit rolls back ordinary errors, not abrupt power loss.</summary>
public static class SettingsBundleStore
{
    public const string Format = "ProcessKeeper.Settings";
    private const int MaximumFileBytes = 6 * 1024 * 1024;
    private static readonly string[] FileNames = ["whitelist.json", "appearance.json", "view.json", "update.json"];

    public static SettingsBundle ReadImport(string path)
    {
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(path));
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException(L.T("全部设置包不能超过 6 MiB。"));
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var fields = new List<string> { "Format", "Version", "Whitelist", "Appearance", "View" };
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Updates", out _)) fields.Add("Updates");
            SettingsJson.RequireObject(root, fields.ToArray());
            if (root.GetProperty("Format").ValueKind != JsonValueKind.String || root.GetProperty("Format").GetString() != Format)
                throw new InvalidDataException(L.T("文件不是Process Keeper的全部设置包；仅白名单文件请使用白名单导入。"));
            SettingsJson.Version(root);
            var whitelist = root.GetProperty("Whitelist");
            var whitelistText = whitelist.GetRawText();
            var profiles = WhitelistProfilesStore.ReadProfiles(whitelistText);
            var rules = profiles?.ActiveRules ?? RuleFileCodec.Parse(whitelistText);
            var appearance = SettingsJson.ReadAppearance(root.GetProperty("Appearance"));
            var view = SettingsJson.ReadView(root.GetProperty("View"));
            var updates = root.TryGetProperty("Updates", out var updateJson) ? UpdatePreferencesStore.ReadJson(updateJson) : null;
            return new SettingsBundle(rules, appearance, view, updates, profiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException(L.T("全部设置包无法读取，当前设置未修改。") + ex.Message, ex);
        }
    }

    public static void Export(string path, SettingsBundle bundle)
    {
        var prepared = Prepare(bundle);
        SettingsJson.AtomicWrite(path, prepared.Bundle);
    }

    public static void CommitAll(string directory, SettingsBundle bundle) => CommitAll(directory, bundle, null);

    // Failure injection is limited to the fixture harness; production callers cannot supply it.
    internal static void CommitAll(string directory, SettingsBundle bundle, Action<int>? beforeReplace)
    {
        using var profileLease = WhitelistProfileOperationGate.EnterMutation();
        var prepared = Prepare(bundle); // Complete validation precedes any change to the destination.
        // Older settings bundles affect only the current profile and retain the destination's other profiles.
        if (bundle.Profiles is null) prepared.Files["whitelist.json"] = new WhitelistStore(directory).PrepareSave(bundle.Rules);
        var fileNames = FileNames.Where(prepared.Files.ContainsKey).ToArray(); // Old bundles do not reset update preferences.
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var token = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N");
        var stage = Path.Combine(directory, ".settings-stage-" + token);
        var backup = Path.Combine(directory, "backups", "settings-" + token);
        var existed = new Dictionary<string, bool>(StringComparer.Ordinal);
        var replaced = new List<string>();
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var name in fileNames) SettingsJson.WriteNew(Path.Combine(stage, name), prepared.Files[name]);
            Directory.CreateDirectory(backup);
            foreach (var name in fileNames)
            {
                var destination = Path.Combine(directory, name);
                if (Directory.Exists(destination)) throw new IOException(L.T("设置文件位置被目录占用：") + name);
                try
                {
                    using var source = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var copy = new FileStream(Path.Combine(backup, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    source.CopyTo(copy);
                    copy.Flush(flushToDisk: true);
                    existed[name] = true;
                }
                catch (FileNotFoundException) { existed[name] = false; }
            }
            SettingsJson.WriteNew(Path.Combine(backup, "backup-info.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = 1, CreatedUtc = DateTime.UtcNow, OriginalFiles = existed
            }, SettingsJson.Options));

            for (var i = 0; i < fileNames.Length; i++)
            {
                beforeReplace?.Invoke(i);
                var name = fileNames[i];
                var destination = Path.Combine(directory, name);
                if (existed[name]) File.Replace(Path.Combine(stage, name), destination, null, ignoreMetadataErrors: false);
                else File.Move(Path.Combine(stage, name), destination);
                replaced.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rollbackErrors = new List<string>();
            foreach (var name in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = Path.Combine(directory, name);
                    if (existed[name])
                    {
                        var restoring = Path.Combine(stage, "restore-" + name);
                        File.Copy(Path.Combine(backup, name), restoring, overwrite: false);
                        if (File.Exists(destination)) File.Replace(restoring, destination, null, ignoreMetadataErrors: false);
                        else File.Move(restoring, destination);
                    }
                    else File.Delete(destination);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                { rollbackErrors.Add(name + "：" + rollback.Message); }
            }
            var restored = rollbackErrors.Count == 0;
            throw new SettingsCommitException(
                restored ? L.F($"设置导入失败，原有配置已保留或恢复。备份目录：{backup}。原因：{ex.Message}") :
                    L.F($"设置导入失败，部分配置无法自动恢复，请停止关闭操作并从备份恢复。备份目录：{backup}。{string.Join("；", rollbackErrors)}"),
                ex, restored, backup);
        }
        finally
        {
            // Delete only this operation's fixed, known temporary files; backups are always retained.
            foreach (var name in fileNames.SelectMany(name => new[] { name, "restore-" + name }))
                try { File.Delete(Path.Combine(stage, name)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static PreparedSettings Prepare(SettingsBundle bundle)
    {
        if (bundle is null || bundle.Rules is null) throw new InvalidDataException(L.T("设置包或白名单不能为空。"));
        var appearance = SettingsJson.AppearanceBytes(bundle.Appearance);
        var view = SettingsJson.ViewBytes(bundle.View);
        var rules = bundle.Rules.ToArray(); // The exported snapshot never changes the caller's rules.
        var whitelist = WhitelistProfilesStore.SerializeDocument(rules, bundle.Profiles);
        using var whitelistJson = JsonDocument.Parse(whitelist);
        using var appearanceJson = JsonDocument.Parse(appearance);
        using var viewJson = JsonDocument.Parse(view);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        { ["whitelist.json"] = whitelist, ["appearance.json"] = appearance, ["view.json"] = view };
        var content = new Dictionary<string, object>
        {
            ["Format"] = Format, ["Version"] = 1, ["Whitelist"] = whitelistJson.RootElement,
            ["Appearance"] = appearanceJson.RootElement, ["View"] = viewJson.RootElement
        };
        if (bundle.Updates is not null)
        {
            files["update.json"] = UpdatePreferencesStore.Serialize(bundle.Updates);
            using var updateJson = JsonDocument.Parse(files["update.json"]);
            content["Updates"] = updateJson.RootElement.Clone();
        }
        var data = JsonSerializer.SerializeToUtf8Bytes(content, SettingsJson.Options);
        if (data.Length > MaximumFileBytes) throw new InvalidDataException(L.T("全部设置包不能超过 6 MiB。"));
        return new PreparedSettings(data, files);
    }

    private sealed record PreparedSettings(byte[] Bundle, Dictionary<string, byte[]> Files);
}
