namespace ProcessKeeper.Launcher;

internal static class StartupElevationFlow
{
    internal static int Run(bool isAdministrator, Func<BrokerReply> requestElevation, Func<bool, int> launchLocal)
    {
        if (isAdministrator) return launchLocal(true);
        // A successful request creates a fresh elevated launcher. Only a denied
        // or unavailable request needs the ordinary permission-page process.
        if (requestElevation() == BrokerReply.Started) return 0;
        return launchLocal(false);
    }
}
