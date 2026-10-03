using System.IO;

namespace ProcessKeeper.Core;

/// <summary>Classifies observed identities. An application group is not a process tree.</summary>
public static class ProcessIdentity
{
    private static readonly IReadOnlyDictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["known:clash"] = "Clash Verge", ["known:codex"] = "Codex", ["known:qq"] = "QQ",
        ["known:steam"] = "Steam", ["known:huorong"] = "火绒安全", ["known:uu"] = "UU 远程",
        ["known:translucenttb"] = "TranslucentTB", ["known:avd"] = "Android 模拟器 / AVD",
        ["known:mydock"] = "MyDockFinder"
    };

    private static readonly HashSet<string> SteamComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
        "gameoverlayui.exe", "steam_monitor.exe", "steamxboxutil.exe", "steamxboxutil64.exe"
    };

    private static readonly HashSet<string> CoreSystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "system idle process", "memory compression", "secure system",
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "lsaiso.exe", "winlogon.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "sihost.exe", "taskhostw.exe", "userinit.exe", "explorer.exe",
        "shellhost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe", "searchhost.exe", "searchindexer.exe",
        "runtimebroker.exe", "applicationframehost.exe", "ctfmon.exe", "chsime.exe", "textinputhost.exe", "tabtip.exe",
        "conhost.exe", "openconsole.exe", "audiodg.exe", "wudfhost.exe", "dllhost.exe", "unsecapp.exe",
        "securityhealthsystray.exe", "securityhealthservice.exe", "msmpeng.exe", "nissrv.exe", "mssense.exe",
        "defendersessionhelper.exe"
    };

    private static readonly HashSet<string> HuorongComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "hipsmain.exe", "hipsdaemon.exe", "hipstray.exe", "hipslog.exe", "hipsupdate.exe", "sysdiag.exe"
    };

    public static ProcessRecord Classify(ProcessRecord process, IReadOnlyDictionary<string, string>? installationRoots = null)
    {
        string name = process.Name.ToLowerInvariant();
        string path = NormalizePath(process.Path);
        string package = process.PackageFamilyName;
        string? key = null;
        string evidence = "";

        // Explicitly separate launchable applications even when installed under a kept application's directory.
        if (name is "mydock.exe" or "mydockfinder.exe" or "dock_64.exe" or "dockmod64.exe" or "dockmod.exe" || HasSegment(path, "MyDockFinder"))
            key = "known:mydock";
        else if (name is "emulator.exe" or "netsimd.exe" || name.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(@"\Android\Sdk\emulator\", StringComparison.OrdinalIgnoreCase))
            key = "known:avd";
        else if (package.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(@"\OpenAI\Codex\", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) ||
                 name == "codex.exe" || name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            key = "known:codex";
        else if (name is "clash-verge.exe" or "clash-verge-service.exe" or "verge-mihomo.exe" or "mihomo.exe" or "clash.exe" or "clash-meta.exe")
            key = "known:clash";
        else if (name is "qq.exe" or "qqexternal.exe" or "qqprotect.exe" or "qqprotectengine.exe" or "qqcrashreport.exe" or "txplatform.exe")
            key = "known:qq";
        else if (SteamComponents.Contains(name) && !HasSegment(path, "steamapps"))
            key = "known:steam";
        else if (HuorongComponents.Contains(name) || HasSegment(path, "Sysdiag"))
            key = "known:huorong";
        else if (name.StartsWith("gameviewer", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || HasSegment(path, "GameViewer"))
            key = "known:uu";
        else if (name == "translucenttb.exe" || package.StartsWith("28017CharlesMilette.TranslucentTB_", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(@"\WindowsApps\28017CharlesMilette.TranslucentTB_", StringComparison.OrdinalIgnoreCase))
            key = "known:translucenttb";

        if (key is not null)
        {
            evidence = package.Length > 0 ? L.F($"已知程序映射；Windows 包族：{package}") :
                path.Length > 0 ? L.F($"已知程序名称 / 路径映射：{process.Name}；{process.Path}") :
                L.F($"按已知组件名称识别：{process.Name}；无法读取路径，归属未充分核实");
        }
        else if (installationRoots is not null && path.Length > 0)
        {
            // These are roots inferred only from known main executable locations, never from process ancestry.
            var root = installationRoots.OrderByDescending(x => x.Key.Length)
                .FirstOrDefault(x => IsUnderDirectory(path, x.Key) && x.Value != "known:steam");
            if (!string.IsNullOrEmpty(root.Key))
            {
                key = root.Value;
                evidence = L.F($"位于已知程序安装目录：{root.Key}；按目录推断组件归属，不依据父子关系");
            }
        }

        if (key is null && package.Length > 0)
        {
            key = "package:" + package.ToLowerInvariant();
            evidence = L.F($"Windows 包族相同：{package}；包内可包含多个入口");
        }
        if (key is null && path.Length > 0)
        {
            key = "path:" + path.ToLowerInvariant();
            evidence = L.T("按同一可执行文件完整路径分组；不自动合并父子进程或同名文件");
        }
        if (key is null)
        {
            key = $"unknown:{process.Id}:{process.StartTimeUtcTicks}";
            evidence = L.T("无法读取可执行文件路径及包身份；单独显示，不推断程序归属");
        }

        bool system = process.IsSystem || process.NativeCritical == true || process.Id is 0 or 4;
        string systemReason = process.SystemReason;
        if (system && systemReason.Length == 0) systemReason = L.T("Windows 核心、桌面、安全或驱动基础组件");
        return process with
        {
            ApplicationKey = key,
            ApplicationName = KnownNames.TryGetValue(key, out string? known) ? key == "known:avd" ? L.T("Android 模拟器 / AVD") : known : FriendlyName(process),
            IdentityEvidence = evidence,
            RoleDescription = DescribeRole(process, key),
            IsSystem = system,
            SystemReason = systemReason
        };
    }

    internal static bool IsWindowsComponentName(string name) => CoreSystemNames.Contains(name);

    public static IReadOnlyList<ProcessRecord> ClassifyAll(IEnumerable<ProcessRecord> source)
    {
        var records = source.ToArray();
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProcessRecord record in records)
        {
            string name = record.Name.ToLowerInvariant();
            string? key = name switch
            {
                "clash-verge.exe" => "known:clash", "qq.exe" => "known:qq", "codex.exe" => "known:codex",
                "hipstray.exe" or "hipsmain.exe" => "known:huorong", "gameviewer.exe" => "known:uu", _ => null
            };
            if (key is null || record.Path.Length == 0) continue;
            string? root = Path.GetDirectoryName(NormalizePath(record.Path));
            // Never make a drive root, a generic apps folder, or a Windows folder an implicit app rule.
            if (string.IsNullOrWhiteSpace(root) || root.Length <= 3 ||
                string.Equals(root.TrimEnd('\\'), Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                IsGenericContainer(root)) continue;
            roots.TryAdd(root, key);
        }

        var classified = records.Select(p => Classify(p, roots)).ToArray();
        var byId = classified.ToDictionary(p => p.Id);
        return classified.Select(p =>
        {
            bool verified = p.ParentId != p.Id && byId.TryGetValue(p.ParentId, out var parent) &&
                parent.StartTimeUtcTicks > 0 && p.StartTimeUtcTicks > 0 && parent.StartTimeUtcTicks <= p.StartTimeUtcTicks;
            string explanation;
            if (p.ParentId == 0) explanation = L.T("无可核实的父进程；PID 0 为系统占位");
            else if (p.ParentId == p.Id) explanation = L.T("父进程记录指向自身，未建立启动关系");
            else if (!byId.TryGetValue(p.ParentId, out var parentRecord)) explanation = L.F($"创建者 PID {p.ParentId} 已退出或不在本次快照中");
            else if (parentRecord.StartTimeUtcTicks <= 0 || p.StartTimeUtcTicks <= 0) explanation = L.F($"记录的父 PID 为 {p.ParentId}，但启动时间不可读，关系未核实");
            else if (!verified) explanation = L.F($"当前 PID {p.ParentId} 启动晚于该进程，原父 PID 已复用；不建立关系");
            else explanation = L.F($"由 {parentRecord.Name}（PID {p.ParentId}）启动；创建时间顺序已核对。启动关系不等于同一程序");
            return p with { ParentIdentityVerified = verified && p.ParentId != 0, ParentEvidence = explanation };
        }).ToArray();
    }

    public static IReadOnlyList<ApplicationGroup> Group(IEnumerable<ProcessRecord> processes) =>
        processes.GroupBy(p => p.ApplicationKey, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var members = group.OrderByDescending(p => p.HasVisibleWindow).ThenBy(p => p.StartTimeUtcTicks).ThenBy(p => p.Id).ToArray();
            var representative = members.FirstOrDefault(p => p.Company.Length > 0) ?? members[0];
            return new ApplicationGroup
            {
                Key = group.Key, Name = members[0].ApplicationName, Company = representative.Company,
                Description = members.FirstOrDefault(p => p.Description.Length > 0)?.Description ?? members[0].RoleDescription,
                IdentityEvidence = string.Join("\n", members.Select(p => p.IdentityEvidence).Distinct(StringComparer.Ordinal).Take(4)),
                Processes = members
            };
        }).OrderBy(g => g.Category).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static bool IsUnderDirectory(string path, string directory) =>
        !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(directory) &&
        NormalizePath(path).StartsWith(NormalizePath(directory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.Replace('/', '\\');
    private static bool HasSegment(string path, string segment) => path.Contains("\\" + segment + "\\", StringComparison.OrdinalIgnoreCase);
    private static bool IsGenericContainer(string root)
    {
        string name = Path.GetFileName(root.TrimEnd('\\'));
        return name.Equals("Windows", StringComparison.OrdinalIgnoreCase) || name.Equals("System32", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SysWOW64", StringComparison.OrdinalIgnoreCase) || name.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase) || name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Desktop", StringComparison.OrdinalIgnoreCase) || name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Documents", StringComparison.OrdinalIgnoreCase) || name.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Roaming", StringComparison.OrdinalIgnoreCase) || name.Equals("Temp", StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyName(ProcessRecord process) => !string.IsNullOrWhiteSpace(process.ProductName) ? process.ProductName :
        !string.IsNullOrWhiteSpace(process.Description) ? process.Description :
        !string.IsNullOrWhiteSpace(process.Name) ? Path.GetFileNameWithoutExtension(process.Name) : L.F($"进程 {process.Id}");

    private static string DescribeRole(ProcessRecord p, string key)
    {
        string name = p.Name.ToLowerInvariant();
        if (p.Services.Count > 0) return L.T("Windows 服务宿主：") + string.Join("、", p.Services.Select(s => s.DisplayName));
        if (name.Contains("crashpad", StringComparison.OrdinalIgnoreCase) || name.Contains("crashreport", StringComparison.OrdinalIgnoreCase) || name.Contains("errorreporter", StringComparison.OrdinalIgnoreCase))
            return L.T("崩溃检测 / 错误报告组件（按文件名称识别）");
        if (name is "steamwebhelper.exe" or "msedgewebview2.exe") return L.T("网页界面渲染 / WebView 子进程；可能有多个实例");
        if (name is "node.exe" or "node") return L.T("Node.js JavaScript 运行时；归属依据安装路径，不能仅凭名称判断用途");
        if (name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return L.T("Python 脚本运行时；未读取命令行，具体脚本未知");
        if (name is "cmd.exe" or "pwsh.exe" or "powershell.exe") return L.T("命令行解释器 / 终端任务；不是父程序的当然组成部分");
        if (name.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase)) return L.T("Android 模拟器虚拟机核心（QEMU）");
        if (name == "netsimd.exe") return L.T("Android 模拟器网络 / 设备模拟组件");
        if (name == "emulator.exe") return L.T("Android 虚拟设备启动器 / 模拟器");
        if (name is "mihomo.exe" or "verge-mihomo.exe" or "clash.exe" or "clash-meta.exe") return L.T("Clash Verge 代理内核 / 网络转发组件");
        if (name == "gameviewerserver.exe") return L.T("UU 远程桌面连接服务端组件");
        if (name == "gameviewerhealthd.exe") return L.T("UU 远程运行状态监护组件");
        if (name is "hipsdaemon.exe" or "hipsmain.exe") return L.T("火绒安全防护后台组件");
        if (name == "hipstray.exe") return L.T("火绒安全托盘 / 控制界面");
        if (key == "known:mydock") return L.T("MyDockFinder 桌面 Dock / 菜单栏美化组件");
        if (key == "known:translucenttb") return L.T("任务栏透明与外观调整程序");
        if (key == "known:qq") return L.T("QQ 主程序或其多进程界面组件；具体实例角色未核实");
        if (key == "known:codex") return L.T("Codex 客户端、运行时或工具组件；具体归属见识别依据");
        if (p.Description.Length > 0) return p.Description + L.T("（文件版本信息声明）");
        return p.Windows.Any(w => w.IsVisible && !w.IsCloaked) ? L.T("拥有桌面窗口的程序进程") :
            L.T("未发现可见顶层窗口；可能是后台任务或辅助组件，不能据此断言为托盘程序");
    }
}
