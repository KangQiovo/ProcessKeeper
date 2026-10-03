using ProcessKeeper.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Security.Principal;

namespace ProcessKeeper.App;

public partial class App : Application
{
    private Window? _window;
    private SingleInstanceCoordinator? _instances;
    private bool _starting, _superseded;
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
            _instances = new SingleInstanceCoordinator(() => dispatcher.TryEnqueue(async () =>
            {
                _superseded = true;
                _avatarLifetime.Cancel();
                if (_window is MainWindow current)
                {
                    // An already-running language/close transition may own the input guard.
                    // Still wait for its writer before ending this superseded process.
                    if (!await current.PrepareWindowTransitionAsync())
                        try { await current.FlushPerformanceAsync(); } catch { current.TryDiscardFailedPerformanceSave(); }
                }
                _window?.Close();
                _instances?.Dispose();
                Exit();
            }));
            var result = await _instances.AcquireAsync();
            if (result == InstanceAcquireResult.Superseded || _superseded) { Exit(); return; }
            if (result == InstanceAcquireResult.Blocked) { ShowInstanceBlocked(); return; }
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
            ShowInstanceBlocked();
        }
        finally { _starting = false; }
    }

    private void OpenInitialWindow()
    {
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
                if (!IsAdministrator()) return;
                OpenMainWindow(startup.LanguagePreference);
                startup.Close();
            };
            TrackWindow(startup);
            startup.Activate();
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OpenMainWindow(string? initialLanguagePreference = null)
    {
        if (!IsAdministrator()) return;
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
        main.Activate();
    }

    private void TrackWindow(Window window)
    {
        _window = window;
        window.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_window, window)) return;
            _avatarLifetime.Cancel();
            _instances?.Dispose();
        };
    }

    private void OpenIntroductionReview(MainWindow main)
    {
        if (!ReferenceEquals(_window, main) || _superseded || !IsAdministrator()) return;
        var position = main.AppWindow.Position;
        var size = main.AppWindow.Size;
        var theme = (main.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;
        var startup = new StartupWindow(true, new OnboardingStore(), false,
            ElevationBrokerClient.RequestElevationAsync, review: true, theme: theme);
        startup.IntroductionCompleted += () =>
        {
            if (!ReferenceEquals(_window, startup) || _superseded || !IsAdministrator()) return;
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
        startup.Activate();
        main.Close();
    }

    private void ShowInstanceBlocked()
    {
        var previous = _window;
        var window = new Window { Title = L.T("Process Keeper") };
        var panel = new StackPanel { Spacing = 20, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        panel.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 48, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(new TextBlock { Text = L.T("正在切换Process Keeper实例"), FontSize = 32, TextWrapping = TextWrapping.Wrap });
        var message = new TextBlock { Text = L.T("旧窗口尚未退出，或无法读取实例状态。为避免重复操作，当前还没有进入管理页面。请关闭旧窗口后重试。"),
            TextWrapping = TextWrapping.Wrap, FontSize = 16 };
        panel.Children.Add(message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var retry = new Button { Content = L.T("重试") };
        retry.Click += async (_, _) => { retry.IsEnabled = false; await StartLatestInstanceAsync(); retry.IsEnabled = true; };
        actions.Children.Add(retry);
        if (!IsAdministrator() && ElevationBrokerClient.IsAvailable)
        {
            var elevate = new Button { Content = L.T("以管理员身份继续") };
            elevate.Click += async (_, _) =>
            {
                elevate.IsEnabled = false;
                try
                {
                    var result = await ElevationBrokerClient.RequestElevationAsync(CancellationToken.None);
                    if (result == ElevationRequestResult.Started) window.Close();
                    else message.Text = L.T("未完成管理员授权。可以重试，或先手动关闭旧版本的Process Keeper。");
                }
                catch { message.Text = L.T("未能完成管理员授权，请重新打开原始单文件「ProcessKeeper.exe」。"); }
                finally { elevate.IsEnabled = true; }
            };
            actions.Children.Add(elevate);
        }
        var close = new Button { Content = L.T("退出") };
        close.Click += (_, _) => window.Close();
        actions.Children.Add(close);
        panel.Children.Add(actions);
        window.Content = new Grid { Children = { panel }, Background = (Microsoft.UI.Xaml.Media.Brush)Resources["ApplicationPageBackgroundThemeBrush"] };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
        TrackWindow(window);
        window.Activate();
        previous?.Close();
    }
}
