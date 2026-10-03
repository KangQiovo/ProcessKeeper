using System.Windows;
namespace ProcessKeeper.App;
public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private readonly CancellationTokenSource _avatarLifetime = new();
    internal static Task<byte[]?> SharedAuthorAvatarTask { get; private set; } = Task.FromResult<byte[]?>(null);
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Keep certificate validation and let the operating system negotiate its supported TLS versions.
        // https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
        AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
        AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
        base.OnStartup(e);
        _instance = new SingleInstanceCoordinator(() => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (MainWindow is MainWindow current) await current.CloseForReplacementAsync();
            else if (MainWindow is not null) MainWindow.Close();
            else Shutdown();
        })));
        if (await _instance.AcquireAsync() != InstanceAcquireResult.Acquired) { _instance.Dispose(); Shutdown(); return; }
        SharedAuthorAvatarTask = AuthorAvatarSource.CreateAsync(_avatarLifetime.Token);
        var window = new MainWindow(new LegacyBackend(), null, SharedAuthorAvatarTask);
        MainWindow = window;
        window.Closed += (_, _) => { _instance.Dispose(); Shutdown(); };
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _avatarLifetime.Cancel();
        _avatarLifetime.Dispose();
        base.OnExit(e);
    }
}
