using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

/// <summary>Each entry describes one application's own GUI entry point, never a shell command.</summary>
public static class SpecialWindowPolicy
{
    private static readonly Dictionary<string, (string Name, string Entry, string[] Arguments)> TrayEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["clash-verge.exe"] = ("Clash Verge", "clash-verge.exe", []),
        ["steam.exe"] = ("Steam", "steam.exe", ["-open", "steam://open/main"]),
        ["qq.exe"] = ("QQ", "QQ.exe", []),
        ["gameviewer.exe"] = ("UU 远程", "GameViewer.exe", []),
        ["hipstray.exe"] = ("火绒安全", "HipsMain.exe", []),
        ["hipsmain.exe"] = ("火绒安全", "HipsMain.exe", []),
        ["docker desktop.exe"] = ("Docker Desktop", "Docker Desktop.exe", []),
        ["com.docker.backend.exe"] = ("Docker Desktop", "..\\..\\Docker Desktop.exe", []),
        ["qbittorrent.exe"] = ("qBittorrent", "qbittorrent.exe", []),
        ["telegram.exe"] = ("Telegram", "Telegram.exe", []),
        ["discord.exe"] = ("Discord", "Discord.exe", []),
        ["wechat.exe"] = ("微信", "WeChat.exe", []),
        ["weixin.exe"] = ("微信", "Weixin.exe", []),
        ["synctrayzor.exe"] = ("SyncTrayzor", "SyncTrayzor.exe", [])
    };

    public static bool IsSupportedName(string name) => TrayEntries.ContainsKey(name) ||
        name.Equals("VBoxHeadless.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vmware-vmx.exe", StringComparison.OrdinalIgnoreCase);

    public static SpecialWindowPlan? Prepare(ProcessRecord source, IReadOnlyList<string> arguments, ISpecialWindowHost host)
    {
        if (source.Id <= 4 || source.StartTimeUtcTicks <= 0 || !Path.IsPathFullyQualified(source.Path) ||
            !Path.GetFileName(source.Path).Equals(source.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T("进程身份不完整，请刷新后重试。"));
        var directory = Path.GetDirectoryName(source.Path)!;
        if (source.Name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase))
        {
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmwp.exe");
            if (!SamePath(expected, source.Path) || source.SessionId != 0) return null;
            var vm = host.ReadVirtualMachine(source, SpecialWindowKind.HyperV, "") ??
                throw new InvalidOperationException(L.T("Hyper-V 未返回与该核心对应的运行中虚拟机。"));
            if (vm.ProcessId != source.Id || !Guid.TryParse(vm.Id, out var id) || string.IsNullOrWhiteSpace(vm.Name))
                throw new InvalidOperationException(L.T("无法唯一核实 Hyper-V 虚拟机。"));
            return Plan(SpecialWindowKind.HyperV, "Hyper-V | " + vm.Name, L.T("打开该虚拟机的 VMConnect 连接窗口。"), source, [],
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe"),
                ["localhost", "-G", id.ToString("D")], id.ToString("D"), vm.Name, host);
        }
        if (arguments.Count == 0 || !SamePath(arguments[0], source.Path))
            throw new InvalidOperationException(L.T("无法核实原进程启动入口。"));
        if (source.Name.Equals("VBoxHeadless.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? target = null;
            for (int i = 1; i < arguments.Count; i++)
                if (arguments[i] is "--startvm" or "-startvm" or "-s")
                {
                    if (target is not null || ++i >= arguments.Count) throw new InvalidOperationException(L.T("VirtualBox 设备参数不唯一。"));
                    target = arguments[i];
                }
            if (!Guid.TryParse(target, out var id)) throw new InvalidOperationException(L.T("没有找到明确的 VirtualBox 设备 UUID。"));
            var vm = host.ReadVirtualMachine(source, SpecialWindowKind.VirtualBox, id.ToString("D")) ??
                throw new InvalidOperationException(L.T("无法核实 VirtualBox 设备与后台核心的对应关系。"));
            if (vm.ProcessId != source.Id || !Guid.TryParse(vm.Id, out var actual) || actual != id || string.IsNullOrWhiteSpace(vm.Name))
                throw new InvalidOperationException(L.T("VirtualBox 设备身份发生变化。"));
            return Plan(SpecialWindowKind.VirtualBox, "VirtualBox | " + vm.Name, L.T("以独立界面连接现有虚拟机；不发送开机或重启命令。"), source, arguments,
                Path.Combine(directory, "VirtualBoxVM.exe"), ["--startvm", id.ToString("D"), "--separate"], id.ToString("D"), vm.Name, host);
        }
        if (source.Name.Equals("vmware-vmx.exe", StringComparison.OrdinalIgnoreCase))
        {
            var paths = arguments.Skip(1).Where(arg => arg.EndsWith(".vmx", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length != 1 || !Path.IsPathFullyQualified(paths[0]) || !host.FileExists(paths[0]))
                throw new InvalidOperationException(L.T("无法唯一核实 VMware 虚拟机配置文件。"));
            // Open the existing configuration, without -x/-X (power-on options).
            var frontend = Path.Combine(directory, "vmware.exe");
            if (!host.FileExists(frontend) && new DirectoryInfo(directory).Name.Equals("x64", StringComparison.OrdinalIgnoreCase))
                frontend = Path.Combine(Path.GetDirectoryName(directory)!, "vmware.exe");
            var vm = host.ReadVirtualMachine(source, SpecialWindowKind.VMware, paths[0]);
            if (vm is null || vm.ProcessId != source.Id || !SamePath(vm.Id, paths[0]) || string.IsNullOrWhiteSpace(vm.Name))
                throw new InvalidOperationException(L.T("无法核实 VMware 配置中的设备名称。"));
            return Plan(SpecialWindowKind.VMware, "VMware | " + vm.Name,
                L.T("在 VMware 中打开这份运行中的虚拟机配置。窗口仍需核验；不会执行开机、关机或重启。"), source, arguments,
                frontend, [paths[0]], paths[0], vm.Name, host);
        }
        if (!TrayEntries.TryGetValue(source.Name, out var entry)) return null;
        // A renderer/service cannot be used as a GUI launcher just because its name matches.
        if (arguments.Skip(1).Any(arg => arg.StartsWith("--type", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--service", StringComparison.OrdinalIgnoreCase))) return null;
        var path = Path.GetFullPath(Path.Combine(directory, entry.Entry));
        if (source.Name.Equals("com.docker.backend.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Docker versions place the backend either in resources or resources/bin.
            var direct = Path.GetFullPath(Path.Combine(directory, "..", "Docker Desktop.exe"));
            if (host.FileExists(direct)) path = direct;
        }
        return Plan(SpecialWindowKind.TrayApplication, entry.Name, L.T("重新调用该程序的界面入口，尝试唤回主窗口。部分版本可能打开登录页或新实例。"),
            source, arguments, path, entry.Arguments, "", "", host);
    }

    private static SpecialWindowPlan Plan(SpecialWindowKind kind, string name, string explanation, ProcessRecord source,
        IReadOnlyList<string> original, string executable, IReadOnlyList<string> arguments, string target, string title, ISpecialWindowHost host)
    {
        if (!host.FileExists(executable) || !host.IsGuiExecutable(executable))
            throw new InvalidOperationException(L.T("未找到可核实的图形界面入口；不会改用命令行窗口。"));
        return new(kind, name, explanation, source, Array.AsReadOnly(original.ToArray()), executable,
            Array.AsReadOnly(arguments.ToArray()), target, title, host.FileHash(executable));
    }

    public static bool IsRealGuiWindow(WindowRecord window) => window.Handle != 0 && window.IsTopLevel &&
        window.IsVisible && !window.IsMinimized && !window.IsCloaked && !window.IsToolWindow && window.HasUsableBounds &&
        (window.HasCaption || window.HasAppWindowStyle) && !string.IsNullOrWhiteSpace(window.Title) &&
        !new[] { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "#32770", "#32768", "tooltips_class32" }
            .Contains(window.ClassName, StringComparer.OrdinalIgnoreCase);

    public static bool SamePath(string left, string right) => ProtectionPolicy.TryNormalizePath(left, out var a) &&
        ProtectionPolicy.TryNormalizePath(right, out var b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
