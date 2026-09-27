using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ProcessKeeper.Core;

/// <summary>Bounded local inventory. Configuration presence does not guarantee a program will start.</summary>
public sealed class AutorunCatalog
{
    public const int MaximumEntries = 10000;
    public const int MaximumScanSeconds = 90;
    private readonly AutorunWindowsBackend _backend = new();
    private readonly AutorunBackupStore _backups;
    public AutorunCatalog(string? backupDirectory = null) => _backups = new(backupDirectory);

    public AutorunSnapshot Scan(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var result = new Dictionary<string, AutorunEntry>(StringComparer.Ordinal);
        var warnings = new List<string>();
        bool Available() => result.Count < MaximumEntries && clock.Elapsed < TimeSpan.FromSeconds(MaximumScanSeconds);
        void ReadSource(Func<IReadOnlyList<AutorunEntry>> scan)
        {
            if (!Available()) return;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in scan())
                { cancellationToken.ThrowIfCancellationRequested(); if (result.Count >= MaximumEntries) break; result[entry.Id] = entry; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warnings.Add(L.T("部分自启动来源未能读取：") + ex.Message); }
        }
        ReadSource(() => RegistryEntries(warnings, cancellationToken, Available));
        ReadSource(() => FolderEntries(warnings, cancellationToken, Available));
        ReadSource(() => TaskEntries(warnings, cancellationToken, Available));
        ReadSource(() => ServiceEntries(warnings, cancellationToken, Available));
        ReadSource(() => AutorunAdvancedScanner.Scan(warnings, cancellationToken, Available));
        try
        {
            foreach (var backup in _backups.ReadAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Available()) break;
                if (backup.Disabled.Enabled != false) continue;
                try
                {
                    var live = _backend.Read(backup.Entry, cancellationToken);
                    if (live.Fingerprint != backup.Disabled.Fingerprint) continue;
                    if (!backup.Entry.CanChange || backup.Entry.IsSystem || backup.Entry.Fingerprint != backup.Original.Fingerprint) continue;
                    var current = result.GetValueOrDefault(backup.Entry.Id) ?? backup.Entry;
                    result[backup.Entry.Id] = AutorunStartupApproval.Apply(current with { Enabled = false, Fingerprint = live.Fingerprint,
                        CanChange = true, ReadOnlyReason = "", Details = current.Details + "\n" + L.T("由 Process Keeper 禁用；保留了可核对的原始配置备份。") });
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Missing or externally changed native identity is never recreated from a backup automatically. */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add(L.T("自启动备份未能完整读取：") + ex.Message); }
        var truncated = !Available();
        if (truncated) warnings.Add(L.F($"扫描达到 {MaximumEntries} 项或 {MaximumScanSeconds} 秒上限。正在执行的系统读取可能稍后返回。"));
        warnings.Add(L.T("仅显示当前权限下可读取的来源；未加载的用户配置及部分系统扩展不在范围内。启用状态表示该来源的配置状态，启动审批、策略、触发条件仍可能阻止运行。"));
        var classified = AutorunOwnershipClassifier.Shared.Classify(result.Values.ToArray(), cancellationToken,
            () => clock.Elapsed < TimeSpan.FromSeconds(MaximumScanSeconds));
        return new() { Entries = classified.OrderBy(entry => entry.SourceKind).ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).Take(100).ToArray(), Truncated = truncated, CapturedAt = DateTimeOffset.Now };
    }

    private static IReadOnlyList<AutorunEntry> RegistryEntries(List<string> warnings, CancellationToken token, Func<bool> available)
    {
        var result = new List<AutorunEntry>();
        var scopes = new List<(string Hive, string Prefix, string Label)> { ("HKCU", "", L.T("当前用户")), ("HKLM", "", L.T("所有用户")) };
        var currentSid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            foreach (var sid in users.GetSubKeyNames().Where(value => value.StartsWith("S-1-5-", StringComparison.Ordinal) &&
                !value.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) && value != currentSid).Take(64)) scopes.Add(("HKU", sid + "\\", sid));
        foreach (var scope in scopes)
        foreach (var view in new[] { 64, 32 })
        foreach (var source in new[] { (AutorunSourceKind.RegistryRun, AutorunWindowsBackend.RunPath), (AutorunSourceKind.RegistryRunOnce, AutorunWindowsBackend.RunOncePath), (AutorunSourceKind.PolicyRun, AutorunWindowsBackend.PolicyPath) })
        {
            token.ThrowIfCancellationRequested(); if (!available() || result.Count >= 3000) return result;
            var locator = new AutorunLocator { Hive = scope.Hive, View = view, Path = scope.Prefix + source.Item2 };
            try
            {
                using var root = AutorunWindowsBackend.OpenHive(locator);
                using var key = root.OpenSubKey(locator.Path);
                if (key is null) continue;
                if (key.ValueCount > 3000) { warnings.Add(L.T("某个注册表启动来源超过读取上限，已跳过。")); continue; }
                foreach (var name in key.GetValueNames())
                {
                    token.ThrowIfCancellationRequested(); if (!available() || result.Count >= 3000) return result;
                    if (name.Length == 0) continue;
                    var location = locator with { ValueName = name };
                    var entry = new AutorunEntry { Id = AutorunIdentity.Id(source.Item1, location), Name = name, SourceKind = source.Item1,
                        Scope = scope.Label, Location = $"{scope.Hive} | {view}-bit | {locator.Path} | {name}", Locator = location,
                        TriggerSummary = source.Item1 == AutorunSourceKind.RegistryRunOnce ? L.T("下次登录时运行一次") : L.T("用户登录") };
                    try
                    {
                        var state = AutorunWindowsBackend.ReadRegistry(entry);
                        var command = AutorunWindowsBackend.RegistryCommand(state);
                        var target = AutorunWindowsBackend.ExtractTarget(command);
                        var system = AutorunWindowsBackend.IsSystemTarget(target) || scope.Label is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";
                        var policy = source.Item1 == AutorunSourceKind.PolicyRun;
                        var otherUser = scope.Hive == "HKU";
                        result.Add(AutorunStartupApproval.Apply(entry with { Command = command, TargetPath = target, Enabled = true, Fingerprint = state.Fingerprint,
                            IsSystem = system, CanChange = !system && !policy && !otherUser,
                            ReadOnlyReason = policy ? L.T("此入口由组策略管理，仅供查看。") : system ? L.T("系统登录组件，仅供查看。") : otherUser ? L.T("其他用户的登录配置，仅供查看。") : "",
                            Details = L.T("保留原始命令、环境变量、值名称及注册表值类型；禁用只移除此值，恢复时不会覆盖已有值。") }));
                    }
                    catch (Exception ex) { result.Add(entry with { Enabled = null, CanChange = false, ReadOnlyReason = ex.Message }); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(L.T("部分注册表启动项未能读取：") + ex.Message); }
        }
        return result;
    }

    private IReadOnlyList<AutorunEntry> FolderEntries(List<string> warnings, CancellationToken token, Func<bool> available)
    {
        var result = new List<AutorunEntry>();
        foreach (var root in AutorunWindowsBackend.StartupDirectories())
        {
            token.ThrowIfCancellationRequested(); if (!available()) break;
            try
            {
                if (!Directory.Exists(root.Path)) continue;
                AutorunPathSafety.RejectReparseAncestors(root.Path);
                foreach (var path in Directory.EnumerateFiles(root.Path).Take(1000))
                {
                    token.ThrowIfCancellationRequested(); if (!available() || result.Count >= 2000) return result;
                    if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var locator = new AutorunLocator { Path = path };
                    var entry = new AutorunEntry { Id = AutorunIdentity.Id(AutorunSourceKind.StartupFolder, locator), Name = Path.GetFileName(path),
                        SourceKind = AutorunSourceKind.StartupFolder, Scope = root.Scope, Location = path, Command = path, Locator = locator,
                        TargetPath = AutorunWindowsBackend.ShortcutTarget(path), TriggerSummary = L.T("用户登录") };
                    try
                    {
                        var state = _backend.Read(entry, token);
                        var system = AutorunWindowsBackend.IsSystemTarget(entry.TargetPath);
                        var otherUser = root.Scope.StartsWith("S-1-5-", StringComparison.Ordinal);
                        result.Add(AutorunStartupApproval.Apply(entry with { Enabled = true, IsSystem = system, CanChange = !system && !otherUser, Fingerprint = state.Fingerprint,
                            ReadOnlyReason = system ? L.T("系统登录组件，仅供查看。") : otherUser ? L.T("其他用户的登录配置，仅供查看。") : "",
                            Details = L.T("只读取启动文件；禁用前保存文件内容、时间、属性及访问权限，不解析迁移链接或运行目标。") }));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { result.Add(entry with { Enabled = null, ReadOnlyReason = ex.Message }); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(L.T("部分启动文件夹未能读取：") + ex.Message); }
        }
        return result;
    }

    private static IReadOnlyList<AutorunEntry> TaskEntries(List<string> warnings, CancellationToken token, Func<bool> available)
    {
        var result = new List<AutorunEntry>();
        object? service = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException());
            ((dynamic)service!).Connect();
            var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue(("\\", 0));
            var foldersRead = 0;
            while (queue.Count > 0 && foldersRead++ < 1000 && result.Count < 4000 && available())
            {
                token.ThrowIfCancellationRequested();
                var current = queue.Dequeue(); object? folder = null, tasks = null, children = null;
                try
                {
                    folder = ((dynamic)service).GetFolder(current.Path);
                    tasks = ((dynamic)folder).GetTasks(1); // TASK_ENUM_HIDDEN
                    var count = Math.Min((int)((dynamic)tasks).Count, 4000);
                    for (var i = 1; i <= count && result.Count < 4000 && available(); i++)
                    {
                        token.ThrowIfCancellationRequested(); object? task = null;
                        try
                        {
                            task = ((dynamic)tasks)[i]; string path = ((dynamic)task).Path; string name = ((dynamic)task).Name; string xml = ((dynamic)task).Xml;
                            var state = AutorunWindowsBackend.ReadTask((dynamic)task);
                            var doc = XDocument.Parse(xml);
                            var actions = doc.Descendants().Where(node => node.Name.LocalName == "Exec").Select(node =>
                                string.Join(" ", node.Elements().Where(child => child.Name.LocalName is "Command" or "Arguments").Select(child => child.Value))).ToArray();
                            var commands = doc.Descendants().Where(node => node.Name.LocalName == "Command").Select(node => AutorunWindowsBackend.ExtractTarget(node.Value)).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var principal = doc.Descendants().FirstOrDefault(node => node.Name.LocalName is "UserId" or "GroupId")?.Value ?? "";
                            var triggerNames = doc.Descendants().Where(node => node.Name.LocalName == "Triggers").Elements().Select(node => node.Name.LocalName).Distinct().ToArray();
                            var hidden = doc.Descendants().Any(node => node.Name.LocalName == "Hidden" && node.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
                            var system = AutorunWindowsBackend.TaskIsSystem(path, xml);
                            var locator = new AutorunLocator { Path = path };
                            result.Add(new() { Id = AutorunIdentity.Id(AutorunSourceKind.ScheduledTask, locator), Name = name,
                                SourceKind = AutorunSourceKind.ScheduledTask, Scope = principal, Location = path, Locator = locator, Enabled = state.Enabled,
                                Command = actions.Length > 0 ? string.Join("\n", actions) : L.T("COM 或其他任务动作"), TargetPath = commands.Length == 1 ? commands[0] : "",
                                IsSystem = system, IsHidden = hidden, CanChange = !system, Fingerprint = state.Fingerprint,
                                TriggerSummary = triggerNames.Length > 0 ? string.Join(" | ", triggerNames.Select(TriggerLabel)) : L.T("未配置触发器，可由其他程序调用"),
                                ReadOnlyReason = system ? L.T("系统计划任务，仅供查看。") : "",
                                Details = L.T("只改变任务的启用标记；不会运行、停止或重新注册任务。计划任务可能由登录、开机、时间或其他事件触发。") });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(L.T("部分计划任务未能读取：") + ex.Message); }
                        finally { AutorunWindowsBackend.ReleaseCom(task); }
                    }
                    if (current.Depth < 32)
                    {
                        children = ((dynamic)folder).GetFolders(0);
                        for (var i = 1; i <= Math.Min((int)((dynamic)children).Count, 1000) && queue.Count < 1000; i++)
                        {
                            token.ThrowIfCancellationRequested(); object? child = null;
                            try { child = ((dynamic)children)[i]; queue.Enqueue(((string)((dynamic)child).Path, current.Depth + 1)); }
                            finally { AutorunWindowsBackend.ReleaseCom(child); }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(L.T("部分任务目录未能读取：") + ex.Message); }
                finally { AutorunWindowsBackend.ReleaseCom(children); AutorunWindowsBackend.ReleaseCom(tasks); AutorunWindowsBackend.ReleaseCom(folder); }
            }
            if (result.Count >= 4000 || foldersRead >= 1000) warnings.Add(L.T("计划任务达到数量或目录上限，部分任务未读取。"));
        }
        finally { AutorunWindowsBackend.ReleaseCom(service); }
        return result;
    }

    private IReadOnlyList<AutorunEntry> ServiceEntries(List<string> warnings, CancellationToken token, Func<bool> available)
    {
        var result = new List<AutorunEntry>();
        using var root = Registry.LocalMachine.OpenSubKey(AutorunWindowsBackend.ServicesPath);
        if (root is null) return result;
        foreach (var name in root.GetSubKeyNames().Take(6000))
        {
            token.ThrowIfCancellationRequested(); if (!available() || result.Count >= 4000) break;
            try
            {
                using var key = root.OpenSubKey(name);
                if (key is null || key.GetValue("Start") is not int start || key.GetValue("Type") is not int type) continue;
                using var trigger = key.OpenSubKey("TriggerInfo");
                var driver = (type & 3) != 0;
                if (!driver && start is not 2 and not 4 && trigger is null) continue;
                var command = key.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var target = AutorunWindowsBackend.ExtractTarget(command);
                var locator = new AutorunLocator { Hive = "HKLM", Path = AutorunWindowsBackend.ServicesPath, ValueName = name };
                var kind = driver ? AutorunSourceKind.Driver : AutorunSourceKind.Service;
                var entry = new AutorunEntry { Id = AutorunIdentity.Id(kind, locator), Name = key.GetValue("DisplayName") as string ?? name,
                    ServiceName = name, SourceKind = kind, Scope = key.GetValue("ObjectName") as string ?? L.T("系统"),
                    Location = "HKLM\\" + AutorunWindowsBackend.ServicesPath + "\\" + name, Locator = locator, Command = command, TargetPath = target,
                    IsSystem = driver || AutorunWindowsBackend.IsSystemTarget(target), Enabled = start != 4,
                    TriggerSummary = start switch { 0 => L.T("引导加载"), 1 => L.T("系统加载"), 2 => (key.GetValue("DelayedAutoStart") is int delay && delay != 0) ? L.T("自动（延迟启动）") : L.T("自动启动"), 4 => L.T("已禁用"), _ => trigger is not null ? L.T("按触发条件启动") : L.T("按需加载") },
                    ReadOnlyReason = driver ? L.T("驱动影响系统启动，仅供查看。") : L.T("系统服务或身份未能充分核实，仅供查看。") };
                if (!driver && !entry.IsSystem)
                {
                    try
                    {
                        var state = _backend.Read(entry, token);
                        var config = AutorunWindowsBackend.ServiceDetails(state);
                        var mutable = AutorunWindowsBackend.MutableService(config);
                        entry = entry with { Name = config.DisplayName.Length > 0 ? config.DisplayName : name, Command = config.Binary,
                            TargetPath = AutorunWindowsBackend.ExtractTarget(config.Binary), Enabled = state.Enabled, Fingerprint = state.Fingerprint,
                            IsSystem = !mutable, CanChange = mutable && config.Start != 4,
                            ReadOnlyReason = !mutable ? L.T("系统服务或身份未能充分核实，仅供查看。") : config.Start == 4 ? L.T("没有本程序保存的原始启动方式，暂不恢复。") : "",
                            Details = L.T("只修改服务启动方式，不启动或停止服务。恢复保留原始自动、延迟与触发配置；驱动和受保护服务不修改。") };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { entry = entry with { ReadOnlyReason = L.T("服务配置无法完整核实：") + ex.Message }; }
                }
                result.Add(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add(L.T("部分服务或驱动未能读取：") + ex.Message); }
        }
        return result;
    }

    private static string TriggerLabel(string value) => value switch
    {
        "BootTrigger" => L.T("系统启动"), "LogonTrigger" => L.T("用户登录"), "CalendarTrigger" => L.T("日历计划"), "TimeTrigger" => L.T("指定时间"),
        "EventTrigger" => L.T("系统事件"), "IdleTrigger" => L.T("空闲"), "RegistrationTrigger" => L.T("任务注册"),
        "SessionStateChangeTrigger" => L.T("会话状态变化"), _ => value
    };
}
