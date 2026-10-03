using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal sealed class UpdateUiBackend
{
    internal required Func<UpdatePreferences, string, CancellationToken, Task<UpdateCheckResult>> Check { get; init; }
    internal required Func<UpdatePreferences, UpdateRelease, UpdateAsset, string, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>> Download { get; init; }
    internal required Func<UpdatePreferences, UpdateRelease, UpdateAsset, IProgress<UpdateProgress>, CancellationToken, Task<IReadOnlyList<UpdateProbeResult>>> Probe { get; init; }
    internal required Func<string> CreateStage { get; init; }
    internal required Action<string> DiscardStage { get; init; }
    internal required Func<UpdateDownloadResult, CancellationToken, Task> Install { get; init; }
    internal required Func<CancellationToken, Task<string>> Shortcut { get; init; }
    internal Func<string?> UnavailableReason { get; init; } = () => null;
    internal Func<string, Task<bool>>? ConfirmExternalLink { get; init; }
    internal Func<UpdateRelease, UpdateAsset, string, UpdateDownloadSession>? CreateSession { get; init; }
    internal Func<UpdatePreferences, UpdateDownloadSession, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>>? ResumeDownload { get; init; }
    internal Action<UpdateDownloadSession>? DiscardSession { get; init; }
    internal Action? ExitAfterUpdate { get; init; }
}

