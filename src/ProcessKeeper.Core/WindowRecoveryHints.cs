namespace ProcessKeeper.Core;

public enum WindowRecoveryKind { HiddenWindow, AvdRestart, VirtualMachine, TrayApplication, BrowserInspection }

public sealed record WindowRecoveryHint(WindowRecoveryKind Kind, string Text, string Tooltip);

/// <summary>Candidate labels from the captured snapshot only. No native inspection or process operations.</summary>
public static class WindowRecoveryHints
{
    private static string Verification => L.T("这是当前快照中的候选能力；操作前会重新核实身份与条件，不保证一定显示成功。");

    public static WindowRecoveryHint? ForProcess(ProcessRecord process, ProcessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!HasIdentity(process, snapshot) || process.IsSelf) return null;
        var current = snapshot.Processes.Where(candidate => candidate.Id == process.Id).Take(2).ToArray();
        if (current.Length != 1 || current[0].StartTimeUtcTicks != process.StartTimeUtcTicks ||
            !SpecialWindowPolicy.SamePath(current[0].Path, process.Path)) return null;

        // Hyper-V's worker is the one supported Session 0 / system-account exception.
        if (process.Name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase))
            return process.SessionId == 0 && (process.OwnerSid.Equals("S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
                process.OwnerSid.StartsWith("S-1-5-83-", StringComparison.OrdinalIgnoreCase)) &&
                ProtectionPolicy.TryNormalizePath(process.Path, out var vmwpPath) &&
                vmwpPath.EndsWith(@"\System32\vmwp.exe", StringComparison.OrdinalIgnoreCase)
                ? Hint(WindowRecoveryKind.VirtualMachine, L.T("可连接画面"), L.T("可检查对应的 Hyper-V 设备并打开连接画面；不会因此重启虚拟机。")) : null;

        if (process.IsSystem || process.SessionId <= 0 || process.Services.Count > 0) return null;
        var selves = snapshot.Processes.Where(candidate => candidate.IsSelf).Take(2).ToArray();
        if (selves.Length != 1 || !HasIdentity(selves[0], snapshot) || selves[0].SessionId <= 0 ||
            process.SessionId != selves[0].SessionId ||
            !process.OwnerSid.Equals(selves[0].OwnerSid, StringComparison.OrdinalIgnoreCase)) return null;

        if (process.Windows.Any(window => !window.IsVisible && WindowActions.IsCandidate(process, window, includeHidden: true)))
            return Hint(WindowRecoveryKind.HiddenWindow, L.T("可恢复窗口"), L.T("快照中存在符合条件的隐藏主窗口，可尝试重新显示。系统会再次核实窗口和所属进程。"));

        // Minimized windows already have a normal restore route; avoid presenting a new-launch hint.
        if (process.Windows.Any(window => window.IsVisible && WindowActions.IsCandidate(process, window))) return null;
        if (process.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase) &&
            !HasAncestor(process, snapshot, ancestor => IsAvdEntry(ancestor)))
            return Hint(WindowRecoveryKind.AvdRestart, L.T("需确认重启"), L.T("可检查 AVD 原生窗口重启条件；必须核实启动器、设备与启动参数，并单独确认。重启会中断当前模拟器会话。"));
        if (IsQemu(process) && process.ApplicationKey.Equals("known:avd", StringComparison.OrdinalIgnoreCase) &&
            HasAncestor(process, snapshot, ancestor => ancestor.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase)))
            return Hint(WindowRecoveryKind.AvdRestart, L.T("需确认重启"), L.T("快照中已关联 AVD 启动器，可检查原生窗口重启条件；仍需核实设备与原启动参数，并单独确认。重启会中断当前模拟器会话。"));
        if (process.Name.Equals("VBoxHeadless.exe", StringComparison.OrdinalIgnoreCase) ||
            process.Name.Equals("vmware-vmx.exe", StringComparison.OrdinalIgnoreCase))
            return Hint(WindowRecoveryKind.VirtualMachine, L.T("可连接画面"), L.T("可检查对应的运行中虚拟机并打开其图形连接入口；不会因此发送开机、关机或重启命令。"));
        if (HasAncestor(process, snapshot, ancestor => SpecialWindowPolicy.SamePath(ancestor.Path, process.Path))) return null;
        if (process.Name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
            process.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase))
            return Hint(WindowRecoveryKind.BrowserInspection, L.T("可检查画面"), L.T("当前快照未发现可见窗口，可进一步核实浏览器启动参数与画面入口；这不表示已确认无头模式，也不保证可以恢复画面。"));
        if (SpecialWindowPolicy.IsSupportedName(process.Name))
            return Hint(WindowRecoveryKind.TrayApplication, L.T("可尝试唤起"), L.T("已识别该程序的界面入口候选，可在核实后尝试唤回窗口；某些版本可能打开登录页或新实例。"));
        return null;
    }

    private static WindowRecoveryHint Hint(WindowRecoveryKind kind, string text, string explanation) =>
        new(kind, text, explanation + "\n" + Verification);

    private static bool HasIdentity(ProcessRecord process, ProcessSnapshot snapshot) => process.Id > 4 &&
        process.StartTimeUtcTicks > 0 && process.StartTimeUtcTicks <= snapshot.CapturedAt.UtcTicks &&
        !string.IsNullOrWhiteSpace(process.OwnerSid) && ProtectionPolicy.TryNormalizePath(process.Path, out var path) &&
        Path.GetFileName(path).Equals(process.Name, StringComparison.OrdinalIgnoreCase);

    private static bool IsQemu(ProcessRecord process) =>
        process.Name.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase) &&
        process.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsAvdEntry(ProcessRecord process) => IsQemu(process) ||
        process.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase);

    private static bool HasAncestor(ProcessRecord process, ProcessSnapshot snapshot, Func<ProcessRecord, bool> predicate)
    {
        var visited = new HashSet<int> { process.Id };
        var child = process;
        for (var depth = 0; depth < 128 && child.ParentId > 4 && visited.Add(child.ParentId); depth++)
        {
            var parents = snapshot.Processes.Where(candidate => candidate.Id == child.ParentId).Take(2).ToArray();
            if (parents.Length != 1 || !ProtectionPolicy.IsVerifiedParent(parents[0], child)) return false;
            if (predicate(parents[0])) return true;
            child = parents[0];
        }
        return false;
    }
}
