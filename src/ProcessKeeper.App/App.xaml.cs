using ProcessKeeper.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Security.Principal;

namespace ProcessKeeper.App;

public partial class App : Application
{
    private Window? _window;
    private SingleInstanceCoordinator? _instances;
    private bool _starting, _shuttingDown;
    private volatile bool _superseded;
    private readonly CancellationTokenSource _startupLifetime = new();
    private readonly CancellationTokenSource _avatarLifetime = new();
    private bool _avatarStarted;
    internal static Task<byte[]?> SharedAuthorAvatarTask { get; private set; } = Task.FromResult<byte[]?>(null);
    public App()
    {
        try { L.Language = LanguageResolver.ResolveCurrent(new ViewPreferencesStore().Load().Language); }
        catch { L.Language = LanguageResolver.ResolveCurrent("auto"); }
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "errors.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n");
            }
            catch { }
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args) => await StartLatestInstanceAsync();

    private async Task StartLatestInstanceAsync()
    {
        if (_starting || _superseded) return;
        _starting = true;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            _instances?.Dispose();
            _instances = await Task.Run(() => new SingleInstanceCoordinator(() =>
            {
                _superseded = true;
                _startupLifetime.Cancel();
                dispatcher.TryEnqueue(async () => await ShutdownForReplacementAsync());
            }), _startupLifetime.Token);
            var coordinator = _instances;
            var result = await Task.Run(() => coordinator.AcquireAsync(_startupLifetime.Token));
            if (result != InstanceAcquireResult.Acquired || !CanCreateWindow)
            {
                await Task.Run(coordinator.NotifyWinner);
                coordinator.Dispose();
                if (result == InstanceAcquireResult.Blocked) Environment.ExitCode = 2;
                Exit(); return;
            }
            if (!_avatarStarted)
            {
                _avatarStarted = true;
                SharedAuthorAvatarTask = AuthorAvatarSource.CreateAsync(_avatarLifetime.Token);
            }
            var waiting = _window;
            OpenInitialWindow();
            if (waiting is not null && !ReferenceEquals(waiting, _window)) waiting.Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _instances?.Dispose();
            Environment.ExitCode = 2; Exit();
        }
        catch (OperationCanceledException) { Environment.ExitCode = 3; _instances?.Dispose(); Exit(); }
        finally { _starting = false; }
    }

    private void OpenInitialWindow()
    {
        if (!CanCreateWindow) return;
        var administrator = IsAdministrator();
        var onboarding = new OnboardingStore();
        string? warning = null;
        if (administrator && onboarding.IsCompleted(out warning))
            OpenMainWindow();
        else
        {
            var startup = new StartupWindow(administrator, onboarding, ElevationBrokerClient.IsAvailable,
                ElevationBrokerClient.RequestElevationAsync, warning);
            startup.IntroductionCompleted += () =>
            {
                // Access is checked again before any MainWindow field or collector is constructed.
                if (!ReferenceEquals(_window, startup) || !CanCreateWindow || !IsAdministrator()) return;
                OpenMainWindow(startup.LanguagePreference);
                startup.Close();
            };
            TrackWindow(startup);
            if (CanCreateWindow) startup.Activate();
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OpenMainWindow(string? initialLanguagePreference = null)
    {
        if (!CanCreateWindow || !IsAdministrator()) return;
        var main = new MainWindow(initialLanguagePreference, SharedAuthorAvatarTask);
        main.IntroductionRequested += async () =>
        {
            if (await main.PrepareWindowTransitionAsync()) OpenIntroductionReview(main);
        };
        main.LanguageChanged += async () =>
        {
            if (!ReferenceEquals(_window, main) || _superseded) return;
            if (!await main.PrepareWindowTransitionAsync() || !ReferenceEquals(_window, main) || _superseded) return;
            var position = main.AppWindow.Position;
            var size = main.AppWindow.Size;
            OpenMainWindow();
            if (_window is MainWindow replacement)
            {
                replacement.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X, position.Y, size.Width, size.Height));
                replacement.OpenLanguageSettings();
                main.Close();
            }
        };
        TrackWindow(main);
        if (CanCreateWindow) main.Activate();
    }

    private void TrackWindow(Window window)
    {
        _window = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _avatarLifetime.Cancel();
            _startupLifetime.Cancel();
            _instances?.Dispose();
            Exit();
        };
    }

    private void OpenIntroductionReview(MainWindow main)
    {
        if (!ReferenceEquals(_window, main) || !CanCreateWindow || !IsAdministrator()) return;
        var position = main.AppWindow.Position;
        var size = main.AppWindow.Size;
        var theme = (main.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        var startup = new StartupWindow(true, new OnboardingStore(), false,
            ElevationBrokerClient.RequestElevationAsync, review: true, theme: theme);
        startup.IntroductionCompleted += () =>
        {
            if (!ReferenceEquals(_window, startup) || !CanCreateWindow || !IsAdministrator()) return;
            OpenMainWindow(startup.LanguagePreference);
            if (_window is MainWindow replacement)
            {
                replacement.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X, position.Y, size.Width, size.Height));
                replacement.OpenAboutSettings();
            }
            startup.Close();
        };
        TrackWindow(startup);
        startup.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(position.X, position.Y, size.Width, size.Height));
        if (CanCreateWindow) startup.Activate();
        main.Close();
    }

    private bool CanCreateWindow => !_superseded && !_shuttingDown && _instances?.IsOwner == true;

    private async Task ShutdownForReplacementAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true; _superseded = true;
        _startupLifetime.Cancel(); _avatarLifetime.Cancel();
        try
        {
            if (_instances is { } coordinator) await Task.Run(coordinator.NotifyWinner);
            if (_window is MainWindow main) await main.CloseForReplacementAsync();
            else _window?.Close();
        }
        finally { _instances?.Dispose(); Exit(); }
    }
}
