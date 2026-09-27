namespace ProcessKeeper.Core;

/// <summary>
/// UI confirmation preference for this process lifetime only. The UI must obtain its
/// separate timed consent before enabling. This state grants no process authorization
/// and is deliberately not read by any protection policy or execution service.
/// </summary>
public static class RiskConfirmationMode
{
    public static bool IsEnabled { get; private set; }
    public static event EventHandler? Changed;

    public static void Enable() => SetEnabled(true);
    public static void Disable() => SetEnabled(false);

    private static void SetEnabled(bool value)
    {
        if (IsEnabled == value) return;
        IsEnabled = value;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