/// <summary>Native, bounded update UI. Network and installation boundaries are injected for isolated fixtures.</summary>
internal sealed class UpdatesView : UserControl
{
    private readonly UpdateUiBackend _backend;
    private readonly UpdatePreferencesStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private TaskCompletionSource<bool>? _operationFinished;
    private bool _exitAfterCleanup;
    private bool _closingSuspended;
    private static bool _startupChecked, _startupShortcutAttempted;
    private static readonly HashSet<string> AnnouncedVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _currentVersion = ResolveCurrentVersion();
    private static string ResolveCurrentVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var information = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        return UpdateVersion.TryParse(information, out var parsed) ? parsed!.Value : assembly.GetName().Version?.ToString(3) ?? "1.7.0";
    }
    private readonly TextBlock _repository = new() { Text = UpdatePolicy.Repository, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _node = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _onStartup = new(), _desktop = new();
    private readonly Button _check = new(), _shortcut = new(), _save = new(), _cancel = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
    private readonly TextBlock _buildDate = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DispatcherTimer _buildDateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 100 };
    private readonly List<string> _nodeIds = ["auto"];
    private readonly Queue<string> _sourceMessages = new();
    private bool _closed, _loading, _readable = true;
    private UpdateDownloadWindow? _downloadWindow;
    internal bool IsBusy => _operation is not null;
    internal UpdatePreferences Preferences { get; private set; } = new();
    internal Func<bool> CanPresent { get; set; } = () => true;
    internal Func<ContentDialog, Task<ContentDialogResult>> Present { get; set; } = async dialog => await dialog.ShowAsync();
    internal Action<string> Log { get; set; } = _ => { };
    internal bool ActivateProgressWindow { get; set; } = true;
    internal event Action<bool>? BusyChanged;

    internal UpdatesView(UpdateUiBackend backend, string? settingsDirectory = null)
    {
        _backend = backend; _store = new UpdatePreferencesStore(settingsDirectory);
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = L.T("检查更新"), FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(Text(L.F($"当前版本：{_currentVersion}")));
        void RefreshBuildDate() => _buildDate.Text = L.F($"构建时间：{BuildInfo.DisplayBuildDate}");
        RefreshBuildDate(); panel.Children.Add(_buildDate);
        _buildDateTimer.Tick += (_, _) => RefreshBuildDate();
        Loaded += (_, _) => { if (!_closed) { RefreshBuildDate(); _buildDateTimer.Start(); } };
        Unloaded += (_, _) => _buildDateTimer.Stop();
        var checks = new Grid { ColumnSpacing = 8 };
        checks.ColumnDefinitions.Add(new ColumnDefinition()); checks.ColumnDefinitions.Add(new ColumnDefinition());
        _check.Content = Text(L.T("立即检查")); _shortcut.Content = Text(L.T("创建桌面快捷方式"));
        _check.HorizontalContentAlignment = _shortcut.HorizontalContentAlignment = HorizontalAlignment.Center;
        _check.HorizontalAlignment = _shortcut.HorizontalAlignment = HorizontalAlignment.Stretch;
        checks.Children.Add(_check); Grid.SetColumn(_shortcut, 1); checks.Children.Add(_shortcut); panel.Children.Add(checks);
        panel.Children.Add(Text(L.T("GitHub 仓库"), 12)); panel.Children.Add(_repository);
        _source.Header = L.T("更新源");
        foreach (var name in new[] { "自动选择最低延迟", "官方 GitHub", "第三方加速" }) _source.Items.Add(L.T(name));
        _node.Header = L.T("第三方节点"); _node.Items.Add(L.T("自动选择最低延迟"));
        foreach (var node in UpdateSources.All.Where(item => !item.IsOfficial)) { _nodeIds.Add(node.Id); _node.Items.Add(node.Name); }
        panel.Children.Add(_source); panel.Children.Add(_node);
        _onStartup.Content = Text(L.T("启动时检查更新")); _desktop.Content = Text(L.T("自动创建桌面快捷方式"));
        panel.Children.Add(_onStartup); panel.Children.Add(_desktop);
        _save.Content = Text(L.T("保存更新设置")); panel.Children.Add(_save);
        panel.Children.Add(Text(L.T("版本信息来自 GitHub 官方；更新源用于下载安装包。"), 12));
        panel.Children.Add(Text(L.T("下载前检测实际文件延迟，自动使用响应最快的可用源。"), 12));
        panel.Children.Add(_progress); panel.Children.Add(_status);
        _cancel.Content = Text(L.T("取消操作")); _cancel.Visibility = Visibility.Collapsed; panel.Children.Add(_cancel);
        Content = panel;
        _source.SelectionChanged += (_, _) => { if (!_loading) _node.IsEnabled = _source.SelectedIndex == (int)UpdateSourceMode.ThirdParty && !IsBusy; };
        _check.Click += async (_, _) => await CheckAsync(false);
        _shortcut.Click += async (_, _) => await CreateShortcutAsync(false);
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) => _operation?.Cancel();
        ActualThemeChanged += (_, _) => _downloadWindow?.ApplyTheme(ActualTheme);
        try { Apply(_store.Load()); }
        catch (Exception exception) { Apply(new UpdatePreferences()); _readable = false; SetStatus(L.T("更新设置无法读取") + " | " + exception.Message); }
    }

    internal void Apply(UpdatePreferences preferences)
    {
        Preferences = UpdatePreferencesStore.Validate(preferences);
        _readable = true;
        _loading = true;
        try
        {
            _repository.Text = UpdatePolicy.Repository; _source.SelectedIndex = (int)Preferences.SourceMode;
            _node.SelectedIndex = _nodeIds.IndexOf(Preferences.ThirdPartySourceId);
            _onStartup.IsChecked = Preferences.CheckOnStartup; _desktop.IsChecked = Preferences.AutoDesktopShortcut;
            _node.IsEnabled = Preferences.SourceMode == UpdateSourceMode.ThirdParty && !IsBusy;
        }
        finally { _loading = false; }
    }

    private void Save()
    {
        if (_closed || IsBusy) return;
        try
        {
            var next = new UpdatePreferences(UpdatePolicy.Repository, _onStartup.IsChecked == true, (UpdateSourceMode)_source.SelectedIndex,
                _nodeIds[Math.Max(0, _node.SelectedIndex)], _desktop.IsChecked == true);
            next = UpdatePreferencesStore.Validate(next); _store.Save(next); Apply(next); _readable = true;
            SetStatus(L.T("更新设置已保存")); Log(L.T("更新设置已保存"));
        }
        catch (Exception exception) { SetStatus(exception.Message); }
    }

    internal async Task StartOnLaunchAsync()
    {
        if (_closed || _closingSuspended || !_readable) return;
        try
        {
            // Let initial list loading render; no collector work is blocked by networking.
            await Task.Delay(900, _lifetime.Token);
            if (_closed || _closingSuspended) return;
            if (!_startupShortcutAttempted && Preferences.AutoDesktopShortcut)
            {
                _startupShortcutAttempted = true;
                await CreateShortcutAsync(true);
            }
            if (!_startupChecked && Preferences.CheckOnStartup && !_closed && !_closingSuspended)
            {
                _startupChecked = true;
                await CheckAsync(true);
            }
        }
        catch (OperationCanceledException) { }
    }

    private CancellationToken BeginOperation()
    {
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _check.IsEnabled = _shortcut.IsEnabled = _save.IsEnabled = _source.IsEnabled = _node.IsEnabled = _onStartup.IsEnabled = _desktop.IsEnabled = false;
        _cancel.Visibility = Visibility.Visible; _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        BusyChanged?.Invoke(true);
        return _operation.Token;
    }

    private void EndOperation()
    {
        _operationFinished?.TrySetResult(true); _operationFinished = null;
        _operation?.Dispose(); _operation = null;
        if (_closed) return;
        _check.IsEnabled = _shortcut.IsEnabled = _save.IsEnabled = _source.IsEnabled = _onStartup.IsEnabled = _desktop.IsEnabled = true;
        _node.IsEnabled = Preferences.SourceMode == UpdateSourceMode.ThirdParty;
        _cancel.Visibility = _progress.Visibility = Visibility.Collapsed;
        _cancel.IsEnabled = true; BusyChanged?.Invoke(false);
    }

    private async Task CheckAsync(bool automatic)
    {
        if (_closingSuspended) return;
        if (!automatic && _downloadWindow is not null) { _downloadWindow.Reveal(); return; }
        if (_closed || IsBusy || !_readable) return;
        if (!automatic && !CanPresent()) { SetStatus(L.T("请等待当前操作完成")); return; }
        var token = BeginOperation();
        try
        {
            SetStatus(L.T("正在检查更新…"));
            var settings = Preferences;
            var result = await _backend.Check(settings, _currentVersion, token);
            token.ThrowIfCancellationRequested(); if (_closed) return;
            SetStatus(result.Message); Log(L.T("检查更新") + " | " + result.Message);
            if (!result.UpdateAvailable || result.LatestRelease is not { } release) return;
            var key = release.Repository + "|" + release.Tag;
            if (automatic && AnnouncedVersions.Contains(key)) return;
            while (!CanPresent()) await Task.Delay(250, token);
            token.ThrowIfCancellationRequested(); if (_closed) return;
            AnnouncedVersions.Add(key);
            var chosen = await ChooseReleaseAssetAsync(release, token);
            if (chosen is null || _closed || token.IsCancellationRequested) return;
            await DownloadAndInstallAsync(settings, release, chosen, token);
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus(L.T("更新检查已取消")); }
        catch (Exception exception) { if (!_closed) { SetStatus(L.T("检查更新失败") + " | " + exception.Message); Log(L.T("检查更新失败") + " | " + exception.Message); } }
        finally { EndOperation(); if (_exitAfterCleanup) { _exitAfterCleanup = false; _backend.ExitAfterUpdate?.Invoke(); } }
    }

    private async Task<UpdateAsset?> ChooseReleaseAssetAsync(UpdateRelease release, CancellationToken token)
    {
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
        panel.Children.Add(Text(release.Name + " | " + release.Tag));
        if (release.PublishedAt is { } published) panel.Children.Add(Text(TimeDisplay.Format(published.ToLocalTime()), 12));
        panel.Children.Add(Text(L.T("更新日志")));
        var noteDocument = await Task.Run(() => ReleaseNotesParser.Parse(string.IsNullOrWhiteSpace(release.Body) ? L.T("未提供更新日志。") : release.Body), token);
        panel.Children.Add(new ScrollViewer { Content = ReleaseNotesView.Create(noteDocument, async url => await OpenReleaseLinkAsync(url)),
            MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var assets = new ComboBox { Header = L.T("发布文件"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var asset in release.Assets)
            assets.Items.Add(new ComboBoxItem { Content = Text($"{asset.Name} | {UpdatePackageDescription.Text(asset)} | {asset.Size / 1048576d:F2} MiB", 12), Tag = asset });
        assets.SelectedIndex = release.Assets.ToList().FindIndex(item => item.CanAutoInstall);
        if (assets.SelectedIndex < 0 && assets.Items.Count > 0) assets.SelectedIndex = 0;
        panel.Children.Add(assets);
        var reason = Text("", 12); panel.Children.Add(reason);
        var probe = new Button { Content = Text(L.T("检测节点延迟")) }; panel.Children.Add(probe);
        var probeStatus = Text("", 12); panel.Children.Add(probeStatus);
        // URI is validated by the update service; opening is always an explicit action.
        var website = new Button { Content = Text(L.T("发布主页")) };
        website.Click += async (_, _) => await OpenReleaseLinkAsync(release.HtmlUrl);
        panel.Children.Add(website);
        var dialog = NewDialog(L.F($"发现新版本 | {release.Tag}"), panel, L.T("下载并更新"));
        using var probing = CancellationTokenSource.CreateLinkedTokenSource(token);
        var probeToken = probing.Token;
        var probingNow = false;
        dialog.Closed += (_, _) => probing.Cancel();
        void Selection()
        {
            var asset = (assets.SelectedItem as ComboBoxItem)?.Tag as UpdateAsset;
            var unavailable = _backend.UnavailableReason();
            reason.Text = unavailable ?? (asset is null ? L.T("没有可用发布文件。") : asset.Restriction);
            dialog.IsPrimaryButtonEnabled = !probingNow && unavailable is null && asset?.CanAutoInstall == true;
            probe.IsEnabled = !probingNow && asset?.CanAutoInstall == true;
        }
        probe.Click += async (_, _) =>
        {
            if (probingNow || (assets.SelectedItem as ComboBoxItem)?.Tag is not UpdateAsset asset) return;
            probingNow = true; assets.IsEnabled = false; Selection();
            try
            {
                probeStatus.Text = L.T("正在检测更新源…");
                var progress = new Progress<UpdateProgress>(value => { if (probingNow && !probeToken.IsCancellationRequested) probeStatus.Text = value.Message; });
                var results = await _backend.Probe(Preferences, release, asset, progress, probeToken);
                if (!probeToken.IsCancellationRequested) probeStatus.Text = string.Join("\n", results.Select(item =>
                    (UpdateSources.All.FirstOrDefault(source => source.Id == item.SourceId)?.Name ?? item.SourceId) + " | " +
                    (item.LatencyMilliseconds is { } latency ? latency + " ms | " : "") + item.Message));
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (!probeToken.IsCancellationRequested) probeStatus.Text = exception.Message; }
            finally { if (!probeToken.IsCancellationRequested) { probingNow = false; assets.IsEnabled = true; Selection(); } }
        };
        assets.SelectionChanged += (_, _) => Selection(); Selection();
        if (await ShowCancellableDialog(dialog, token) != ContentDialogResult.Primary || token.IsCancellationRequested) return null;
        return (assets.SelectedItem as ComboBoxItem)?.Tag as UpdateAsset is { CanAutoInstall: true } selected ? selected : null;
    }

    private async Task DownloadAndInstallAsync(UpdatePreferences settings, UpdateRelease release, UpdateAsset asset, CancellationToken token)
    {
        var transfer = new UpdateTransferController(settings, _backend.CreateStage, _backend.DiscardStage,
            _backend.Download, _backend.CreateSession, _backend.ResumeDownload, _backend.DiscardSession);
        TaskCompletionSource<bool>? installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _downloadWindow = new UpdateDownloadWindow(transfer, release, asset, ActualTheme,
                () => _operation?.Cancel(),
                () => installChoice?.TrySetResult(true), ActivateProgressWindow);
            transfer.Changed += value =>
            {
                if (_closed || token.IsCancellationRequested) return;
                _downloadWindow?.Refresh(value); SetStatus(value.Message + " | " + value.Detail);
                _progress.IsIndeterminate = value.Total <= 0; _progress.Value = value.Percent;
            };
            _check.IsEnabled = true; _check.Content = Text(L.T("查看下载进度"));
            _downloadWindow.Reveal();
            var download = await transfer.DownloadAsync(release, asset, token);
            token.ThrowIfCancellationRequested(); if (_closed) return;
            while (!_closed)
            {
                installChoice ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                var currentChoice = installChoice;
                using (token.Register(() => currentChoice.TrySetCanceled())) await currentChoice.Task;
                installChoice = null;
                if (!CanPresent())
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("请等待当前操作完成")); continue; }
                transfer.SetPhase(UpdateTransferPhase.Confirming, L.T("请确认更新并重新启动。"));
                var body = new StackPanel { Spacing = 12, MaxWidth = 500 };
                body.Children.Add(Text(L.T("点击“更新并重新启动”后，Process Keeper 将退出并安装已下载的更新，然后重新启动。其他应用不会因此退出；请先完成本应用中的其他操作。")));
                body.Children.Add(Text(download.Asset.Name + " | " + download.Release.Tag));
                body.Children.Add(Text("SHA-256 | " + download.Sha256, 12));
                if (await ShowCancellableDialog(NewDialog(L.T("更新并重新启动"), body, L.T("更新并重新启动")), token) != ContentDialogResult.Primary)
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("下载完成")); continue; }
                if (!CanPresent())
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("请等待当前操作完成")); continue; }
                try
                {
                    transfer.SetPhase(UpdateTransferPhase.Installing, L.T("正在准备更新…"));
                    _cancel.IsEnabled = false; _check.IsEnabled = false;
                    await _backend.Install(download, token); _exitAfterCleanup = true; return;
                }
                catch (UpdateInstallDeferredException)
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); _cancel.IsEnabled = _check.IsEnabled = true; transfer.SetPhase(UpdateTransferPhase.Ready, L.T("请等待当前操作完成")); }
            }
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus(L.T("更新下载已取消")); }
        catch (Exception exception) { if (!_closed) { SetStatus(L.T("更新失败") + " | " + exception.Message); Log(L.T("更新失败") + " | " + exception.Message); } }
        finally
        {
            _downloadWindow?.Finish(); _downloadWindow = null; _check.Content = Text(L.T("立即检查"));
            try { await Task.Run(transfer.Dispose); } catch (Exception exception) { Log(L.T("更新失败") + " | " + exception.Message); }
        }
    }

    private async Task CreateShortcutAsync(bool automatic)
    {
        if (_closed || _closingSuspended || IsBusy) return;
        if (!automatic && !CanPresent()) { SetStatus(L.T("请等待当前操作完成")); return; }
        var token = BeginOperation();
        try
        {
            var result = await _backend.Shortcut(token);
            if (!_closed) { SetStatus(result); Log(L.F($"快捷方式：{result}")); }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closed) { SetStatus(L.T("快捷方式创建失败") + " | " + exception.Message); Log(L.T("快捷方式创建失败") + " | " + exception.Message); } }
        finally { EndOperation(); }
    }

    private ContentDialog NewDialog(string title, StackPanel body, string primary) => new()
    {
        XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = title, PrimaryButtonText = primary,
        CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close,
        Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(100, Math.Min(520, XamlRoot.Size.Height - 200)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
    };

    private async Task<ContentDialogResult> ShowCancellableDialog(ContentDialog dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        return await Present(dialog);
    }
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size };
    private async Task OpenReleaseLinkAsync(string url)
    {
        if (_closed || !ReleaseNotesParser.SafeLink(url)) return;
        try
        {
            if (_backend.ConfirmExternalLink is null || !await _backend.ConfirmExternalLink(url) || _closed) return;
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception exception) { if (!_closed) SetStatus(exception.Message); }
    }
    private void SetStatus(string message) { if (!_closed) _status.Text = message; }
    internal async Task CloseAndWaitAsync()
    {
        var finished = _operationFinished?.Task; Close();
        if (finished is not null) await finished;
    }
    internal async Task CancelAndWaitAsync()
    {
        _closingSuspended = true;
        var finished = _operationFinished?.Task; _operation?.Cancel();
        if (finished is not null) await finished;
    }
    internal void SuspendForClosing() => _closingSuspended = true;
    internal void ResumeAfterCloseCancellation() { if (!_closed) _closingSuspended = false; }
    internal void Close() { _closed = true; _buildDateTimer.Stop(); _lifetime.Cancel(); _operation?.Cancel(); _downloadWindow?.Finish(); _downloadWindow = null; }
}
