using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Win32;

namespace ProcessKeeper.Core;

public sealed record AutorunApproval(bool HasRecord, bool? Enabled);

/// <summary>Only known 12-byte StartupApproved records can be changed. Original startup data stays intact.</summary>
public static class AutorunStartupApproval
{
    private const string Marker = "StartupApproved/v1|";
    private sealed record ApprovedState(AutorunState Original, AutorunLocator Approval, byte[] Data);
    public static AutorunApproval Interpret(object? raw, bool exists)
    {
        if (!exists) return new(false, null);
        if (raw is not byte[] { Length: 12 } data) return new(true, null);
        return new(true, BinaryPrimitives.ReadUInt32LittleEndian(data) switch { 2 or 6 => true, 3 or 7 => false, _ => null });
    }

    private static (AutorunLocator Locator, object? Value) ReadRecord(AutorunEntry entry)
    {
        var hive = entry.Locator.Hive;
        var prefix = "";
        var name = entry.Locator.ValueName;
        var category = entry.Locator.View == 32 ? "Run32" : "Run";
        if (entry.SourceKind == AutorunSourceKind.StartupFolder)
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup).TrimEnd('\\');
            hive = string.Equals(Path.GetDirectoryName(entry.Locator.Path), common, StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
            if (entry.Scope.StartsWith("S-1-5-", StringComparison.Ordinal)) { hive = "HKU"; prefix = entry.Scope + "\\"; }
            name = Path.GetFileName(entry.Locator.Path); category = "StartupFolder";
        }
        else if (hive == "HKU")
        {
            var slash = entry.Locator.Path.IndexOf('\\');
            if (slash <= 0) throw new InvalidDataException();
            prefix = entry.Locator.Path[..(slash + 1)];
        }
        var location = new AutorunLocator { Hive = hive, View = 64, ValueName = name,
            Path = prefix + @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + category };
        using var root = AutorunWindowsBackend.OpenHive(location);
        using var key = root.OpenSubKey(location.Path);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        // HKCU/HKU Software is shared across WOW64 views. HKLM Software is redirected:
        // identical text in its Run and Run32 entries does not make them the same entry.
        // https://learn.microsoft.com/windows/win32/winprog64/shared-registry-keys
        if (value is null && category == "Run32" && entry.SourceKind == AutorunSourceKind.RegistryRun && hive is "HKCU" or "HKU")
        {
            var sixtyFour = entry with { Locator = entry.Locator with { View = 64 } };
            if (AutorunWindowsBackend.ReadRegistry(entry).Payload == AutorunWindowsBackend.ReadRegistry(sixtyFour).Payload)
            {
                var sharedLocation = location with { Path = location.Path[..^5] + "Run" };
                using var shared = root.OpenSubKey(sharedLocation.Path);
                var sharedValue = shared?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (sharedValue is not null) { location = sharedLocation; value = sharedValue; }
            }
        }
        return (location, value);
    }
    private static bool Supports(AutorunEntry entry) => entry.SourceKind is AutorunSourceKind.RegistryRun or AutorunSourceKind.StartupFolder;

    public static AutorunApproval Read(AutorunEntry entry)
    {
        if (!Supports(entry)) return new(false, null);
        try { var record = ReadRecord(entry); return Interpret(record.Value, record.Value is not null); }
        catch { return new(true, null); }
    }

    internal static AutorunState Wrap(AutorunEntry entry, AutorunState original)
    {
        if (!Supports(entry) || !original.Exists) return original;
        var record = ReadRecord(entry);
        if (record.Value is null) return original;
        var enabled = Interpret(record.Value, true).Enabled;
        if (!enabled.HasValue) throw new InvalidDataException(L.T("Windows 启动审批状态无法识别，仅供查看。"));
        return new(true, enabled, Marker + JsonSerializer.Serialize(new ApprovedState(original, record.Locator, (byte[])record.Value)));
    }

    public static bool IsManagedState(AutorunState state) => state.Payload.StartsWith(Marker, StringComparison.Ordinal);
    private static ApprovedState Decode(AutorunState state)
    {
        if (!IsManagedState(state) || state.Payload.Length > 2 * 1024 * 1024) throw new InvalidDataException();
        var data = JsonSerializer.Deserialize<ApprovedState>(state.Payload[Marker.Length..]) ?? throw new InvalidDataException();
        if (!state.Exists || !data.Original.Exists || data.Original.Enabled != true ||
            !Interpret(data.Data, true).Enabled.HasValue || Interpret(data.Data, true).Enabled != state.Enabled)
            throw new InvalidDataException();
        return data;
    }
    public static AutorunState ChangeState(AutorunState current, bool enabled)
    {
        var data = Decode(current);
        var bytes = (byte[])data.Data.Clone();
        var family = BinaryPrimitives.ReadUInt32LittleEndian(bytes) >= 6 ? 6u : 2u;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, family + (enabled ? 0u : 1u));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), enabled ? 0 : DateTime.UtcNow.ToFileTimeUtc());
        return new(true, enabled, Marker + JsonSerializer.Serialize(data with { Data = bytes }));
    }

    public static bool IsValidTransition(AutorunState original, AutorunState changed)
    {
        try
        {
            var before = Decode(original); var after = Decode(changed);
            return original.Enabled != changed.Enabled && before.Original == after.Original && before.Approval == after.Approval &&
                BinaryPrimitives.ReadUInt32LittleEndian(before.Data) / 4 == BinaryPrimitives.ReadUInt32LittleEndian(after.Data) / 4;
        }
        catch { return false; }
    }

    internal static void Write(AutorunEntry entry, AutorunState expected, AutorunState desired)
    {
        if (!Supports(entry) || !entry.CanChange || entry.IsSystem) throw new InvalidOperationException();
        var before = Decode(expected); var after = Decode(desired);
        if (!IsValidTransition(expected, desired))
            throw new InvalidDataException();
        var target = entry.SourceKind == AutorunSourceKind.RegistryRun
            ? AutorunWindowsBackend.ExtractTarget(AutorunWindowsBackend.RegistryCommand(before.Original))
            : AutorunWindowsBackend.ShortcutTarget(entry.Locator.Path);
        if (AutorunWindowsBackend.IsSystemTarget(target)) throw new InvalidOperationException(L.T("系统或只读入口不能修改。"));
        // Derive the destination again from the real entry; never trust a serialized registry path.
        var live = ReadRecord(entry);
        if (live.Locator != before.Approval || live.Value is not byte[] bytes || !bytes.SequenceEqual(before.Data))
            throw new IOException(L.T("入口已发生变化，请刷新列表后重新确认。"));
        using var root = AutorunWindowsBackend.OpenHive(live.Locator);
        using var key = root.OpenSubKey(live.Locator.Path, writable: true) ?? throw new IOException();
        if (new AutorunWindowsBackend().Read(entry).Fingerprint != expected.Fingerprint)
            throw new IOException(L.T("入口已发生变化，请刷新列表后重新确认。"));
        key.SetValue(live.Locator.ValueName, after.Data, RegistryValueKind.Binary);
        key.Flush();
    }

    public static AutorunEntry Apply(AutorunEntry entry)
    {
        var approval = Read(entry);
        if (!approval.HasRecord) return entry;
        if (!approval.Enabled.HasValue) return entry with { Enabled = null, CanChange = false,
            ReadOnlyReason = L.T("Windows 启动审批状态无法识别，仅供查看。") };
        try
        {
            var state = new AutorunWindowsBackend().Read(entry);
            return entry with { Enabled = state.Enabled, Fingerprint = state.Fingerprint,
                Details = entry.Details + "\n" + L.T("通过 Windows 启动审批切换，保留原始启动命令或文件。") };
        }
        catch (Exception ex) { return entry with { Enabled = null, CanChange = false, ReadOnlyReason = ex.Message }; }
    }
}
