using ProcessKeeper.Core;

public static class RiskConfirmationModeVerification
{
    public static void Run(Action<bool, string> check)
    {
        check(!RiskConfirmationMode.IsEnabled, "risk confirmation mode defaults off for this new process");
        var events = 0;
        var eventStates = new List<bool>();
        EventHandler handler = (_, _) => { events++; eventStates.Add(RiskConfirmationMode.IsEnabled); };
        RiskConfirmationMode.Changed += handler;
        try
        {
            RiskConfirmationMode.Disable();
            check(events == 0, "disabling the default-off mode is idempotent");
            RiskConfirmationMode.Enable();
            check(RiskConfirmationMode.IsEnabled && events == 1 && eventStates[0], "enable publishes one notification after the state changes");
            RiskConfirmationMode.Enable();
            check(events == 1, "repeated enable does not publish another change");
            const string sid = "S-1-5-21-100-200-300-1001";
            var policy = new ProtectionPolicy(1, sid, 999);
            var desktop = new ProcessRecord
            {
                Id = 700, Name = "explorer.exe", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                OwnerSid = sid, SessionId = 1, NativeCritical = false, StartTimeUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks
            };
            ProcessSnapshot Snapshot(ProcessRecord process) => new(DateTimeOffset.UtcNow, new[] { process }, []);
            check(policy.Evaluate(desktop, Snapshot(desktop), []).Protected, "risk confirmation mode never removes ordinary bulk desktop protection");
            var critical = desktop with { NativeCritical = true, IsSystem = true };
            check(policy.EvaluateSensitiveClose(critical, Snapshot(critical), []).Protected,
                "risk confirmation mode never authorizes a native critical desktop process");
            var system = desktop with { Name = "lsass.exe", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "lsass.exe") };
            check(policy.Evaluate(system, Snapshot(system), []).Protected && policy.EvaluateSensitiveClose(system, Snapshot(system), []).Protected,
                "risk confirmation mode never broadens core system process authorization");
            var rule = new WhitelistRule { Kind = RuleKind.ProcessName, Value = desktop.Name };
            check(policy.EvaluateSensitiveClose(desktop, Snapshot(desktop), new[] { rule }).Protected,
                "risk confirmation mode never bypasses an enabled whitelist rule");
            RiskConfirmationMode.Disable();
            check(!RiskConfirmationMode.IsEnabled && events == 2 && !eventStates[1], "disable restores confirmations and publishes one change");
            RiskConfirmationMode.Disable();
            check(events == 2, "repeated disable does not publish another change");
        }
        finally
        {
            RiskConfirmationMode.Changed -= handler;
            RiskConfirmationMode.Disable();
        }
        check(!RiskConfirmationMode.IsEnabled, "risk confirmation test leaves the real session state disabled");
    }
}
