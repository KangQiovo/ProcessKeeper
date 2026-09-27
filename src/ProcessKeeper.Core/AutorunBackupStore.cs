using System.Text.Json;

namespace ProcessKeeper.Core;

public sealed class AutorunBackupStore : IAutorunBackupStore
{
    public string DirectoryPath { get; }
    private readonly bool _production;
    private const long MaximumRecordBytes = 2 * 1024 * 1024;
    public AutorunBackupStore(string? directory = null)
    {
        _production = directory is null;
        DirectoryPath = Path.GetFullPath(directory ?? AutorunBackupSecurity.GetDirectory());
    }

    public string Save(AutorunBackupRecord backup)
    {
        var path = RecordPath(backup.Entry.Id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup);
        if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException(L.T("自启动备份超过大小上限。"));
        if (_production) AutorunBackupSecurity.EnsureDirectory(DirectoryPath);
        else Directory.CreateDirectory(DirectoryPath);
        AutorunPathSafety.RejectReparseAncestors(DirectoryPath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = _production ? AutorunBackupSecurity.CreateFile(temporary) : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            if (File.Exists(path))
            {
                AutorunPathSafety.RejectReparseAncestors(path);
                if (_production) AutorunBackupSecurity.ValidateFile(path);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    public AutorunBackupRecord? Load(string id)
    {
        var path = RecordPath(id);
        if (!File.Exists(path)) return null;
        if (_production) AutorunBackupSecurity.ValidateFile(path);
        AutorunPathSafety.RejectReparseAncestors(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (_production) AutorunBackupSecurity.ValidateFile(file);
        if (file.Length > MaximumRecordBytes) throw new InvalidDataException(L.T("自启动备份超过大小上限。"));
        var record = JsonSerializer.Deserialize<AutorunBackupRecord>(file) ?? throw new InvalidDataException(L.T("自启动备份无法读取。"));
        if (record.Entry is null || record.Original is null || record.Disabled is null || record.Entry.Locator is null ||
            record.Original.Payload is null || record.Disabled.Payload is null || record.Entry.Locator.Path is null ||
            record.Entry.Locator.Hive is null || record.Entry.Locator.ValueName is null)
            throw new InvalidDataException(L.T("自启动备份的身份或状态不匹配。"));
        var validTaskToggle = record.Entry.SourceKind == AutorunSourceKind.ScheduledTask && record.Original.Exists && record.Disabled.Exists &&
            record.Original.Enabled.HasValue && record.Disabled.Enabled == !record.Original.Enabled.Value &&
            record.Original.Payload == record.Disabled.Payload;
        var validApprovalToggle = record.Entry.SourceKind is AutorunSourceKind.RegistryRun or AutorunSourceKind.StartupFolder &&
            AutorunStartupApproval.IsValidTransition(record.Original, record.Disabled);
        if (record.Entry.Id != id || record.Entry.Fingerprint != record.Original.Fingerprint ||
            (!validTaskToggle && !validApprovalToggle && (!record.Original.Exists || record.Original.Enabled != true || record.Disabled.Enabled != false)))
            throw new InvalidDataException(L.T("自启动备份的身份或状态不匹配。"));
        return record;
    }

    public IReadOnlyList<AutorunBackupRecord> ReadAll()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        if (_production) AutorunBackupSecurity.ValidateDirectory(DirectoryPath);
        AutorunPathSafety.RejectReparseAncestors(DirectoryPath);
        var result = new List<AutorunBackupRecord>();
        long totalBytes = 0;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").Take(500))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) continue;
            try
            {
                totalBytes += new FileInfo(path).Length;
                if (totalBytes > 16 * 1024 * 1024) break;
                if (Load(id) is { } item) result.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        }
        return result;
    }

    private string RecordPath(string id)
    {
        if (id.Length != 64 || id.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException(L.T("自启动备份标识无效。"));
        return Path.Combine(DirectoryPath, id + ".json");
    }
}

internal static class AutorunPathSafety
{
    internal static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("此路径含重解析点，不能安全修改。"));
    }
}
