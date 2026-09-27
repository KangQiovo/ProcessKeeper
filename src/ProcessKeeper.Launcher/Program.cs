using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ProcessKeeper.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            // Only the original native launcher can request elevation. The ordinary
            // user cache is never a source of elevated code or launch arguments.
            using var original = elevated ? null : OriginalLauncher.TryOpenCurrent();
            return StartupElevationFlow.Run(elevated,
                () => original?.Elevate() ?? BrokerReply.Unavailable,
                administrator => LaunchApplication(administrator, original));
        }
        catch (Exception exception)
        {
            var language = LauncherBootstrapText.CurrentLanguage();
            MessageBox(IntPtr.Zero,
                LauncherBootstrapText.Text(LauncherBootstrapText.FailureIntro, language) + "\n\n" +
                LauncherBootstrapText.Text(exception.Message, language) + "\n\n" +
                LauncherBootstrapText.Text(LauncherBootstrapText.FailureHelp, language),
                LauncherBootstrapText.Text(LauncherBootstrapText.FailureTitle, language), 0x10);
            return 1;
        }
    }

    private static int LaunchApplication(bool elevated, OriginalLauncher? original)
    {
        using var broker = elevated || original is null ? null : new ElevationBroker(original);
        var assembly = Assembly.GetExecutingAssembly();
        using var manifestResource = assembly.GetManifestResourceStream("ProcessKeeper.Payload.json")
            ?? throw new InvalidDataException("The embedded file manifest is missing. Download a complete ProcessKeeper.exe again.");
        using var payload = assembly.GetManifestResourceStream("ProcessKeeper.Payload.zip")
            ?? throw new InvalidDataException("The embedded application is missing. Download a complete ProcessKeeper.exe again.");
        var contract = PayloadContract.Read(manifestResource);
        contract.VerifyArchiveHash(payload);

        var versionRoot = elevated
            ? CacheSecurity.GetVersionRoot(contract.Hash)
            : UserCacheSecurity.GetVersionRoot(contract.Hash);

        using var cacheLock = CacheStore.AcquireLock(versionRoot, TimeSpan.FromSeconds(90), elevated);
        var appDirectory = CacheStore.Prepare(versionRoot, payload, contract, elevated);
        // Hold read-only handles until CreateProcess returns, preventing ordinary
        // writes or replacements between the integrity check and child creation.
        using var verifiedFiles = CacheStore.OpenVerifiedFiles(appDirectory, contract, elevated);
        var executable = contract.ResolvePath(appDirectory, contract.EntryPoint);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = appDirectory
        };
        broker?.AddChildArguments(start);
        using var child = Process.Start(start)
            ?? throw new IOException("Windows could not start Process Keeper.");
        cacheLock.Dispose();
        // Keep the source EXE, payload files and IPC server alive while the
        // non-elevated access page is open, including after UAC cancellation.
        broker?.RunAsync(child).GetAwaiter().GetResult();
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
