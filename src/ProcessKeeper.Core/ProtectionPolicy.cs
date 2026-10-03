namespace ProcessKeeper.Core;

/// <summary>Fail-closed policy: incomplete identities cannot become close candidates.</summary>
public sealed class ProtectionPolicy(int sessionId, string userSid, int selfId)
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle Process", "Registry", "Secure System", "Memory Compression",
        "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "explorer.exe", "sihost.exe", "taskhostw.exe",
        "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchIndexer.exe",
        "TextInputHost.exe", "ctfmon.exe", "RuntimeBroker.exe", "conhost.exe", "OpenConsole.exe", "audiodg.exe",
        "SecurityHealthService.exe", "SecurityHealthSystray.exe", "MsMpEng.exe", "NisSrv.exe"
    };

    /// <summary>Checks explicit rules only; service actions use this in addition to their independent safety checks.</summary>
    public static bool IsDirectlyWhitelisted(ProcessRecord process, IReadOnlyList<WhitelistRule> rules) =>
        rules.Any(rule => rule.Enabled && Matches(rule, process));

    public ProtectionDecision Evaluate(ProcessRecord process, ProcessSnapshot snapshot, IReadOnlyList<WhitelistRule> rules)
        => EvaluateCore(process, snapshot, rules, allowSensitiveDesktop: false);

    /// <summary>
    /// Only for one frozen identity after the UI's separate, timed second confirmation.
    /// A fresh snapshot and the execution layer's same-handle native validation remain mandatory.
    /// This is never the recheck delegate for an ordinary/bulk close operation.
    /// </summary>
    public ProtectionDecision EvaluateSensitiveClose(ProcessRecord process, ProcessSnapshot snapshot, IReadOnlyList<WhitelistRule> rules)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        if (!SensitiveProcessClose.IsSensitiveComponent(process)) return Keep(L.T("此进程不属于可单独关闭的桌面组件。"));
        if (process.NativeCritical != false) return Keep(L.T("关键进程标志未明确为非关键，不能关闭。"));
        if (process.Services.Count > 0) return Keep(L.T("关联 Windows 服务的进程不能通过此入口关闭。"));
        // No MyDock/system-account exception applies to this deliberately narrow desktop action.
        if (string.IsNullOrWhiteSpace(userSid) || !string.Equals(process.OwnerSid, userSid, StringComparison.OrdinalIgnoreCase))
            return Keep(L.T("属于其他用户或系统账户"));
        var observed = snapshot.Processes.Where(item => item.Id == process.Id).Take(2).ToArray();
        if (observed.Length != 1 || !SensitiveProcessClose.SameIdentity(process, observed[0]))
            return Keep(L.T("进程身份已变化或无法唯一核实，请刷新后重试。"));
        var current = observed[0];
        if (current.NativeCritical != false || current.Services.Count > 0 || current.IsSelf || !SensitiveProcessClose.IsSensitiveComponent(current))
            return Keep(L.T("进程状态已变化，不能通过敏感关闭入口结束。"));
        // Recheck rules on both the originally confirmed identity and current attribution.
        var original = EvaluateCore(process, snapshot, rules, allowSensitiveDesktop: true);
        return original.Protected ? original : EvaluateCore(current, snapshot, rules, allowSensitiveDesktop: true);
    }

    private ProtectionDecision EvaluateCore(ProcessRecord process, ProcessSnapshot snapshot, IReadOnlyList<WhitelistRule> rules,
        bool allowSensitiveDesktop)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);

        if (process.Id == selfId || process.IsSelf) return Keep(L.T("当前管理程序自身"));
        if (process.CriticalStatusUnknown) return Keep(L.T("无法核实关键进程标志，已保守保护此进程。"));
        if (process.Id <= 4 || !allowSensitiveDesktop && (process.IsSystem || CriticalNames.Contains(process.Name)))
            return Keep(string.IsNullOrWhiteSpace(process.SystemReason) ? L.T("系统或必要桌面组件") : process.SystemReason);
        if (process.SessionId == 0) return Keep(L.T("系统服务会话（Session 0）"));
        if (process.SessionId != sessionId) return Keep(L.T("其他登录会话"));
        if (process.StartTimeUtcTicks <= 0 || process.StartTimeUtcTicks > DateTime.UtcNow.Ticks)
            return Keep(L.T("无法确认进程创建时间"));
        if (!TryNormalizePath(process.Path, out _)) return Keep(L.T("无法确认进程完整路径"));
        if (string.IsNullOrWhiteSpace(process.OwnerSid) || string.IsNullOrWhiteSpace(userSid))
            return Keep(L.T("无法确认进程所属用户"));
        if (!string.Equals(process.OwnerSid, userSid, StringComparison.OrdinalIgnoreCase) && !IsVerifiedDockComponent(process))
            return Keep(L.T("属于其他用户或系统账户"));

        foreach (var rule in rules.Where(r => r.Enabled))
            if (Matches(rule, process)) return new(true, L.T("白名单：") + WhitelistStore.GetDisplayName(rule), rule.Id);

        // No automatic application grouping follows parent PIDs. Only an explicitly enabled
        // descendant rule inherits protection, after validating every edge's creation time.
        foreach (var rule in rules.Where(r => r.Enabled && r.IncludeDescendants))
        {
            if (IsDefaultCodex(rule) && IsExplicitCloseFamily(process)) continue;
            var seen = new HashSet<int> { process.Id };
            var child = process;
            for (int depth = 0; depth < 128; depth++)
            {
                if (child.ParentId <= 4 || !seen.Add(child.ParentId)) break;
                var parents = snapshot.Processes.Where(p => p.Id == child.ParentId).Take(2).ToArray();
                if (parents.Length != 1) break;
                var parent = parents[0];
                if (!IsVerifiedParent(parent, child)) break;
                if (IsDefaultCodex(rule) && IsExplicitCloseFamily(parent)) break;
                if (Matches(rule, parent)) return new(true, L.F($"白名单子进程：{WhitelistStore.GetDisplayName(rule)}（父级 PID {parent.Id}）"), rule.Id);
                child = parent;
            }
        }
        return new(false, L.T("当前用户的非白名单应用进程"));
    }

    private static ProtectionDecision Keep(string reason) => new(true, reason);
    internal static bool IsDefaultCodex(WhitelistRule rule) => rule.Id == "default-codex" &&
        rule.Kind == RuleKind.Application && string.Equals(rule.Value, "known:codex", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVerifiedParent(ProcessRecord parent, ProcessRecord child) =>
        parent.StartTimeUtcTicks > 0 && child.StartTimeUtcTicks > 0 && parent.StartTimeUtcTicks <= child.StartTimeUtcTicks &&
        !string.IsNullOrWhiteSpace(parent.OwnerSid) && !string.IsNullOrWhiteSpace(child.OwnerSid) &&
        string.Equals(parent.OwnerSid, child.OwnerSid, StringComparison.OrdinalIgnoreCase) &&
        parent.SessionId == child.SessionId && TryNormalizePath(parent.Path, out _) && TryNormalizePath(child.Path, out _);

    internal static bool IsVerifiedDockComponent(ProcessRecord p) =>
        string.Equals(p.OwnerSid, "S-1-5-18", StringComparison.OrdinalIgnoreCase) &&
        (p.Name.Equals("Dock_64.exe", StringComparison.OrdinalIgnoreCase) ||
         p.Name.Equals("Dockmod.exe", StringComparison.OrdinalIgnoreCase) ||
         p.Name.Equals("Dockmod64.exe", StringComparison.OrdinalIgnoreCase)) &&
        TryNormalizePath(p.Path, out var path) &&
        System.IO.Path.GetFileName(path).Equals(p.Name, StringComparison.OrdinalIgnoreCase) &&
        path.Contains("\\MyDockFinder\\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExplicitCloseFamily(ProcessRecord p) =>
        p.ApplicationKey.Equals("known:avd", StringComparison.OrdinalIgnoreCase) ||
        p.ApplicationKey.Equals("known:mydock", StringComparison.OrdinalIgnoreCase) ||
        p.Path.Replace('/', '\\').Contains("\\MyDockFinder\\", StringComparison.OrdinalIgnoreCase) ||
        p.Path.Replace('/', '\\').Contains("\\Android\\Sdk\\emulator\\", StringComparison.OrdinalIgnoreCase) ||
        p.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Name.Equals("netsimd.exe", StringComparison.OrdinalIgnoreCase) ||
        (p.Name.StartsWith("qemu-system-", StringComparison.OrdinalIgnoreCase) && p.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ||
        p.Name.Equals("MyDock.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Name.Equals("MyDockFinder.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Name.Equals("Dock_64.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Name.Equals("Dockmod.exe", StringComparison.OrdinalIgnoreCase) ||
        p.Name.Equals("Dockmod64.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool Matches(WhitelistRule rule, ProcessRecord process)
    {
        if (string.IsNullOrWhiteSpace(rule.Value)) return false;
        return rule.Kind switch
        {
            RuleKind.Application => string.Equals(rule.Value, process.ApplicationKey, StringComparison.OrdinalIgnoreCase),
            RuleKind.ProcessName => string.Equals(rule.Value, process.Name, StringComparison.OrdinalIgnoreCase),
            RuleKind.ExecutablePath => TryNormalizePath(rule.Value, out var rulePath) &&
                TryNormalizePath(process.Path, out var imagePath) && string.Equals(rulePath, imagePath, StringComparison.OrdinalIgnoreCase),
            RuleKind.Directory => TryNormalizePath(rule.Value, out var directory) &&
                TryNormalizePath(process.Path, out var path) && path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static bool TryNormalizePath(string value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['*', '?', '\r', '\n', '\0']) >= 0) return false;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(value)) return false;
            normalized = System.IO.Path.GetFullPath(value).Replace('/', '\\');
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return false; }
    }
}
