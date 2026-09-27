namespace ProcessKeeper.Core;

public static class AutorunPresentation
{
    public static bool Matches(AutorunEntry entry, bool complex, int sourceIndex)
    {
        var kind = entry.SourceKind;
        if (!complex)
        {
            if (entry.IsSystem || entry.Ownership == AutorunOwnership.Windows) return false;
            var common = kind is AutorunSourceKind.RegistryRun or AutorunSourceKind.RegistryRunOnce or
                AutorunSourceKind.StartupFolder or AutorunSourceKind.PackagedStartup;
            return sourceIndex switch { 1 => common, 2 => !common, _ => true };
        }
        return sourceIndex switch
        {
            1 => kind is AutorunSourceKind.RegistryRun or AutorunSourceKind.RegistryRunOnce or AutorunSourceKind.PolicyRun,
            2 => kind == AutorunSourceKind.StartupFolder, 3 => kind == AutorunSourceKind.ScheduledTask,
            4 => kind == AutorunSourceKind.Service, 5 => kind == AutorunSourceKind.Driver,
            6 => kind == AutorunSourceKind.AdvancedRegistry, 7 => kind == AutorunSourceKind.WmiSubscription,
            8 => kind == AutorunSourceKind.PackagedStartup, _ => true
        };
    }
}
