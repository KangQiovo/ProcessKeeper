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
        return UpdateVersion.TryParse(information, out var parsed) ? parsed!.Value : assembly.GetName().Version?.ToString(3) ?? "1.6.0";
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
    private readonly HashSet<string> _shownUpdateVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _updatePromptTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void BuildUpdateSettings(StackPanel about)
    {
        var panel = new StackPanel { Tag = "updates-settings", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 24) };
        about.Children.Add(panel); panel.Children.Add(Text(L.T("检查更新"), 20));
        panel.Children.Add(Text(L.F($"当前版本：{CurrentAppVersion}"), 12));
        _buildDateText = Text(L.F($"构建时间：{BuildInfo.DisplayBuildDate}"), 12); _buildDateText.Tag = "update-build-date"; panel.Children.Add(_buildDateText);
        _buildDateTimer.Start();
        panel.Children.Add(Text(L.T("GitHub 仓库")));
        var repository = Text(UpdatePolicy.Repository); repository.Tag = "update-repository";
        panel.Children.Add(repository);
        var startup = new CheckBox { Tag = "update-startup", IsChecked = _updates.CheckOnStartup, Content = new TextBlock { Text = L.T("启动时检查更新"), TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 12) };
        var shortcut = new CheckBox { Tag = "update-shortcut", IsChecked = _updates.AutoDesktopShortcut, Content = new TextBlock { Text = L.T("自动创建桌面快捷方式"), TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(startup); panel.Children.Add(shortcut);
        panel.Children.Add(Text(L.T("更新源")));
        var source = UpdateCombo("update-source", new[] { L.T("自动选择最低延迟"), L.T("官方 GitHub"), L.T("第三方加速") }, (int)_updates.SourceMode); panel.Children.Add(source);
        var nodeLabel = Text(L.T("第三方节点")); panel.Children.Add(nodeLabel);
        var nodes = new[] { new UpdateSource("auto", L.T("自动选择最低延迟"), "", false) }.Concat(UpdateSources.All.Where(item => !item.IsOfficial)).ToArray();
        var node = UpdateCombo("update-node", nodes.Select(item => item.Name).ToArray(), Math.Max(0, Array.FindIndex(nodes, item => item.Id == _updates.ThirdPartySourceId))); panel.Children.Add(node);
        void UpdateNodeVisibility() { node.Visibility = nodeLabel.Visibility = source.SelectedIndex == (int)UpdateSourceMode.ThirdParty ? Visibility.Visible : Visibility.Collapsed; }
        source.SelectionChanged += (_, _) => UpdateNodeVisibility(); UpdateNodeVisibility();
        panel.Children.Add(Text(L.T("版本信息来自 GitHub 官方；更新源用于下载安装包。"), 12));
        panel.Children.Add(Text(L.T("下载前检测实际文件延迟，自动使用响应最快的可用源。"), 12));
        bool Save()
        {
            try
            {
                var preferences = UpdatePreferencesStore.Validate(new UpdatePreferences(CheckOnStartup: startup.IsChecked == true, SourceMode: (UpdateSourceMode)source.SelectedIndex, ThirdPartySourceId: nodes[Math.Max(0, node.SelectedIndex)].Id, AutoDesktopShortcut: shortcut.IsChecked == true));
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
        if (_closed || _shortcutBusy || !_updatesReadable) return;
        _shortcutBusy = true;
        try
        {
            if (!await Task.Run(_backend.Updates.HasTrustedLauncher, _life.Token))
            { if (manual) SetUpdateStatus(L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。")); return; }
            if (_closed || _life.IsCancellationRequested || (!manual && !_updates.AutoDesktopShortcut)) return;
            var result = await _backend.Updates.CreateShortcut();
            if (manual || !result.Success) SetUpdateStatus(result.Message);
            if (result.Success && !result.AlreadyExists) Log(result.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (manual) SetUpdateStatus(L.T("快捷方式创建失败") + " | " + ex.Message); }
        finally { _shortcutBusy = false; }
    }
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_closed || !_updatesReadable || _updateDownloading || _updateDialogOpen) return;
        var preferences = _updates;
        var generation = ++_updateCheckGeneration;
        _updateCheckCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); _updateCheckCancellation = cancellation;
        try
        {
            SetUpdateStatus(L.T("正在检查更新…"));
            var result = await _backend.Updates.Check(preferences, CurrentAppVersion, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || generation != _updateCheckGeneration || preferences != _updates) return;
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
        finally { if (ReferenceEquals(_updateCheckCancellation, cancellation)) _updateCheckCancellation = null; cancellation.Dispose(); }
    }
    private bool UpdateUiAvailable() => !_closed && !_busy && !_updateDialogOpen && !_riskModeDialogOpen && !_updateDownloading && Overlay.Visibility != Visibility.Visible && OwnedWindows.Count == 0;
    private static string ReleaseIdentity(UpdateRelease release) => release.Repository + "|" + release.Tag;
    private async Task ShowPendingUpdateAsync()
    {
        if (_pendingUpdate is null || _closed) { _updatePromptTimer.Stop(); return; }
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
        if (selected is not null && !_closed) await DownloadUpdateAsync(selected);
    }
    private sealed record UpdateSelection(UpdateRelease Release, UpdateAsset Asset, UpdatePreferences Preferences);
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
        var preferences = _updates;
        var window = UpdateWindow(L.T("检查更新"), 720, out _, out var body, out var actions);
        using var dialogCancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        window.Closed += (_, _) => dialogCancellation.Cancel();
        body.Children.Add(Text(result.UpdateAvailable && result.LatestRelease is not null ? L.F($"发现新版本 | {result.LatestRelease.Version}") : result.Message, 22));
        body.Children.Add(Text(L.F($"当前版本：{CurrentAppVersion}"), 12)); body.Children.Add(Text(L.F($"更新仓库：{preferences.Repository}"), 12)); body.Children.Add(Text(L.T("版本信息来自 GitHub 官方；更新源用于下载安装包。"), 12));
        var releases = UpdateCombo("update-releases", result.Releases.Select(item => item.Tag + " | " + item.Name).ToArray(), 0); body.Children.Add(releases);
        body.Children.Add(Text(L.T("更新日志"), 18));
        var notes = new TextBox { Tag = "update-notes", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 16) }; body.Children.Add(notes);
        body.Children.Add(Text(L.T("发布文件"), 18));
        var assets = new ListBox { Tag = "update-assets", MinHeight = 54, MaxHeight = 180, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = window.Background, Foreground = window.Foreground, BorderThickness = new Thickness(0) }; ScrollViewer.SetHorizontalScrollBarVisibility(assets, ScrollBarVisibility.Disabled); body.Children.Add(assets);
        var restriction = Text("", 12); body.Children.Add(restriction);
        var probeStatus = Text("", 12); body.Children.Add(probeStatus);
        Button? probe = null; var probing = false;
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
            finally { probing = false; if (window.IsVisible) probe!.IsEnabled = assets.SelectedIndex >= 0; }
        }); probe.Tag = "update-probe"; body.Children.Add(probe);
        var page = Button(L.T("发布主页"), async () =>
        {
            if (releases.SelectedIndex < 0) return; var url = result.Releases[releases.SelectedIndex].HtmlUrl;
            var title = L.T("打开项目主页？"); var detail = L.T("将在默认浏览器打开以下网址：") + "\n" + url;
            var confirmed = _backend.Confirm is not null ? await _backend.Confirm(title, detail) : MessageBox.Show(window, detail, "Process Keeper | " + title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK;
            if (confirmed) OpenWebsite(url);
        }); body.Children.Add(page);
        var later = new Button { Content = L.T("取消"), Tag = "update-later", Style = (Style)Resources[typeof(Button)], IsCancel = true, IsDefault = true };
        var download = new Button { Content = L.T("下载并更新"), Tag = "update-download", Style = (Style)Resources[typeof(Button)], IsEnabled = false };
        actions.Children.Add(later); actions.Children.Add(download);
        var trusted = await Task.Run(_backend.Updates.HasTrustedLauncher, _life.Token);
        if (_closed || _life.IsCancellationRequested || preferences != _updates) return null;
        void SelectAsset()
        {
            var release = result.Releases[Math.Max(0, releases.SelectedIndex)]; var asset = assets.SelectedIndex >= 0 ? release.Assets[assets.SelectedIndex] : null;
            download.IsEnabled = trusted && asset?.CanAutoInstall == true;
            probe.IsEnabled = !probing && asset is not null;
            restriction.Text = !trusted ? L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。") : asset?.Restriction ?? L.T("没有可用发布文件。");
        }
        void SelectRelease()
        {
            var release = result.Releases[Math.Max(0, releases.SelectedIndex)]; notes.Text = string.IsNullOrWhiteSpace(release.Body) ? L.T("未提供更新日志。") : release.Body;
            assets.Items.Clear(); foreach (var asset in release.Assets) assets.Items.Add(new ListBoxItem { Content = new TextBlock { Text = asset.Name + " | " + FormatUpdateBytes(asset.Size) + (string.IsNullOrWhiteSpace(asset.Restriction) ? "" : " | " + asset.Restriction), TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(4, 7, 4, 7) });
            assets.SelectedIndex = release.Assets.Count > 0 ? Math.Max(0, release.Assets.ToList().FindIndex(item => item.CanAutoInstall)) : -1; SelectAsset();
        }
        releases.SelectionChanged += (_, _) => SelectRelease(); assets.SelectionChanged += (_, _) => SelectAsset(); SelectRelease();
        UpdateSelection? selection = null;
        later.Click += (_, _) => window.DialogResult = false;
        download.Click += (_, _) => { if (!download.IsEnabled || releases.SelectedIndex < 0 || assets.SelectedIndex < 0) return; var release = result.Releases[releases.SelectedIndex]; selection = new UpdateSelection(release, release.Assets[assets.SelectedIndex], preferences); window.DialogResult = true; };
        using var canceled = _life.Token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })));
        if (_closed || _life.IsCancellationRequested) return null;
        await Task.Yield(); return window.ShowDialog() == true ? selection : null;
    }
    private static string FormatUpdateBytes(long bytes) => bytes >= 1024 * 1024 ? (bytes / 1048576d).ToString("0.0") + " MiB" : bytes >= 1024 ? (bytes / 1024d).ToString("0.0") + " KiB" : bytes + " B";

    private async Task DownloadUpdateAsync(UpdateSelection selection)
    {
        if (_closed || _busy || _updateDownloading || !_updatesReadable || !selection.Asset.CanAutoInstall) return;
        _updateDownloading = true; _busy = true;
        string? stage = null; var started = false; LegacyUpdateTransaction? preparedTransaction = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        try
        {
            if (!await Task.Run(_backend.Updates.HasTrustedLauncher, cancellation.Token)) throw new InvalidOperationException(L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。"));
            stage = await Task.Run(_backend.Updates.CreateStage, cancellation.Token);
            var window = UpdateWindow(L.T("下载并更新"), 620, out _, out var body, out var actions);
            body.Children.Add(Text(selection.Release.Tag + " | " + selection.Asset.Name, 20));
            var status = Text(L.T("正在下载更新…")); body.Children.Add(status);
            var progressBar = new ProgressBar { Tag = "update-progress", Minimum = 0, Maximum = 100, Height = 6, IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 16) }; body.Children.Add(progressBar);
            var cancel = new Button { Content = L.T("取消下载"), Tag = "update-cancel-download", IsCancel = true, IsDefault = true, Style = (Style)Resources[typeof(Button)] }; actions.Children.Add(cancel);
            var completion = new TaskCompletionSource<LegacyUpdateTransaction?>(); var finished = false; var operationStarted = false;
            cancel.Click += (_, _) => cancellation.Cancel(); window.Closing += (_, _) => { if (!finished) cancellation.Cancel(); };
            using var canceled = cancellation.Token.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (window.IsVisible) window.Close(); })));
            window.ContentRendered += async (_, _) =>
            {
                if (operationStarted) return; operationStarted = true;
                try
                {
                    var downloading = true;
                    var progress = new Progress<UpdateProgress>(update => { if (!downloading || finished || cancellation.IsCancellationRequested || !window.IsVisible) return; status.Text = update.Message + (update.TotalBytes > 0 ? "\n" + FormatUpdateBytes(update.BytesReceived) + " / " + FormatUpdateBytes(update.TotalBytes) : ""); progressBar.IsIndeterminate = update.TotalBytes <= 0; if (update.TotalBytes > 0) progressBar.Value = Math.Min(100, update.BytesReceived * 100d / update.TotalBytes); });
                    UpdateDownloadResult download;
                    try { download = await _backend.Updates.Download(selection.Preferences, selection.Release, selection.Asset, stage, progress, cancellation.Token); }
                    finally { downloading = false; }
                    cancellation.Token.ThrowIfCancellationRequested(); status.Text = L.T("正在准备更新…"); progressBar.IsIndeterminate = true;
                    preparedTransaction = await _backend.Updates.Prepare(download, cancellation.Token); cancellation.Token.ThrowIfCancellationRequested();
                    completion.TrySetResult(preparedTransaction);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { finished = true; if (window.IsVisible) window.Close(); }
            };
            window.ShowDialog();
            if (!operationStarted) { cancellation.Cancel(); return; }
            var prepared = await completion.Task;
            if (prepared is null || _closed || cancellation.IsCancellationRequested) return;
            SetUpdateStatus(L.T("下载完成"));
            if (!await Confirm(L.T("更新并重新启动"), L.T("更新下载完成，确认后将退出并重新启动 Process Keeper。") + "\n\n" + selection.Release.Tag + " | " + selection.Asset.Name)) return;
            if (_closed || cancellation.IsCancellationRequested) return;
            await FlushPerformanceAsync();
            var result = await Task.Run(prepared.Start, cancellation.Token);
            if (!result.Success) { SetUpdateStatus(L.T("更新失败") + " | " + result.Message); return; }
            started = true; Log(result.Message);
            if (stage is not null)
            {
                try { await Task.Run(() => _backend.Updates.CleanupStage(stage)); stage = null; }
                catch (Exception ex) { Log(ex.Message); }
            }
            if (_backend.Updates.ExitAfterUpdate is not null) _backend.Updates.ExitAfterUpdate(); else Close();
        }
        catch (OperationCanceledException) { SetUpdateStatus(L.T("更新下载已取消")); }
        catch (Exception ex) { SetUpdateStatus(L.T("更新失败") + " | " + ex.Message); Log(ex.Message); }
        finally
        {
            if (!started && preparedTransaction?.Discard is not null) { try { await Task.Run(preparedTransaction.Discard); } catch (Exception ex) { SetUpdateStatus(ex.Message); } }
            if (stage is not null) { try { await Task.Run(() => _backend.Updates.CleanupStage(stage)); } catch (Exception ex) { SetUpdateStatus(ex.Message); } }
            _updateDownloading = false; _busy = false;
        }
    }
}
