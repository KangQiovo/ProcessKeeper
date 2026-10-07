using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private static readonly string CurrentAppVersion = ResolveCurrentVersion();
    private static string ResolveCurrentVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var information = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        return UpdateVersion.TryParse(information, out var parsed) ? parsed!.Value : assembly.GetName().Version?.ToString(3) ?? "1.7.1";
    }
    private UpdatePreferences _updates = new();
    private bool _updatesReadable = true, _updateDialogOpen, _updateDownloading, _shortcutBusy;
    private int _updateCheckGeneration;
    private string _updateStatus = "";
    private TextBlock? _updateStatusText;
    private TextBlock? _buildDateText;
    private readonly DispatcherTimer _buildDateTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _updateCheckCancellation;
    private UpdateCheckResult? _pendingUpdate;
    private UpdateDownloadWindow? _updateDownloadWindow;
    private UpdateTransferController? _updateTransfer;
    private UpdateSelection? _activeUpdateSelection;
    private UpdateRuntimeIdentity? _activeUpdateRuntime;
    private UpdateDownloadResult? _verifiedUpdateDownload;
    private Action? _requestUpdateInstall;
    private CancellationTokenSource? _updateDownloadCancellation;
    private TaskCompletionSource<bool>? _updateTransferFinished;
    private TaskCompletionSource<bool>? _shortcutFinished;
    private readonly HashSet<Task> _updateChecks = new();
    private CancellationTokenSource? _releaseDialogCancellation;
    private readonly HashSet<string> _shownUpdateVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _updatePromptTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void BuildUpdateSettings(StackPanel about)
    {
        var panel = new StackPanel { Tag = "updates-settings", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 24) };
        about.Children.Add(panel); panel.Children.Add(Text(L.T("检查更新"), 20));
        panel.Children.Add(Text(L.F($"当前版本：{DisplayAboutVersion(CurrentAppVersion)}"), 12));
        _buildDateText = Text(L.F($"构建时间：{BuildInfo.DisplayBuildDate}"), 12); _buildDateText.Tag = "update-build-date"; panel.Children.Add(_buildDateText);
        _buildDateTimer.Start();
        var startup = new CheckBox { Tag = "update-startup", IsChecked = _updates.CheckOnStartup, Content = new TextBlock { Text = L.T("启动时检查更新"), TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 12) };
        var shortcut = new CheckBox { Tag = "update-shortcut", IsChecked = _updates.AutoDesktopShortcut, Content = new TextBlock { Text = L.T("自动创建桌面快捷方式"), TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(startup); panel.Children.Add(shortcut);
        panel.Children.Add(Text(L.T("更新源")));
        var source = UpdateCombo("update-source", new[] { L.T("自动（优先 GitHub 官方源）"), L.T("官方 GitHub"), L.T("第三方加速") }, (int)_updates.SourceMode); panel.Children.Add(source);
        var nodeLabel = Text(L.T("第三方节点")); panel.Children.Add(nodeLabel);
        var nodes = new[] { new UpdateSource("auto", L.T("自动选择最低延迟"), "", false) }.Concat(UpdateSources.All.Where(item => !item.IsOfficial)).ToArray();
        var node = UpdateCombo("update-node", nodes.Select(item => item.Name).ToArray(), Math.Max(0, Array.FindIndex(nodes, item => item.Id == _updates.ThirdPartySourceId))); panel.Children.Add(node);
        void UpdateNodeVisibility() { node.Visibility = nodeLabel.Visibility = source.SelectedIndex == (int)UpdateSourceMode.ThirdParty ? Visibility.Visible : Visibility.Collapsed; }
        source.SelectionChanged += (_, _) => UpdateNodeVisibility(); UpdateNodeVisibility();
        panel.Children.Add(Text(L.T("版本信息来自 GitHub 官方；更新源用于下载安装包。"), 12));
        panel.Children.Add(Text(L.T("自动模式优先使用 GitHub 官方源；不可用时再尝试第三方源。"), 12));
        bool Save()
        {
            try
            {
                var preferences = UpdatePreferencesStore.Validate(_updates with { CheckOnStartup = startup.IsChecked == true, SourceMode = (UpdateSourceMode)source.SelectedIndex, ThirdPartySourceId = nodes[Math.Max(0, node.SelectedIndex)].Id, AutoDesktopShortcut = shortcut.IsChecked == true });
                new UpdatePreferencesStore(_directory).Save(preferences); ApplyUpdatePreferences(preferences);
                SetUpdateStatus(L.T("更新设置已保存")); return true;
            }
            catch (Exception ex) { SetUpdateStatus(ex.Message); return false; }
        }
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(actions);
        var save = Button(L.T("保存更新设置"), () => Save()); save.Tag = "update-save"; actions.Children.Add(save);
        var check = Button(L.T("立即检查"), async () => { if (Save()) await CheckUpdatesAsync(true); }); check.Tag = "update-check"; actions.Children.Add(check);
        var create = Button(L.T("创建桌面快捷方式"), async () => await EnsureDesktopShortcutAsync(true)); create.Tag = "update-create-shortcut"; actions.Children.Add(create);
        _updateStatusText = Text(_updateStatus, 12); _updateStatusText.Tag = "update-status"; panel.Children.Add(_updateStatusText);
    }
    private static ComboBox UpdateCombo(string tag, string[] items, int selected) => new()
    { Tag = tag, ItemsSource = items, SelectedIndex = selected, MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12), Foreground = Brushes.Black, Background = Brushes.White };
    private void SetUpdateStatus(string message)
    { if (_closed) return; _updateStatus = message; if (_updateStatusText is not null) _updateStatusText.Text = message; }
    private void RefreshBuildDateLabel()
    { if (!_closed && _buildDateText is not null) _buildDateText.Text = L.F($"构建时间：{BuildInfo.DisplayBuildDate}"); }
    private void ApplyUpdatePreferences(UpdatePreferences preferences)
    {
        preferences = UpdatePreferencesStore.Validate(preferences);
        if (_updates != preferences)
        {
            _updateCheckGeneration++; _updateCheckCancellation?.Cancel();
            _pendingUpdate = null; _updatePromptTimer.Stop();
        }
        _updates = preferences; _updatesReadable = true;
    }
    private async Task InitializeUpdatesAsync()
    {
        await Task.Yield();
        if (_closed || !_updatesReadable || !_backend.IsAdministrator) return;
        var shortcut = _updates.AutoDesktopShortcut ? EnsureDesktopShortcutAsync(false) : Task.CompletedTask;
        var check = _updates.CheckOnStartup ? CheckUpdatesAsync(false) : Task.CompletedTask;
        await Task.WhenAll(shortcut, check);
    }
    private async Task EnsureDesktopShortcutAsync(bool manual)
    {
        if (_closed || _replacementRequested || _savingBeforeClose || _closingMotion || _shortcutBusy || !_updatesReadable) return;
        _shortcutBusy = true;
        _shortcutFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!await Task.Run(_backend.Updates.HasTrustedLauncher, _life.Token))
            { if (manual) SetUpdateStatus(L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。")); return; }
            if (_closed || _replacementRequested || _savingBeforeClose || _closingMotion || _life.IsCancellationRequested || (!manual && !_updates.AutoDesktopShortcut)) return;
            var result = await _backend.Updates.CreateShortcut();
            if (manual || !result.Success) SetUpdateStatus(result.Message);
            if (result.Success && !result.AlreadyExists) Log(result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (manual) SetUpdateStatus(L.T("快捷方式创建失败") + " | " + ex.Message); }
        finally { _shortcutBusy = false; _shortcutFinished?.TrySetResult(true); _shortcutFinished = null; }
    }
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_replacementRequested || _savingBeforeClose || _closingMotion) return;
        if (manual && _updateDownloading) { RevealUpdateProgress(); return; }
        if (_closed || !_updatesReadable || _updateDownloading || _updateDialogOpen) return;
        var preferences = _updates;
        var generation = ++_updateCheckGeneration;
        _updateCheckCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); _updateCheckCancellation = cancellation;
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); _updateChecks.Add(finished.Task);
        try
        {
            SetUpdateStatus(L.T("正在检查更新…"));
            var result = await _backend.Updates.Check(preferences, CurrentAppVersion, cancellation.Token);
            if (_closed || _replacementRequested || cancellation.IsCancellationRequested || generation != _updateCheckGeneration || preferences != _updates) return;
            SetUpdateStatus(result.Message);
            if (manual && result.Releases.Count > 0)
            {
                if (UpdateUiAvailable()) await ShowUpdateReleasesAsync(result);
                else { _pendingUpdate = result; _updatePromptTimer.Start(); }
            }
            else if (result.UpdateAvailable && result.LatestRelease is not null && !_shownUpdateVersions.Contains(ReleaseIdentity(result.LatestRelease)))
            { _pendingUpdate = result; _updatePromptTimer.Start(); await ShowPendingUpdateAsync(); }
        }
        catch (OperationCanceledException) { if (manual && generation == _updateCheckGeneration) SetUpdateStatus(L.T("更新检查已取消")); }
        catch (Exception ex) { if (generation == _updateCheckGeneration) SetUpdateStatus(L.T("检查更新失败") + " | " + ex.Message); }
        finally { if (ReferenceEquals(_updateCheckCancellation, cancellation)) _updateCheckCancellation = null; cancellation.Dispose(); finished.TrySetResult(true); _updateChecks.Remove(finished.Task); }
    }
    private bool UpdateUiAvailable() => !_closed && !_replacementRequested && !_savingBeforeClose && !_closingMotion && !_busy && !_updateDialogOpen && !_riskModeDialogOpen && !_updateDownloading && Overlay.Visibility != Visibility.Visible && OwnedWindows.Count == 0;
    private static string ReleaseIdentity(UpdateRelease release) => release.Repository + "|" + release.Tag;
    private async Task ShowPendingUpdateAsync()
    {
        if (_pendingUpdate is null || _closed || _replacementRequested) { _updatePromptTimer.Stop(); return; }
        if (!UpdateUiAvailable()) return;
        var pending = _pendingUpdate; _pendingUpdate = null; _updatePromptTimer.Stop();
        await ShowUpdateReleasesAsync(pending);
    }
    private async Task ShowUpdateReleasesAsync(UpdateCheckResult result)
    {
        if (!UpdateUiAvailable() || result.Releases.Count == 0) return;
        _updateDialogOpen = true;
        UpdateSelection? selected = null;
        try
        {
            if (result.LatestRelease is not null) _shownUpdateVersions.Add(ReleaseIdentity(result.LatestRelease));
            selected = await ChooseUpdateReleaseAsync(result);
        }
        finally { _updateDialogOpen = false; }
        if (selected is not null && !_closed && !_replacementRequested) await DownloadUpdateAsync(selected);
    }
    private sealed record UpdateSelection(UpdateRelease Release, UpdateAsset Asset, UpdatePreferences Preferences, bool ShowProgressWindow = false);
    private Window UpdateWindow(string title, double width, out Grid root, out StackPanel body, out WrapPanel actions)
    {
        var area = _backend.WorkingArea();
        var window = new Window { Owner = this, Title = "Process Keeper | " + title, Width = Math.Min(width, Math.Max(1, area.Width - 16)), Height = Math.Min(680, Math.Max(1, area.Height - 16)), MinHeight = 0, MaxHeight = Math.Max(1, area.Height - 16), ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = (Brush)Resources["PageBrush"], Foreground = (Brush)Resources["InkBrush"] };
        root = new Grid { Margin = new Thickness(24), Background = window.Background };
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(actions, 1); root.Children.Add(actions); window.Content = root; return window;
    }
    private async Task<UpdateSelection?> ChooseUpdateReleaseAsync(UpdateCheckResult result)
    {
        if (_closed || _replacementRequested) return null;
        var preferences = _updates;
        var window = UpdateWindow(L.T("检查更新"), 720, out _, out var body, out var actions);
        using var dialogCancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, _updateCheckCancellation?.Token ?? CancellationToken.None);
        window.Closed += (_, _) => dialogCancellation.Cancel();
        body.Children.Add(Text(result.UpdateAvailable && result.LatestRelease is not null ? L.F($"发现新版本 | {ProcessKeeper.Core.ReleaseIdentity.DisplayVersion(result.LatestRelease.Version)}") : result.Message, 22));
        body.Children.Add(Text(L.F($"当前版本：{DisplayAboutVersion(CurrentAppVersion)}"), 12)); body.Children.Add(Text(L.F($"更新仓库：{preferences.Repository}"), 12)); body.Children.Add(Text(L.T("版本信息来自 GitHub 官方；更新源用于下载安装包。"), 12));
        var releases = UpdateCombo("update-releases", result.Releases.Select(item => DisplayUpdateReleaseTag(item) + " | " + item.Name).ToArray(), 0); body.Children.Add(releases);
        body.Children.Add(Text(L.T("更新日志"), 18));
        var notes = new ScrollViewer { Tag = "update-notes", MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 16) }; body.Children.Add(notes);
        body.Children.Add(Text(L.T("发布文件"), 18));
        var assets = new ListBox { Tag = "update-assets", MinHeight = 54, MaxHeight = 180, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = window.Background, Foreground = window.Foreground, BorderThickness = new Thickness(0) }; ScrollViewer.SetHorizontalScrollBarVisibility(assets, ScrollBarVisibility.Disabled); body.Children.Add(assets);
        var restriction = Text("", 12); body.Children.Add(restriction);
        var probeStatus = Text("", 12); body.Children.Add(probeStatus);
        Button? probe = null; var probing = false; Action? refreshAsset = null;
        probe = Button(L.T("检测节点延迟"), async () =>
        {
            if (probing || assets.SelectedIndex < 0 || releases.SelectedIndex < 0) return;
            probing = true; probe!.IsEnabled = false;
            var release = result.Releases[releases.SelectedIndex]; var asset = release.Assets[assets.SelectedIndex];
            try
            {
                var measured = await _backend.Updates.Probe(preferences, release, asset, new Progress<UpdateProgress>(progress => { if (probing && window.IsVisible && !dialogCancellation.IsCancellationRequested) probeStatus.Text = progress.Message; }), dialogCancellation.Token);
                if (window.IsVisible) probeStatus.Text = string.Join("\n", measured.Select(item =>
                    (UpdateSources.All.FirstOrDefault(source => source.Id == item.SourceId)?.Name ?? item.SourceId) + " | " +
                    (item.LatencyMilliseconds.HasValue ? item.LatencyMilliseconds + " ms | " : "") + item.Message));
            }
            catch (OperationCanceledException) { } catch (Exception ex) { if (window.IsVisible) probeStatus.Text = ex.Message; }
            finally { probing = false; if (window.IsVisible) refreshAsset?.Invoke(); }
        }); probe.Tag = "update-probe"; body.Children.Add(probe);
        var page = Button(L.T("发布主页"), async () =>
        {
            if (releases.SelectedIndex < 0) return; var url = result.Releases[releases.SelectedIndex].HtmlUrl;
            var title = L.T("打开项目主页？"); var detail = L.T("将在默认浏览器打开以下网址：") + "\n" + url;
            var confirmed = _backend.Confirm is not null ? await _backend.Confirm(title, detail) : MessageBox.Show(window, detail, "Process Keeper | " + title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
            if (confirmed) OpenWebsite(url);
        }); body.Children.Add(page);
        var showProgress = new CheckBox { Tag = "update-show-progress", IsChecked = false, Content = new TextBlock { Text = L.T("显示下载进度窗口"), TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 12) };
        body.Children.Add(showProgress);
        var later = new Button { Content = L.T("取消"), Tag = "update-later", Style = (Style)Resources[typeof(Button)], IsCancel = true, IsDefault = true };
        var download = new Button { Content = L.T("下载并更新"), Tag = "update-download", Style = (Style)Resources[typeof(Button)], IsEnabled = false };
        actions.Children.Add(later); actions.Children.Add(download);
        var trusted = await Task.Run(_backend.Updates.HasTrustedLauncher, _life.Token);
        var runtime = await Task.Run(_backend.Updates.RuntimeIdentity, _life.Token);
        if (_closed || _life.IsCancellationRequested || preferences != _updates) return null;
        void SelectAsset()
        {
            var release = result.Releases[Math.Max(0, releases.SelectedIndex)]; var asset = assets.SelectedIndex >= 0 ? release.Assets[assets.SelectedIndex] : null;
            var eligible = asset?.CanAutoInstall == true && UpdatePackagePolicy.Supports(asset, runtime);
            download.IsEnabled = trusted && eligible;
            probe.IsEnabled = !probing && eligible;
            restriction.Text = !trusted ? L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。") : asset?.Restriction ?? L.T("没有可用发布文件。");
            if (trusted && eligible && asset is not null)
                restriction.Text = PreservesInstallation(asset, runtime) ? InstalledPayloadNotice() : UpdatePackagePolicy.IsPackageChange(asset, runtime)
                    ? L.T("所选更新包与当前运行的包类型不同，可能改变安装方式或兼容界面。确认后继续。") : asset.Restriction;
            else if (trusted && asset is not null && asset.Restriction.Length == 0) restriction.Text = L.T("所选更新包不支持当前系统或运行方式。");
        }
        refreshAsset = SelectAsset;
        var noteGeneration = 0;
        async void SelectRelease()
        {
            var release = result.Releases[Math.Max(0, releases.SelectedIndex)];
            var generation = ++noteGeneration;
            assets.Items.Clear(); assets.SelectedIndex = -1; download.IsEnabled = probe.IsEnabled = false;
            var document = await Task.Run(() => ReleaseNotesParser.Parse(string.IsNullOrWhiteSpace(release.Body) ? L.T("未提供更新日志。") : release.Body));
            if (dialogCancellation.IsCancellationRequested || generation != noteGeneration) return;
            notes.Content = ReleaseNotesView.Create(document, async url =>
            {
                if (!ReleaseNotesParser.SafeLink(url) || dialogCancellation.IsCancellationRequested) return;
                var title = L.T("打开项目主页？"); var detail = L.T("将在默认浏览器打开以下网址：") + "\n" + url;
                var confirmed = _backend.Confirm is not null ? await _backend.Confirm(title, detail) : MessageBox.Show(window, detail, "Process Keeper | " + title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
                if (confirmed && !dialogCancellation.IsCancellationRequested) OpenWebsite(url);
            });
            assets.Items.Clear(); foreach (var asset in release.Assets) assets.Items.Add(new ListBoxItem { Content = new TextBlock { Text = asset.Name + " | " + UpdatePackageDescription.Text(asset) + " | " + FormatUpdateBytes(asset.Size) + (string.IsNullOrWhiteSpace(asset.Restriction) ? "" : " | " + asset.Restriction), TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(4, 7, 4, 7) });
            var preferred = UpdatePackagePolicy.DefaultAsset(release, runtime);
            assets.SelectedIndex = preferred is null ? -1 : release.Assets.ToList().IndexOf(preferred); SelectAsset();
        }
        releases.SelectionChanged += (_, _) => SelectRelease(); assets.SelectionChanged += (_, _) => SelectAsset(); SelectRelease();
        UpdateSelection? selection = null;
        later.Click += (_, _) => window.DialogResult = false;
        download.Click += async (_, _) =>
        {
            if (!download.IsEnabled || releases.SelectedIndex < 0 || assets.SelectedIndex < 0) return;
            var release = result.Releases[releases.SelectedIndex]; var asset = release.Assets[assets.SelectedIndex]; download.IsEnabled = false;
            try
            {
                if (!await ConfirmUpdatePackageChangeAsync(asset, runtime) || dialogCancellation.IsCancellationRequested || _closed || _replacementRequested) return;
                if (!window.IsVisible || releases.SelectedIndex < 0 || !ReferenceEquals(release, result.Releases[releases.SelectedIndex]) || assets.SelectedIndex < 0 || !ReferenceEquals(asset, release.Assets[assets.SelectedIndex]) || !asset.CanAutoInstall || !UpdatePackagePolicy.Supports(asset, runtime)) return;
                selection = new UpdateSelection(release, asset, preferences, showProgress.IsChecked == true); window.DialogResult = true;
            }
            catch (Exception ex) { if (window.IsVisible && !dialogCancellation.IsCancellationRequested) restriction.Text = ex.Message; }
            finally { if (window.IsVisible) SelectAsset(); }
        };
        using var canceled = dialogCancellation.Token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })));
        _releaseDialogCancellation = dialogCancellation;
        try
        {
            if (_closed || _replacementRequested || _savingBeforeClose || _closingMotion || dialogCancellation.IsCancellationRequested) return null;
            await Task.Yield();
            if (_closed || _replacementRequested || _savingBeforeClose || _closingMotion || dialogCancellation.IsCancellationRequested) return null;
            return window.ShowDialog() == true ? selection : null;
        }
        finally { if (ReferenceEquals(_releaseDialogCancellation, dialogCancellation)) _releaseDialogCancellation = null; }
    }
    private static string FormatUpdateBytes(long bytes) => bytes >= 1024 * 1024 ? (bytes / 1048576d).ToString("0.0") + " MiB" : bytes >= 1024 ? (bytes / 1024d).ToString("0.0") + " KiB" : bytes + " B";

    private bool VerifiedUpdateReady()
    {
        var download = _verifiedUpdateDownload; var selection = _activeUpdateSelection; var runtime = _activeUpdateRuntime;
        return !_closed && _updateTransfer?.Snapshot.Phase == UpdateTransferPhase.Ready && download is not null && selection is not null && runtime is not null &&
            SameUpdateReleaseIdentity(download.Release, selection.Release) && SameUpdateAssetIdentity(download.Asset, selection.Asset) && !string.IsNullOrWhiteSpace(download.FilePath) &&
            download.Sha256.Length == 64 && download.Sha256.All(Uri.IsHexDigit) &&
            string.Equals(selection.Asset.Digest, "sha256:" + download.Sha256, StringComparison.OrdinalIgnoreCase) &&
            download.Asset.CanAutoInstall && selection.Asset.CanAutoInstall && UpdatePackagePolicy.Supports(download.Asset, runtime) && UpdatePackagePolicy.Supports(selection.Asset, runtime);
    }
    private static bool SameUpdateReleaseIdentity(UpdateRelease actual, UpdateRelease selected) =>
        string.Equals(actual.Repository, selected.Repository, StringComparison.Ordinal) && string.Equals(actual.Tag, selected.Tag, StringComparison.Ordinal) &&
        string.Equals(actual.Version, selected.Version, StringComparison.Ordinal);
    private static bool SameUpdateAssetIdentity(UpdateAsset actual, UpdateAsset selected) =>
        actual.Id == selected.Id && actual.Size == selected.Size && actual.PackageTarget == selected.PackageTarget && actual.DistributionKind == selected.DistributionKind &&
        string.Equals(actual.Name, selected.Name, StringComparison.Ordinal) && string.Equals(actual.DownloadUrl, selected.DownloadUrl, StringComparison.Ordinal) &&
        string.Equals(actual.Digest, selected.Digest, StringComparison.OrdinalIgnoreCase);
    private void RequestUpdateInstall()
    { if (VerifiedUpdateReady()) _requestUpdateInstall?.Invoke(); }
    private void RevealUpdateProgress()
    {
        if (_closed || _replacementRequested || !_updateDownloading || _updateTransfer is null || _activeUpdateSelection is null) return;
        _updateDownloadWindow ??= new UpdateDownloadWindow(this, _updateTransfer, _activeUpdateSelection.Release, _activeUpdateSelection.Asset,
            () => _updateDownloadCancellation?.Cancel(), RequestUpdateInstall, _backend.Updates.ActivateProgressWindow, VerifiedUpdateReady);
        _updateDownloadWindow.Refresh(_updateTransfer.Snapshot); _updateDownloadWindow.Reveal();
    }
    private void RefreshMainUpdateProgress()
    {
        var state = _updateTransfer?.Snapshot;
        UpdateProgressArea.Visibility = _updateDownloading && state is not null ? Visibility.Visible : Visibility.Collapsed;
        if (state is null) return;
        UpdateProgressText.Text = state.Message; UpdateProgressDetail.Text = state.Detail;
        UpdateProgressBar.IsIndeterminate = state.Total <= 0 || state.Phase is UpdateTransferPhase.Starting or UpdateTransferPhase.Pausing;
        UpdateProgressBar.Value = state.Percent;
        UpdateViewAction.Content = L.T("查看更新器窗口"); UpdateViewAction.IsEnabled = state.Phase != UpdateTransferPhase.Installing;
        UpdateCancelAction.Content = L.T("取消下载"); UpdateCancelAction.IsEnabled = state.Phase != UpdateTransferPhase.Installing;
        UpdateInstallAction.Content = L.T(_activeUpdateSelection?.Asset.DistributionKind == UpdateDistributionKind.Installer ? "退出并启动安装程序" : "更新并重启");
        UpdateInstallAction.IsEnabled = VerifiedUpdateReady();
        UpdateInstallAction.Visibility = UpdateInstallAction.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        _updateDownloadWindow?.Refresh(state);
    }
    private void ViewUpdateProgressClicked(object sender, RoutedEventArgs e) => RevealUpdateProgress();
    private void InstallUpdateClicked(object sender, RoutedEventArgs e) => RequestUpdateInstall();
    private void CancelUpdateClicked(object sender, RoutedEventArgs e) => _updateDownloadCancellation?.Cancel();

    private async Task<bool> ConfirmUpdateRestartAsync(UpdateDownloadResult download, CancellationToken token)
    {
        bool installer = download.Asset.DistributionKind == UpdateDistributionKind.Installer;
        var action = L.T(installer ? "退出并启动安装程序" : "更新并重新启动");
        var detail = L.T(installer ? "点击“退出并启动安装程序”后，Process Keeper 将退出并打开标准安装向导。请在安装向导中确认安装位置并完成更新。其他应用不会因此退出。" :
            "点击“更新并重新启动”后，Process Keeper 将退出并安装已下载的更新，然后重新启动。其他应用不会因此退出；请先完成本应用中的其他操作。") +
            "\n\n" + download.Release.Tag + " | " + download.Asset.Name + "\nSHA-256 | " + download.Sha256;
        if (installer) detail += "\n\n" + L.T("安装向导中的完成或取消由你决定；取消后原有 EXE 会保留。");
        if (_backend.Confirm is not null) return await _backend.Confirm(action, detail);
        var window = UpdateWindow(action, 640, out _, out var body, out var actions);
        body.Children.Add(Text(detail));
        var cancel = Button(L.T("取消"), () => window.DialogResult = false); cancel.IsCancel = cancel.IsDefault = true;
        var accept = Button(action, () => window.DialogResult = true);
        actions.Children.Add(cancel); actions.Children.Add(accept);
        using var canceled = token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })));
        _updateDialogOpen = true;
        try { await Task.Yield(); return !token.IsCancellationRequested && !_closed && window.ShowDialog() == true; }
        finally { _updateDialogOpen = false; }
    }

    private async Task DownloadUpdateAsync(UpdateSelection selection)
    {
        if (_closed || _replacementRequested || _savingBeforeClose || _closingMotion || _busy || _updateDownloading || !_updatesReadable || !selection.Asset.CanAutoInstall) return;
        _updateDownloading = true;
        _updateTransferFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false; var lockedForInstall = false; LegacyUpdateTransaction? preparedTransaction = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _updateDownloadCancellation = cancellation;
        var transfer = new UpdateTransferController(selection.Preferences, _backend.Updates.CreateStage, _backend.Updates.CleanupStage,
            (preferences, release, asset, stage, progress, token) => _backend.Updates.Download(preferences, release, asset, stage, progress, token),
            _backend.Updates.CreateSession, _backend.Updates.ResumeDownload, _backend.Updates.DiscardSession);
        TaskCompletionSource<bool>? installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _updateTransfer = transfer; _activeUpdateSelection = selection; _verifiedUpdateDownload = null;
        _requestUpdateInstall = () => installChoice?.TrySetResult(true);
        RefreshMainUpdateProgress();
        try
        {
            if (!await Task.Run(_backend.Updates.HasTrustedLauncher, cancellation.Token)) throw new InvalidOperationException(L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。"));
            var runtime = await Task.Run(_backend.Updates.RuntimeIdentity, cancellation.Token);
            if (!UpdatePackagePolicy.Supports(selection.Asset, runtime)) throw new InvalidOperationException(L.T("所选更新包不支持当前系统或运行方式。"));
            _activeUpdateRuntime = runtime;
            transfer.Changed += update =>
            {
                if (_closed || cancellation.IsCancellationRequested) return;
                RefreshMainUpdateProgress(); SetUpdateStatus(update.Message + " | " + update.Detail);
            };
            if (selection.ShowProgressWindow) RevealUpdateProgress();
            var download = await transfer.DownloadAsync(selection.Release, selection.Asset, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _verifiedUpdateDownload = download; RefreshMainUpdateProgress();
            while (!_closed)
            {
                installChoice ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                var currentChoice = installChoice;
                using (cancellation.Token.Register(() => currentChoice.TrySetCanceled())) await currentChoice.Task;
                installChoice = null;
                if (_busy || HasPendingTool || _riskModeDialogOpen || _updateDialogOpen || Overlay.Visibility == Visibility.Visible)
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); SetUpdateStatus(L.T("请等待当前操作完成")); continue; }
                transfer.SetPhase(UpdateTransferPhase.Confirming, L.T(download.Asset.DistributionKind == UpdateDistributionKind.Installer ? "请确认退出并启动安装程序。" : "请确认更新并重新启动。"));
                if (!await ConfirmUpdateRestartAsync(download, cancellation.Token))
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("下载完成")); continue; }
                cancellation.Token.ThrowIfCancellationRequested();
                if (_busy || HasPendingTool || _closed || _riskModeDialogOpen || _updateDialogOpen || Overlay.Visibility == Visibility.Visible)
                { installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("请等待当前操作完成")); continue; }
                _busy = true;
                lockedForInstall = true;
                transfer.SetPhase(UpdateTransferPhase.Installing, L.T("正在准备更新…"));
                Navigation.IsEnabled = List.IsEnabled = false;
                preparedTransaction = await _backend.Updates.Prepare(download, cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
                await FlushPerformanceAsync();
                if (HasPendingTool || _closed || _riskModeDialogOpen || _updateDialogOpen || Overlay.Visibility == Visibility.Visible)
                {
                    if (preparedTransaction.Discard is not null) await Task.Run(preparedTransaction.Discard);
                    preparedTransaction = null; _busy = false; lockedForInstall = false;
                    if (!_closed) Navigation.IsEnabled = List.IsEnabled = true;
                    installChoice = new(TaskCreationOptions.RunContinuationsAsynchronously); transfer.SetPhase(UpdateTransferPhase.Ready, L.T("请等待当前操作完成")); continue;
                }
                cancellation.Token.ThrowIfCancellationRequested();
                var result = await Task.Run(preparedTransaction.Start, cancellation.Token);
                if (!result.Success) throw new IOException(result.Message);
                started = true; Log(result.Message);
                return;
            }
        }
        catch (OperationCanceledException) { SetUpdateStatus(L.T("更新下载已取消")); }
        catch (Exception ex) { SetUpdateStatus(L.T("更新失败") + " | " + ex.Message); Log(ex.Message); }
        finally
        {
            if (!started && preparedTransaction?.Discard is not null) { try { await Task.Run(preparedTransaction.Discard); } catch (Exception ex) { SetUpdateStatus(ex.Message); } }
            _updateDownloadWindow?.Finish(); _updateDownloadWindow = null;
            try { await Task.Run(transfer.Dispose); } catch (Exception ex) { SetUpdateStatus(ex.Message); }
            _updateDownloading = false;
            _updateTransfer = null; _activeUpdateSelection = null; _activeUpdateRuntime = null; _verifiedUpdateDownload = null; _requestUpdateInstall = null;
            RefreshMainUpdateProgress();
            _updateDownloadCancellation = null; _updateTransferFinished?.TrySetResult(true); _updateTransferFinished = null;
            if (lockedForInstall) { _busy = false; if (!_closed) Navigation.IsEnabled = List.IsEnabled = true; }
            if (started)
            {
                if (_backend.Updates.ExitAfterUpdate is not null) _backend.Updates.ExitAfterUpdate();
                else { _closeApproved = true; Close(); }
            }
        }
    }
    private async Task CancelUpdatesAndWaitAsync()
    {
        _pendingUpdate = null; _updatePromptTimer.Stop(); _updateCheckGeneration++;
        _updateCheckCancellation?.Cancel(); _releaseDialogCancellation?.Cancel(); _updateDownloadCancellation?.Cancel();
        var finished = _updateTransferFinished?.Task;
        if (finished is not null) await finished;
        var checks = _updateChecks.ToArray(); if (checks.Length > 0) await Task.WhenAll(checks);
        var shortcut = _shortcutFinished?.Task; if (shortcut is not null) await shortcut;
    }
}
