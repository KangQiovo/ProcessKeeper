using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace ProcessKeeper.Core;

/// <summary>Bounded inventory only. None of these sources can be changed or invoked by this scanner.</summary>
public static class AutorunAdvancedScanner
{
    private const int MaximumEntries = 2000, MaximumSourceEntries = 1000, MaximumRegistryBytes = 65536;
    private static readonly SemaphoreSlim WmiReader = new(1), PackageReader = new(1);
    private const string NtVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\";
    private const string AdvancedReadOnly = "高级启动入口仅供查看，不能在此更改。";

    public static IReadOnlyList<AutorunEntry> Scan(List<string> warnings, CancellationToken token, Func<bool> budgetAvailable)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(budgetAvailable);
        token.ThrowIfCancellationRequested();
        var entries = new List<AutorunEntry>();
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var notice = new HashSet<string>(StringComparer.Ordinal);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool Available()
        {
            token.ThrowIfCancellationRequested();
            return entries.Count < MaximumEntries && timer.Elapsed < TimeSpan.FromSeconds(30) && budgetAvailable();
        }
        void Warn(string message) { if (notice.Add(message)) warnings.Add(message); }
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            if (!Available()) break;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                var prefix = (hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU") + $" [{(view == RegistryView.Registry64 ? 64 : 32)}]\\";
                void AddValue(string path, string name, string category)
                {
                    if (!Available()) return;
                    if (categoryCounts.GetValueOrDefault(category) >= MaximumSourceEntries)
                    { Warn(L.F($"高级来源 {category} 达到 {MaximumSourceEntries} 项读取上限。")); return; }
                    try
                    {
                        using var key = root.OpenSubKey(path, false);
                        if (key is null) return;
                        var raw = ReadRegistryValue(key, name);
                        if (raw is null || raw.Value.Text.Length == 0) return;
                        var location = prefix + path + "\\" + name;
                        entries.Add(new AutorunEntry
                        {
                            Id = AutorunIdentity.Hash("advanced|" + location), Name = category + " | " + name,
                            SourceKind = AutorunSourceKind.AdvancedRegistry,
                            Scope = hive == RegistryHive.LocalMachine ? L.T("本机高级入口") : L.T("当前用户高级入口"),
                            Location = location, Command = raw.Value.Text, Enabled = null, IsSystem = true, CanChange = false,
                            TriggerSummary = category, ReadOnlyReason = L.T(AdvancedReadOnly),
                            Details = L.T("已读取注册配置；条件是否满足及实际启用状态未验证。"),
                            Fingerprint = AutorunIdentity.Hash(location + "|" + raw.Value.Type + "|" + raw.Value.Raw)
                        });
                        categoryCounts[category] = categoryCounts.GetValueOrDefault(category) + 1;
                    }
                    catch (Exception ex) when (IsReadFailure(ex)) { Warn(L.F($"高级注册表入口读取不完整：{category}（{ex.GetType().Name}）。")); }
                }
                void AddChildren(string path, string[] valueNames, string category, bool filters = false)
                {
                    try
                    {
                        using var key = root.OpenSubKey(path, false);
                        if (key is null) return;
                        var count = 0;
                        foreach (var child in Subkeys(key, MaximumSourceEntries, Available))
                        {
                            if (!Available()) break;
                            count++;
                            foreach (var name in valueNames) AddValue(path + "\\" + child, name, category);
                            if (!filters) continue;
                            using var nested = key.OpenSubKey(child, false);
                            if (nested is null) continue;
                            foreach (var filter in Subkeys(nested, 32, Available))
                                AddValue(path + "\\" + child + "\\" + filter, "Debugger", category);
                        }
                        if (count == MaximumSourceEntries && key.SubKeyCount > count) Warn(L.F($"高级来源 {category} 达到 {MaximumSourceEntries} 项读取上限。"));
                    }
                    catch (Exception ex) when (IsReadFailure(ex)) { Warn(L.F($"高级注册表入口读取不完整：{category}（{ex.GetType().Name}）。")); }
                }
                AddValue(NtVersion + "Winlogon", "Shell", "Winlogon");
                AddValue(NtVersion + "Winlogon", "Userinit", "Winlogon");
                AddChildren(NtVersion + @"Winlogon\Notify", ["DLLName"], "Winlogon Notify");
                AddValue(NtVersion + "Windows", "AppInit_DLLs", "AppInit");
                AddChildren(NtVersion + "Image File Execution Options", ["Debugger"], "IFEO", true);
                AddChildren(NtVersion + "SilentProcessExit", ["MonitorProcess"], "SilentProcessExit");
                AddChildren(@"SOFTWARE\Microsoft\Active Setup\Installed Components", ["StubPath"], "Active Setup");
                if (hive == RegistryHive.LocalMachine && (view == RegistryView.Registry64 || !Environment.Is64BitOperatingSystem))
                {
                    foreach (var name in new[] { "BootExecute", "SetupExecute", "Execute" })
                        AddValue(@"SYSTEM\CurrentControlSet\Control\Session Manager", name, "BootExecute");
                    foreach (var name in new[] { "Authentication Packages", "Notification Packages", "Security Packages" })
                        AddValue(@"SYSTEM\CurrentControlSet\Control\Lsa", name, "LSA");
                    AddValue(@"SYSTEM\CurrentControlSet\Control\Lsa\OSConfig", "Security Packages", "LSA");
                }
            }
            catch (Exception ex) when (IsReadFailure(ex)) { Warn(L.T("某个高级注册表视图不可读，已保留其他来源。")); }
        }
        if (Available()) AddBoundedWorker(WmiReader, ReadWmi, L.T("WMI 永久订阅"));
        if (Available()) AddBoundedWorker(PackageReader, ReadPackages, L.T("打包应用启动声明"));
        if (!Available()) Warn(L.T("高级启动来源达到数量或时间边界，结果可能不完整。"));
        return entries.AsReadOnly();

        void AddBoundedWorker(SemaphoreSlim gate, Func<CancellationToken, WorkerResult> read, string name)
        {
            if (!gate.Wait(0)) { Warn(L.F($"{name} 的上次系统读取尚未结束，本次未重复请求。")); return; }
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var operation = Task.Run(() =>
            {
                try { return read(stop.Token); }
                finally { gate.Release(); }
            });
            try
            {
                var result = operation.WaitAsync(TimeSpan.FromSeconds(8), token).GetAwaiter().GetResult();
                foreach (var warning in result.Warnings) Warn(warning);
                foreach (var entry in result.Entries)
                {
                    if (!Available()) break;
                    entries.Add(entry);
                }
            }
            catch (TimeoutException) { stop.Cancel(); Warn(L.F($"{name} 读取超时，系统调用可能仍在完成，当前保留其他来源。")); }
            catch (OperationCanceledException) { stop.Cancel(); throw; }
            catch (Exception ex) { Warn(L.F($"{name} 读取失败（{ex.GetType().Name}），已保留其他来源。")); }
            finally
            {
                // A timed-out COM read is not interruptible. Keep one bounded worker
                // per source and observe its eventual completion without sharing result lists.
                if (operation.IsCompleted) { _ = operation.Exception; stop.Dispose(); }
                else _ = operation.ContinueWith(task => { _ = task.Exception; stop.Dispose(); }, TaskScheduler.Default);
            }
        }
    }

    private sealed record WorkerResult(IReadOnlyList<AutorunEntry> Entries, IReadOnlyList<string> Warnings);

    private static WorkerResult ReadWmi(CancellationToken token)
    {
        var entries = new List<AutorunEntry>();
        var warnings = new List<string>();
        foreach (var scope in new[] { @"root\subscription", @"root\default" })
        {
            object? locator = null, service = null;
            try
            {
                token.ThrowIfCancellationRequested();
                locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
                service = ((dynamic)locator!).ConnectServer(".", scope);
                var consumers = new Dictionary<string, (string Command, string Name)>(StringComparer.OrdinalIgnoreCase);
                var filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Read("SELECT __RELPATH,Name,CommandLineTemplate,ExecutablePath FROM CommandLineEventConsumer", item =>
                    consumers[Relative(Property(item, "__RELPATH"), scope)] = (Property(item, "CommandLineTemplate"), Property(item, "Name")));
                Read("SELECT __RELPATH,Name,ScriptingEngine,ScriptFileName FROM ActiveScriptEventConsumer", item =>
                    consumers[Relative(Property(item, "__RELPATH"), scope)] = (Property(item, "ScriptingEngine") + " | " + Property(item, "ScriptFileName"), Property(item, "Name")));
                Read("SELECT __RELPATH,Name,Query,EventNamespace FROM __EventFilter", item =>
                    filters[Relative(Property(item, "__RELPATH"), scope)] = Property(item, "EventNamespace") + " | " + Property(item, "Query"));
                Read("SELECT __RELPATH,Filter,Consumer FROM __FilterToConsumerBinding", item =>
                {
                    if (entries.Count >= MaximumSourceEntries) return;
                    var filter = Property(item, "Filter");
                    var consumer = Property(item, "Consumer");
                    var relative = Property(item, "__RELPATH");
                    consumers.TryGetValue(Relative(consumer, scope), out var linked);
                    var condition = filters.GetValueOrDefault(Relative(filter, scope), filter);
                    var location = scope + ":" + relative;
                    entries.Add(new AutorunEntry
                    {
                        Id = AutorunIdentity.Hash("wmi|" + location), Name = linked.Name ?? consumer,
                        SourceKind = AutorunSourceKind.WmiSubscription, Scope = L.T("本机 WMI 订阅"),
                        Location = location, Command = linked.Command ?? "", Enabled = null, CanChange = false, IsSystem = true,
                        TriggerSummary = L.T("WMI 条件事件"), ReadOnlyReason = L.T(AdvancedReadOnly),
                        Details = condition + "\n" + filter + "\n" + consumer + "\n" + L.T("只读取订阅绑定；未验证条件或执行结果。脚本正文未读取。"),
                        Fingerprint = AutorunIdentity.Hash(location + "|" + filter + "|" + consumer + "|" + condition + "|" + linked.Command)
                    });
                });
                void Read(string query, Action<object> accept)
                {
                    object? results = null;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        results = ((dynamic)service).ExecQuery(query, "WQL", 48);
                        var count = 0;
                        foreach (object item in (IEnumerable)results)
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                if (++count > MaximumSourceEntries) { warnings.Add(L.T("WMI 来源达到读取上限，部分对象未显示。")); break; }
                                accept(item);
                            }
                            finally { Release(item); }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { warnings.Add(L.F($"WMI 某类对象不可读（{ex.GetType().Name}），关联信息可能不完整。")); }
                    finally { Release(results); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add(L.F($"WMI 命名空间 {scope} 不可读（{ex.GetType().Name}）。")); }
            finally { Release(service); Release(locator); }
        }
        warnings.Add(L.T("WMI 仅读取 root\\subscription 与 root\\default；其他命名空间未扫描。"));
        return new(entries, warnings.Distinct().ToArray());
    }

    private static WorkerResult ReadPackages(CancellationToken token)
    {
        var entries = new List<AutorunEntry>();
        var warnings = new List<string>();
        var count = 0;
        foreach (Package package in new PackageManager().FindPackagesForUser(string.Empty))
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaximumSourceEntries || entries.Count >= MaximumSourceEntries)
            { warnings.Add(L.T("打包启动来源达到读取上限，部分声明未显示。")); break; }
            try
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                var directory = package.InstalledLocation.Path;
                if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                using var stream = new FileStream(Path.Combine(directory, "AppxManifest.xml"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > 4 * 1024 * 1024) { warnings.Add(L.T("某个包清单过大，启动声明未读取。")); continue; }
                var name = package.DisplayName;
                if (string.IsNullOrEmpty(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) name = package.Id.Name;
                var parsed = ReadPackageStartupManifest(stream, package.Id.FamilyName, name, directory, token);
                if (parsed.Count >= 100) warnings.Add(L.T("某个包已达到 100 项启动声明读取边界。"));
                entries.AddRange(parsed.Take(MaximumSourceEntries - entries.Count)
                    .Select(entry => entry with { IsSystem = package.SignatureKind == PackageSignatureKind.System,
                        Ownership = package.SignatureKind == PackageSignatureKind.System ? AutorunOwnership.Windows : AutorunOwnership.Unknown }));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add(L.F($"某个打包启动声明不可读（{ex.GetType().Name}），已保留其他包。")); }
        }
        return new(entries, warnings.Distinct().ToArray());
    }

    /// <summary>Pure bounded manifest parsing used by inventory and isolated fixtures. Enabled is always unknown.</summary>
    public static IReadOnlyList<AutorunEntry> ReadPackageStartupManifest(Stream stream, string familyName, string displayName,
        string installLocation, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024,
            MaxCharactersFromEntities = 1024, IgnoreComments = true, IgnoreProcessingInstructions = true, CloseInput = false
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        var ns = root?.Name.NamespaceName;
        if (root?.Name.LocalName != "Package" || ns is not ("http://schemas.microsoft.com/appx/manifest/foundation/windows10" or
            "http://schemas.microsoft.com/appx/2010/manifest" or "http://schemas.microsoft.com/appx/2013/manifest"))
            throw new XmlException("Unsupported package manifest root.");
        var result = new List<AutorunEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in root.Elements(XName.Get("Applications", ns)).Elements(XName.Get("Application", ns)))
        foreach (var extension in app.Elements(XName.Get("Extensions", ns)).Elements().Where(element =>
            element.Name.LocalName == "Extension" && IsStartupNamespace(element.Name.NamespaceName) && (string?)element.Attribute("Category") == "windows.startupTask"))
        foreach (var task in extension.Elements().Where(element => element.Name.LocalName == "StartupTask" && IsStartupNamespace(element.Name.NamespaceName)))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= 100) return result.AsReadOnly();
            var id = (string?)task.Attribute("TaskId") ?? "";
            if (id.Length == 0 || !seen.Add(id)) continue;
            var appId = (string?)app.Attribute("Id") ?? "";
            var executable = (string?)extension.Attribute("Executable") ?? (string?)app.Attribute("Executable") ?? "";
            var location = familyName + "!" + appId + " | " + id;
            var target = ResolvePackagePath(installLocation, executable);
            result.Add(new AutorunEntry
            {
                Id = AutorunIdentity.Hash("package-startup|" + location), Name = displayName + " | " + id,
                SourceKind = AutorunSourceKind.PackagedStartup, Scope = L.T("当前用户的包声明"), Location = location,
                Command = executable.Length > 0 ? executable : familyName + "!" + appId, TargetPath = target,
                Enabled = null, CanChange = false, TriggerSummary = L.T("包登录启动声明"),
                ReadOnlyReason = L.T("第三方包的当前启用状态由 Windows 管理，请使用系统启动应用设置。"),
                Details = L.F($"清单声明 Enabled={((string?)task.Attribute("Enabled") ?? L.T("未声明"))}；这不代表当前启用状态。"),
                Fingerprint = AutorunIdentity.Hash(location + "|" + executable + "|" + task.ToString(SaveOptions.DisableFormatting))
            });
        }
        return result.AsReadOnly();
    }

    private static bool IsStartupNamespace(string value) => value is "http://schemas.microsoft.com/appx/manifest/uap/windows10/5" or
        "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static string ResolvePackagePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) || relative.IndexOfAny([':', '\0', '*', '?', '"', '<', '>', '|']) >= 0) return "";
        var segments = relative.Split(['\\', '/']);
        if (segments.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' '))) return "";
        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
            var path = Path.GetFullPath(Path.Combine(root, relative));
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : "";
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return ""; }
    }

    private static (string Text, int Type, string Raw)? ReadRegistryValue(RegistryKey key, string name)
    {
        uint size = 0;
        var status = RegQueryValueEx(key.Handle, name, nint.Zero, out var type, null, ref size);
        if (status == 2) return null;
        if (status != 0) throw new Win32Exception(status);
        if (size > MaximumRegistryBytes) throw new IOException("Registry value exceeds the bounded inventory size.");
        var bytes = new byte[size];
        status = RegQueryValueEx(key.Handle, name, nint.Zero, out type, bytes, ref size);
        if (status != 0) throw new Win32Exception(status); // Changed/expanded concurrently: never allocate an unbounded retry.
        var text = type is 1 or 2 or 7 ? Encoding.Unicode.GetString(bytes, 0, checked((int)size)).TrimEnd('\0').Replace('\0', '\n') : Convert.ToHexString(bytes.AsSpan(0, checked((int)size)));
        return (text, type, Convert.ToHexString(bytes.AsSpan(0, checked((int)size))));
    }
    private static IEnumerable<string> Subkeys(RegistryKey key, int maximum, Func<bool> available)
    {
        for (uint index = 0; index < maximum && available(); index++)
        {
            var name = new StringBuilder(256);
            var length = (uint)name.Capacity;
            var status = RegEnumKeyEx(key.Handle, index, name, ref length, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
            if (status == 259) yield break;
            if (status != 0) throw new Win32Exception(status);
            yield return name.ToString();
        }
    }
    private static string Relative(string path, string scope)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return path;
        var colon = path.IndexOf(':');
        var separator = path.IndexOf('\\', 2);
        if (separator < 0 || colon <= separator) return path;
        var machine = path[2..separator];
        var location = path[(separator + 1)..colon];
        // Never associate a foreign-host or foreign-namespace binding with a
        // similarly named local object, and never fetch arbitrary references.
        return (machine == "." || machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) &&
            location.Equals(scope, StringComparison.OrdinalIgnoreCase) ? path[(colon + 1)..] : path;
    }
    private static string Property(object item, string name)
    {
        object? properties = null, property = null;
        try
        {
            properties = name.StartsWith("__", StringComparison.Ordinal) ? ((dynamic)item).SystemProperties_ : ((dynamic)item).Properties_;
            property = ((dynamic)properties).Item(name);
            var text = Convert.ToString(((dynamic)property).Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return text.Length > 16384 ? text[..16384] + "…" : text;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return ""; }
        finally { Release(property); Release(properties); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or System.Security.SecurityException or Win32Exception or COMException;

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, out int type, byte[]? data, ref uint length);
    [DllImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyEx(SafeRegistryHandle key, uint index, StringBuilder name, ref uint length,
        nint reserved, nint className, nint classLength, nint lastWriteTime);
}
