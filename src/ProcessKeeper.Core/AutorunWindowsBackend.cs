using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

public sealed partial class AutorunWindowsBackend : IAutorunBackend
{
    internal const int MaximumFileBytes = 256 * 1024;
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string PolicyPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    internal const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private sealed record RegistryPayload(int Kind, string Value);
    private sealed record FolderPayload(string Content, long Created, long Written, int Attributes, string Security);
    private sealed record TaskPayload(string Definition);

    public AutorunState Read(AutorunEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateLocator(entry);
        var raw = entry.SourceKind switch
        {
            AutorunSourceKind.RegistryRun or AutorunSourceKind.RegistryRunOnce or AutorunSourceKind.PolicyRun => ReadRegistry(entry),
            AutorunSourceKind.StartupFolder => ReadFolder(entry),
            AutorunSourceKind.ScheduledTask => WithTask(entry.Locator.Path, task => ReadTask(task)),
            AutorunSourceKind.Service => ReadService(entry),
            _ => throw new InvalidOperationException(L.T("此来源不支持修改。"))
        };
        return AutorunStartupApproval.Wrap(entry, raw);
    }

    public AutorunState CreateDisabledState(AutorunEntry entry, AutorunState current) => AutorunStartupApproval.IsManagedState(current)
        ? AutorunStartupApproval.ChangeState(current, false) : entry.SourceKind switch
    {
        AutorunSourceKind.RegistryRun or AutorunSourceKind.RegistryRunOnce or AutorunSourceKind.StartupFolder => new(false, false, ""),
        AutorunSourceKind.ScheduledTask => current with { Enabled = false },
        AutorunSourceKind.Service => DisabledService(current),
        _ => throw new InvalidOperationException(L.T("此来源不支持修改。"))
    };

    public void Apply(AutorunEntry entry, AutorunState expected, AutorunState desired)
    {
        ValidateLocator(entry);
        if (!entry.CanChange || entry.IsSystem) throw new InvalidOperationException(L.T("系统或只读入口不能修改。"));
        if (Read(entry).Fingerprint != expected.Fingerprint) throw new InvalidOperationException(L.T("入口已发生变化，请刷新列表后重新确认。"));
        if (AutorunStartupApproval.IsManagedState(expected))
        {
            AutorunStartupApproval.Write(entry, expected, desired);
            return;
        }
        switch (entry.SourceKind)
        {
            case AutorunSourceKind.RegistryRun:
            case AutorunSourceKind.RegistryRunOnce:
                if (expected.Exists && IsSystemTarget(ExtractTarget(RegistryCommand(expected))))
                    throw new InvalidOperationException(L.T("系统或只读入口不能修改。"));
                using (var root = OpenHive(entry.Locator))
                using (var key = root.OpenSubKey(entry.Locator.Path, writable: true) ?? throw new IOException(L.T("原始注册表项已不存在。")))
                {
                    if (ReadRegistry(entry).Fingerprint != expected.Fingerprint) throw new IOException(L.T("入口已发生变化，请刷新列表后重新确认。"));
                    if (!desired.Exists) key.DeleteValue(entry.Locator.ValueName, throwOnMissingValue: true);
                    else
                    {
                        var value = JsonSerializer.Deserialize<RegistryPayload>(desired.Payload) ?? throw new InvalidDataException();
                        if (value.Kind is not (int)RegistryValueKind.String and not (int)RegistryValueKind.ExpandString)
                            throw new InvalidDataException(L.T("注册表值类型不受支持。"));
                        if (IsSystemTarget(ExtractTarget(value.Value))) throw new InvalidOperationException(L.T("系统或只读入口不能修改。"));
                        key.SetValue(entry.Locator.ValueName, value.Value, (RegistryValueKind)value.Kind);
                    }
                    key.Flush();
                }
                break;
            case AutorunSourceKind.StartupFolder: ApplyFolder(entry, expected, desired); break;
            case AutorunSourceKind.ScheduledTask:
                WithTask(entry.Locator.Path, task =>
                {
                    var state = ReadTask(task);
                    if (state.Fingerprint != expected.Fingerprint || TaskIsSystem(entry.Locator.Path, (string)task.Xml))
                        throw new InvalidOperationException(L.T("计划任务已变化或属于系统任务。"));
                    task.Enabled = desired.Enabled == true;
                    return true;
                });
                break;
            case AutorunSourceKind.Service: ApplyService(entry, expected, desired); break;
            default: throw new InvalidOperationException(L.T("此来源不支持修改。"));
        }
    }

