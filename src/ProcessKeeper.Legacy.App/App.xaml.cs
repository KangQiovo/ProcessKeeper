using System.Windows;
namespace ProcessKeeper.App;
public partial class App : Application
{
    private SingleInstanceCoordinator? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Keep certificate validation and let the operating system negotiate its supported TLS versions.
        // https://learn.microsoft.com/en-us/dotnet/framework/network-programming/tls
        AppContext.SetSwitch("Switch.System.Net.DontEnableSchUseStrongCrypto", false);
        AppContext.SetSwitch("Switch.System.Net.DontEnableSystemDefaultTlsVersions", false);
        base.OnStartup(e);
        _instance = new SingleInstanceCoordinator(() => Dispatcher.BeginInvoke(new Action(() => { if (MainWindow is not null) MainWindow.Close(); else Shutdown(); })));
        if (await _instance.AcquireAsync() != InstanceAcquireResult.Acquired) { _instance.Dispose(); Shutdown(); return; }
        var window = new MainWindow();
        MainWindow = window;
        window.Closed += (_, _) => { _instance.Dispose(); Shutdown(); };
        window.Show();
    }
}
