using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
    internal Func<UpdateRuntimeIdentity> Runtime { get; init; } = UpdatePackagePolicy.Current;
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
    internal static string ResolveCurrentVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var information = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        return UpdateVersion.TryParse(information, out var parsed) ? parsed!.Value : assembly.GetName().Version?.ToString(3) ?? "1.7.1";
    }
    private readonly ComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _node = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _onStartup = new(), _desktop = new();
    private readonly Button _check = new(), _shortcut = new(), _save = new(), _cancel = new(), _installUpdate = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
    private readonly TextBlock _buildDate = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DispatcherTimer _buildDateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 100 };
    private readonly List<string> _nodeIds = ["auto"];
    private readonly Queue<string> _sourceMessages = new();
    private bool _closed, _loading, _readable = true;
    private UpdateDownloadWindow? _downloadWindow;
    private UpdateTransferController? _transfer;
    private UpdateRelease? _transferRelease;
    private UpdateAsset? _transferAsset;
    private UpdateDownloadResult? _readyDownload;
    private Action? _requestInstall;
    private bool _showDownloadProgressWindow;
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
        panel.Children.Add(BuildAboutVersion(_currentVersion, _backend.Runtime()));
        var sourceHash = ResolveCurrentSourceHash();
        if (sourceHash.Length > 0) panel.Children.Add(new TextBlock { Text = L.F($"构建哈希：{sourceHash}"),
            IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
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
        _source.Header = L.T("更新源");
        foreach (var name in new[] { "自动（优先 GitHub 官方源）", "官方 GitHub", "第三方加速" }) _source.Items.Add(L.T(name));
        _node.Header = L.T("第三方节点"); _node.Items.Add(L.T("自动选择最低延迟"));
        foreach (var node in UpdateSources.All.Where(item => !item.IsOfficial)) { _nodeIds.Add(node.Id); _node.Items.Add(node.Name); }
        panel.Children.Add(_source); panel.Children.Add(_node);
        _onStartup.Content = Text(L.T("启动时检查更新")); _desktop.Content = Text(L.T("自动创建桌面快捷方式"));
        panel.Children.Add(_onStartup); panel.Children.Add(_desktop);
        _save.Content = Text(L.T("保存更新设置")); panel.Children.Add(_save);
        panel.Children.Add(Text(L.T("版本信息来自 GitHub 官方；更新源用于下载安装包。"), 12));
        panel.Children.Add(Text(L.T("自动模式优先使用 GitHub 官方源；不可用时再尝试第三方源。"), 12));
        panel.Children.Add(_progress); panel.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _installUpdate.Content = Text(L.T("更新并重启")); _installUpdate.Visibility = Visibility.Collapsed; _installUpdate.IsEnabled = false;
        _cancel.Content = Text(L.T("取消操作")); _cancel.Visibility = Visibility.Collapsed;
        foreach (var button in new[] { _installUpdate, _cancel }) button.Margin = new Thickness(0, 0, 8, 4);
        actions.Children.Add(_installUpdate); actions.Children.Add(_cancel);
        actions.SizeChanged += (_, _) => NativeCommandLayout.Reflow(actions); panel.Children.Add(actions);
        Content = panel;
        _source.SelectionChanged += (_, _) => { if (!_loading) _node.IsEnabled = _source.SelectedIndex == (int)UpdateSourceMode.ThirdParty && !IsBusy; };
        _check.Click += async (_, _) => await CheckAsync(false);
        _shortcut.Click += async (_, _) => await CreateShortcutAsync(false);
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _installUpdate.Click += (_, _) => _requestInstall?.Invoke();
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
            _source.SelectedIndex = (int)Preferences.SourceMode;
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
        if (!automatic && _transfer is not null) { RevealDownloadWindow(); return; }
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
        _showDownloadProgressWindow = false;
        var runtime = _backend.Runtime();
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
        panel.Children.Add(Text(release.Name + " | " + DisplayReleaseTag(release)));
        if (release.PublishedAt is { } published) panel.Children.Add(Text(TimeDisplay.Format(published.ToLocalTime()), 12));
        panel.Children.Add(Text(L.T("更新日志")));
        var noteDocument = await Task.Run(() => ReleaseNotesParser.Parse(string.IsNullOrWhiteSpace(release.Body) ? L.T("未提供更新日志。") : release.Body), token);
        panel.Children.Add(new ScrollViewer { Content = ReleaseNotesView.Create(noteDocument, async url => await OpenReleaseLinkAsync(url)),
            MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var assets = new ComboBox { Header = L.T("发布文件"), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var asset in release.Assets)
            assets.Items.Add(new ComboBoxItem { Content = Text($"{asset.Name} | {UpdatePackageDescription.Text(asset)} | {asset.Size / 1048576d:F2} MiB", 12), Tag = asset });
        var preferred = UpdatePackagePolicy.DefaultAsset(release, runtime);
        assets.SelectedIndex = preferred is null ? -1 : release.Assets.ToList().IndexOf(preferred);
        panel.Children.Add(assets);
        var reason = Text("", 12); panel.Children.Add(reason);
        var probe = new Button { Content = Text(L.T("检测节点延迟")) }; panel.Children.Add(probe);
        var probeStatus = Text("", 12); panel.Children.Add(probeStatus);
        // URI is validated by the update service; opening is always an explicit action.
        var website = new Button { Content = Text(L.T("发布主页")) };
        website.Click += async (_, _) => await OpenReleaseLinkAsync(release.HtmlUrl);
        panel.Children.Add(website);
        var showProgress = new CheckBox { Content = Text(L.T("显示下载进度窗口")), IsChecked = false };
        panel.Children.Add(showProgress);
        var dialog = NewDialog(L.F($"发现新版本 | {DisplayReleaseTag(release)}"), panel, L.T("下载并更新"));
        using var probing = CancellationTokenSource.CreateLinkedTokenSource(token);
        var probeToken = probing.Token;
        var probingNow = false;
        dialog.Closed += (_, _) => probing.Cancel();
        void Selection()
        {
            var asset = (assets.SelectedItem as ComboBoxItem)?.Tag as UpdateAsset;
            var unavailable = _backend.UnavailableReason();
            var eligible = asset?.CanAutoInstall == true && UpdatePackagePolicy.Supports(asset, runtime);
            reason.Text = unavailable ?? (asset is null ? L.T("请选择发布文件。") : asset.Restriction.Length > 0 ? asset.Restriction :
                PreservesInstallation(asset, runtime) ? L.T("当前安装目录会继续更新，原有卸载入口与安装状态会保留。") + "\n" + L.T("如需独立的免安装副本，请另外手动下载到其他目录。") :
                UpdatePackagePolicy.IsPackageChange(asset, runtime) ? L.T("所选文件与当前版本类型不同，更新后将切换版本类型。") : "");
            dialog.IsPrimaryButtonEnabled = !probingNow && unavailable is null && eligible;
            probe.IsEnabled = !probingNow && eligible;
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
        if ((assets.SelectedItem as ComboBoxItem)?.Tag is not UpdateAsset { CanAutoInstall: true } selected || !UpdatePackagePolicy.Supports(selected, runtime)) return null;
        if (UpdatePackagePolicy.IsPackageChange(selected, runtime))
        {
            var warning = new StackPanel { Spacing = 12, MaxWidth = 500 };
            warning.Children.Add(Text(L.T("所选更新包与当前运行的包类型不同，可能改变安装方式或兼容界面。确认后继续。")));
            if (PreservesInstallation(selected, runtime))
            {
                warning.Children.Add(Text(L.T("当前安装目录会继续更新，原有卸载入口与安装状态会保留。"), 12));
                warning.Children.Add(Text(L.T("如需独立的免安装副本，请另外手动下载到其他目录。"), 12));
            }
            warning.Children.Add(BuildAboutVersion(_currentVersion, runtime));
            warning.Children.Add(Text(selected.Name, 12));
            if (await ShowCancellableDialog(NewDialog(L.T("切换更新包类型？"), warning, L.T("下载并更新")), token) != ContentDialogResult.Primary || token.IsCancellationRequested) return null;
        }
        _showDownloadProgressWindow = showProgress.IsChecked == true;
        return selected;
    }

    private async Task DownloadAndInstallAsync(UpdatePreferences settings, UpdateRelease release, UpdateAsset asset, CancellationToken token)
    {
        var transfer = new UpdateTransferController(settings, _backend.CreateStage, _backend.DiscardStage,
            _backend.Download, _backend.CreateSession, _backend.ResumeDownload, _backend.DiscardSession);
        TaskCompletionSource<bool>? installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _transfer = transfer; _transferRelease = release; _transferAsset = asset; _readyDownload = null;
            _requestInstall = () => { if (CanRequestInstall) installChoice?.TrySetResult(true); };
            transfer.Changed += value =>
            {
                if (_closed || token.IsCancellationRequested) return;
                RefreshTransfer(value);
            };
            _check.IsEnabled = true; _check.Content = Text(L.T("查看更新器窗口"));
            if (_showDownloadProgressWindow) RevealDownloadWindow();
            var download = await transfer.DownloadAsync(release, asset, token);
            token.ThrowIfCancellationRequested(); if (_closed) return;
            _readyDownload = download; RefreshTransfer(transfer.Snapshot);
            while (!_closed)
            {
                installChoice ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                var currentChoice = installChoice;
                using (token.Register(() => currentChoice.TrySetCanceled())) await currentChoice.Task;
                installChoice = null;
                if (!CanPresent())
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("请等待当前操作完成")); continue; }
                var installer = download.Asset.DistributionKind == UpdateDistributionKind.Installer;
                var installAction = L.T(installer ? "退出并启动安装程序" : "更新并重新启动");
                transfer.SetPhase(UpdateTransferPhase.Confirming, installAction);
                var body = new StackPanel { Spacing = 12, MaxWidth = 500 };
                body.Children.Add(Text(L.T(installer ? "点击“退出并启动安装程序”后，Process Keeper 将退出并打开标准安装向导。请在安装向导中确认安装位置并完成更新。其他应用不会因此退出。" : "点击“更新并重新启动”后，Process Keeper 将退出并安装已下载的更新，然后重新启动。其他应用不会因此退出；请先完成本应用中的其他操作。")));
                if (installer) body.Children.Add(Text(L.T("安装向导中的完成或取消由你决定；取消后原有 EXE 会保留。"), 12));
                body.Children.Add(Text(download.Asset.Name + " | " + DisplayReleaseTag(download.Release)));
                body.Children.Add(Text("SHA-256 | " + download.Sha256, 12));
                if (await ShowCancellableDialog(NewDialog(installAction, body, installAction), token) != ContentDialogResult.Primary)
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
            _requestInstall = null; _readyDownload = null; _transfer = null; _transferRelease = null; _transferAsset = null;
            _installUpdate.Visibility = Visibility.Collapsed; _installUpdate.IsEnabled = false;
            try { await Task.Run(transfer.Dispose); } catch (Exception exception) { Log(L.T("更新失败") + " | " + exception.Message); }
        }
    }

    private bool CanRequestInstall => _transfer?.Snapshot.Phase == UpdateTransferPhase.Ready &&
        _readyDownload is { Asset.CanAutoInstall: true } download && _transferRelease is { } release && _transferAsset is { } asset &&
        download.Release.Repository == release.Repository && download.Release.Tag == release.Tag && download.Release.Version == release.Version &&
        download.Asset.Id == asset.Id && download.Asset.Name == asset.Name && download.Asset.Size == asset.Size &&
        download.Asset.PackageTarget == asset.PackageTarget && download.Asset.DistributionKind == asset.DistributionKind && download.Asset.Digest == asset.Digest &&
        download.Sha256.Length == 64 && download.Sha256.All(Uri.IsHexDigit) &&
        asset.Digest.Equals("sha256:" + download.Sha256, StringComparison.OrdinalIgnoreCase) &&
        UpdatePackagePolicy.Supports(download.Asset, _backend.Runtime());

    private void RefreshTransfer(UpdateTransferSnapshot value)
    {
        var ready = CanRequestInstall;
        _downloadWindow?.Refresh(value, ready); SetStatus(value.Message + " | " + value.Detail);
        _progress.IsIndeterminate = value.Total <= 0; _progress.Value = value.Percent;
        _installUpdate.Content = Text(L.T(_transferAsset?.DistributionKind == UpdateDistributionKind.Installer ? "退出并启动安装程序" : "更新并重启"));
        _installUpdate.Visibility = ready ? Visibility.Visible : Visibility.Collapsed; _installUpdate.IsEnabled = ready;
    }

    private void RevealDownloadWindow()
    {
        if (_closed || _transfer is null || _transferRelease is null || _transferAsset is null) return;
        _downloadWindow ??= new UpdateDownloadWindow(_transfer, _transferRelease, _transferAsset, ActualTheme,
            () => _operation?.Cancel(), () => _requestInstall?.Invoke(), ActivateProgressWindow);
        _downloadWindow.Refresh(_transfer.Snapshot, CanRequestInstall); _downloadWindow.Reveal();
    }

    internal static string DisplayReleaseTag(UpdateRelease release) => release.Tag == "v" + release.Version
        ? "v" + ReleaseIdentity.DisplayVersion(release.Version) : release.Tag;

    private static string ResolveCurrentSourceHash()
    {
        var information = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainWindow).Assembly)?.InformationalVersion;
        if (!UpdateVersion.TryParse(information, out _)) return "";
        var marker = information!.IndexOf('+');
        var hash = marker >= 0 ? information[(marker + 1)..] : "";
        return hash.Length is 40 or 64 && hash.All(Uri.IsHexDigit) ? hash : "";
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

    private static bool PreservesInstallation(UpdateAsset asset, UpdateRuntimeIdentity runtime) =>
        runtime.DistributionKind == UpdateDistributionKind.Installer && asset.DistributionKind == UpdateDistributionKind.Portable;

    internal static IReadOnlyList<string> DistributionBadges(UpdateRuntimeIdentity runtime)
    {
        var labels = new List<string>();
        if (runtime.PackageFlavor == UpdatePackageTarget.Universal) labels.Add(L.T("合包"));
        labels.Add(L.T(runtime.DistributionKind switch
        {
            UpdateDistributionKind.Installer => runtime.CompatibilityUi ? "兼容安装版" : "安装版",
            UpdateDistributionKind.Portable => runtime.CompatibilityUi ? "兼容免安装版" : "免安装版",
            _ => "运行方式未验证"
        }));
        if (runtime.DistributionKind == UpdateDistributionKind.Unknown && runtime.CompatibilityUi) labels.Add(L.T("兼容界面"));
        return labels;
    }

    private static RichTextBlock BuildAboutVersion(string version, UpdateRuntimeIdentity runtime)
    {
        var line = new RichTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var paragraph = new Paragraph(); paragraph.Inlines.Add(new Run { Text = L.F($"当前版本：{ReleaseIdentity.DisplayVersion(version.Split('+')[0])}") + "  " });
        foreach (var label in DistributionBadges(runtime))
        {
            var badge = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        Background="{ThemeResource ControlFillColorSecondaryBrush}" BorderBrush="{ThemeResource ControlStrokeColorDefaultBrush}"
                        BorderThickness="1" CornerRadius="4" Padding="7,2" VerticalAlignment="Center">
                    <TextBlock FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                </Border>
                """);
            ((TextBlock)badge.Child).Text = label;
            paragraph.Inlines.Add(new InlineUIContainer { Child = badge });
            paragraph.Inlines.Add(new Run { Text = " " });
        }
        line.Blocks.Add(paragraph); return line;
    }
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