    internal static RegistryKey OpenHive(AutorunLocator locator) => RegistryKey.OpenBaseKey(locator.Hive switch
    { "HKLM" => RegistryHive.LocalMachine, "HKCU" => RegistryHive.CurrentUser, "HKU" => RegistryHive.Users, _ => throw new InvalidDataException() },
        locator.View == 32 ? RegistryView.Registry32 : RegistryView.Registry64);

    private static void ValidateLocator(AutorunEntry entry)
    {
        if (entry.Id != AutorunIdentity.Id(entry.SourceKind, entry.Locator) || entry.Locator.View is not 32 and not 64)
            throw new InvalidDataException(L.T("自启动入口身份不完整。"));
        var location = entry.Locator;
        switch (entry.SourceKind)
        {
            case AutorunSourceKind.RegistryRun:
            case AutorunSourceKind.RegistryRunOnce:
            case AutorunSourceKind.PolicyRun:
                var path = location.Path;
                if (location.Hive == "HKU")
                {
                    var slash = path.IndexOf('\\');
                    if (slash < 1 || !path[..slash].StartsWith("S-1-5-", StringComparison.Ordinal) || path[..slash].EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException();
                    path = path[(slash + 1)..];
                }
                else if (location.Hive is not "HKCU" and not "HKLM") throw new InvalidDataException();
                var expected = entry.SourceKind == AutorunSourceKind.RegistryRun ? RunPath : entry.SourceKind == AutorunSourceKind.RegistryRunOnce ? RunOncePath : PolicyPath;
                if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase) || location.ValueName.Length == 0 || location.ValueName.Length > 16383)
                    throw new InvalidDataException(L.T("注册表入口不在允许修改的范围内。"));
                break;
            case AutorunSourceKind.StartupFolder:
                if (!Path.IsPathFullyQualified(location.Path) || location.Path.StartsWith(@"\\", StringComparison.Ordinal) ||
                    !StartupDirectories().Any(root => string.Equals(Path.GetDirectoryName(location.Path), root.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(L.T("文件不在已核实的启动文件夹内。"));
                AutorunPathSafety.RejectReparseAncestors(location.Path);
                break;
            case AutorunSourceKind.ScheduledTask:
                if (!location.Path.StartsWith('\\') || location.Path.Contains("..", StringComparison.Ordinal) || location.Path.Length > 1024)
                    throw new InvalidDataException();
                break;
            case AutorunSourceKind.Service:
                if (location.Hive != "HKLM" || location.Path != ServicesPath || location.ValueName.Length == 0 ||
                    location.ValueName.IndexOfAny(['\\', '/', '\0']) >= 0) throw new InvalidDataException();
                break;
            default: throw new InvalidOperationException(L.T("此来源不支持修改。"));
        }
    }

    internal static AutorunState ReadRegistry(AutorunEntry entry)
    {
        using var root = OpenHive(entry.Locator);
        using var key = root.OpenSubKey(entry.Locator.Path);
        if (key is null || !key.GetValueNames().Contains(entry.Locator.ValueName, StringComparer.OrdinalIgnoreCase)) return new(false, false, "");
        var kind = key.GetValueKind(entry.Locator.ValueName);
        var value = key.GetValue(entry.Locator.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string text || kind is not RegistryValueKind.String and not RegistryValueKind.ExpandString || text.Length > 65536)
            throw new InvalidDataException(L.T("注册表值类型或长度不受支持，只能查看。"));
        return new(true, true, JsonSerializer.Serialize(new RegistryPayload((int)kind, text)));
    }
    internal static string RegistryCommand(AutorunState state) => state.Exists ? JsonSerializer.Deserialize<RegistryPayload>(state.Payload)?.Value ?? "" : "";

    private static AutorunState ReadFolder(AutorunEntry entry) => AutorunFileSafety.Read(entry.Locator.Path);

    private static void ApplyFolder(AutorunEntry entry, AutorunState expected, AutorunState desired) =>
        AutorunFileSafety.Apply(entry.Locator.Path, expected, desired);

    internal static AutorunState ReadTask(dynamic task)
    {
        string xml = task.Xml;
        if (xml.Length > 1024 * 1024) throw new InvalidDataException(L.T("计划任务定义超过读取上限。"));
        var document = XDocument.Parse(xml);
        foreach (var node in document.Descendants().Where(node => node.Name.LocalName == "Settings").Elements().Where(node => node.Name.LocalName == "Enabled").ToArray()) node.Remove();
        return new(true, (bool)task.Enabled, JsonSerializer.Serialize(new TaskPayload(document.ToString(SaveOptions.DisableFormatting))));
    }

    internal static T WithTask<T>(string path, Func<dynamic, T> action)
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException());
            ((dynamic)service!).Connect(); folder = ((dynamic)service).GetFolder("\\"); task = ((dynamic)folder).GetTask(path);
            return action(task);
        }
        finally { ReleaseCom(task); ReleaseCom(folder); ReleaseCom(service); }
    }
    internal static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    internal static bool TaskIsSystem(string path, string xml)
    {
        if (path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) return true;
        var doc = XDocument.Parse(xml);
        return doc.Descendants().Any(node => node.Name.LocalName == "Author" && node.Value.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) ||
            doc.Descendants().Where(node => node.Name.LocalName == "Command").Any(node => IsSystemTarget(ExtractTarget(node.Value)));
    }
    internal static string ExtractTarget(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.Length == 0) return "";
        var target = "";
        if (command[0] == '"') { var end = command.IndexOf('"', 1); if (end > 1) target = command[1..end]; }
        else { var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); target = exe >= 0 ? command[..(exe + 4)] : command.Split(' ', '\t')[0]; }
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        if (target.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), target[12..]);
        try { return Path.IsPathFullyQualified(target) && !target.StartsWith(@"\\", StringComparison.Ordinal) ? Path.GetFullPath(target) : ""; }
        catch { return ""; }
    }
    internal static bool IsSystemTarget(string path) => path.Length > 0 && path.StartsWith(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<(string Path, string Scope)> StartupDirectories()
    {
        var result = new List<(string, string)>();
        void Add(string value, string scope)
        {
            if (Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal) &&
                !result.Any(item => item.Item1.Equals(value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) result.Add((value.TrimEnd('\\'), scope));
        }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Startup), L.T("当前用户"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), L.T("所有用户"));
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        foreach (var sid in users.GetSubKeyNames().Where(name => name.StartsWith("S-1-5-", StringComparison.Ordinal) && !name.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)).Take(64))
        {
            try
            {
                using var folders = users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                using var profile = profiles?.OpenSubKey(sid);
                var root = Environment.ExpandEnvironmentVariables(profile?.GetValue("ProfileImagePath") as string ?? "");
                var path = folders?.GetValue("Startup", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                if (root.Length > 0)
                    path = path.Replace("%USERPROFILE%", root, StringComparison.OrdinalIgnoreCase)
                        .Replace("%APPDATA%", Path.Combine(root, "AppData", "Roaming"), StringComparison.OrdinalIgnoreCase);
                if (!path.Contains('%')) Add(path, sid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return result;
    }

    internal static string ShortcutTarget(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? path : "";
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            ((IPersistFile)link!).Load(path, 0);
            var text = new StringBuilder(32768); ((IShellLinkRead)link).GetPath(text, text.Capacity, 0, 4);
            return ExtractTarget(text.ToString());
        }
        catch { return ""; }
        finally { ReleaseCom(link); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkRead { void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maximum, nint findData, uint flags); }
}
