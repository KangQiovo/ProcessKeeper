using System.Windows;
namespace ProcessKeeper.App;
public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    private volatile bool _superseded;
    private bool _shuttingDown;
    private readonly CancellationTokenSource _startupLifetime = new();
    private readonly CancellationTokenSource _avatarLifetime = new();
    internal static Task<byte[]?> SharedAuthorAvatarTask { get; private set; } = Task.FromResult<byte[]?>(null);
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Keep certificate validation and let the operating system negotiate its supported TLS versions.
        // https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
        AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
        AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
        base.OnStartup(e);
        try
        {
            _instance = await Task.Run(() => new SingleInstanceCoordinator(() =>
            {
                _superseded = true; _startupLifetime.Cancel();
                Dispatcher.BeginInvoke(new Action(async () => await ShutdownForReplacementAsync()));
            }), _startupLifetime.Token);
            var coordinator = _instance;
            var result = await Task.Run(() => coordinator.AcquireAsync(_startupLifetime.Token));
            if (result != InstanceAcquireResult.Acquired || !CanCreateWindow)
            { await Task.Run(coordinator.NotifyWinner); coordinator.Dispose(); Shutdown(result == InstanceAcquireResult.Blocked ? 2 : Environment.ExitCode); return; }
            SharedAuthorAvatarTask = AuthorAvatarSource.CreateAsync(_avatarLifetime.Token);
            if (!CanCreateWindow) { await ShutdownForReplacementAsync(); return; }
            var window = new MainWindow(new LegacyBackend(), null, SharedAuthorAvatarTask);
            MainWindow = window;
            window.Closed += (_, _) => { _instance?.Dispose(); Shutdown(Environment.ExitCode); };
            if (!CanCreateWindow) { await ShutdownForReplacementAsync(); return; }
            window.Show();
        }
        catch (OperationCanceledException) { _instance?.Dispose(); Shutdown(3); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { _instance?.Dispose(); Shutdown(2); }
    }
    private bool CanCreateWindow => !_superseded && !_shuttingDown && _instance?.IsOwner == true;
    private async Task ShutdownForReplacementAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true; _superseded = true; _startupLifetime.Cancel(); _avatarLifetime.Cancel();
        try
        {
            if (_instance is { } coordinator) await Task.Run(coordinator.NotifyWinner);
            if (MainWindow is MainWindow current) await current.CloseForReplacementAsync();
            else MainWindow?.Close();
        }
        finally { _instance?.Dispose(); Shutdown(Environment.ExitCode); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _avatarLifetime.Cancel();
        _startupLifetime.Cancel(); _instance?.Dispose();
        _avatarLifetime.Dispose();
        base.OnExit(e);
    }
}
