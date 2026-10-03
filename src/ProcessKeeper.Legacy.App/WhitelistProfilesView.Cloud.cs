using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal sealed partial class WhitelistProfilesView
{
    private CloudWhitelistProfiles? _cloudProfiles;
    private readonly bool _ownsCloudService;
    private CancellationTokenSource? _cloudRequest;
    private int _cloudGeneration;
    private CloudWhitelistProfilePreview? _cloudPreview;
    private readonly ComboBox _cloudChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _cloudName = new() { MaxLength = 80, Visibility = Visibility.Collapsed };
    private readonly TextBox _cloudEditor = new() { IsReadOnly = true, AcceptsReturn = true, Height = 210,
        TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly TextBlock _cloudSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _cloudLimit = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ProgressBar _cloudProgress = new() { IsIndeterminate = true, Height = 3, Visibility = Visibility.Collapsed };
    private readonly Expander _cloudPreviewPanel = new() { Visibility = Visibility.Collapsed, IsExpanded = true };
    private readonly Button _cloudRead = new Wpf.Ui.Controls.Button();
    private readonly Button _cloudImport = new Wpf.Ui.Controls.Button();

    private FrameworkElement CreateCloudSection()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T("云端白名单配置"), FontSize = 19,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = L.T("仅主动加载；导入计入五套配置。"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = L.T("配置来源 | KangQiovo/ProcessKeeper/community/profiles"), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var load = new Wpf.Ui.Controls.Button { Content = L.T("从 GitHub 加载"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        load.Click += async (_, _) => await RunAsync(LoadCloudProfilesAsync); _actions.Add(load); panel.Children.Add(load);
        _cloudProgress.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(_cloudProgress);
        AutomationProperties.SetName(_cloudChoice, L.T("选择云端配置"));
        _cloudChoice.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(_cloudChoice);
        _cloudChoice.SelectionChanged += (_, _) =>
        {
            _cloudPreview = null; _cloudPreviewPanel.Visibility = Visibility.Collapsed; _cloudName.Visibility = Visibility.Collapsed;
            _cloudSummary.Text = _cloudChoice.SelectedItem is ComboBoxItem { Tag: CloudWhitelistProfileDescriptor descriptor } ? descriptor.FileName : "";
            RefreshCloudControls();
        };
        var commands = new WrapPanel();
        _cloudRead.Content = L.T("预览云端配置"); _cloudRead.Margin = new Thickness(0, 0, 8, 8);
        _cloudImport.Content = L.T("导入并应用"); _cloudImport.Margin = new Thickness(0, 0, 0, 8);
        _cloudRead.Click += async (_, _) => await RunAsync(ReadCloudProfileAsync);
        _cloudImport.Click += async (_, _) => await RunAsync(ImportCloudProfileAsync);
        _actions.Add(_cloudRead); _actions.Add(_cloudImport); commands.Children.Add(_cloudRead); commands.Children.Add(_cloudImport); panel.Children.Add(commands);
        _cloudSummary.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(_cloudSummary);
        _cloudLimit.Text = L.T("本地配置已达五套，请先删除未使用的配置。");
        _cloudLimit.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(_cloudLimit);
        AutomationProperties.SetName(_cloudName, L.T("云端配置名称"));
        _cloudName.ToolTip = L.T("云端配置名称"); _cloudName.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(_cloudName);
        var preview = new StackPanel();
        preview.Children.Add(new TextBlock { Text = L.T("云端配置仅供参考，请核对后导入。"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        preview.Children.Add(_cloudEditor);
        _cloudPreviewPanel.Header = L.T("云端规则预览"); _cloudPreviewPanel.Content = preview;
        AutomationProperties.SetName(_cloudEditor, L.T("云端规则预览")); panel.Children.Add(_cloudPreviewPanel);
        RefreshCloudControls(); return panel;
    }
    private void RefreshCloudControls()
    {
        _cloudChoice.IsEnabled = !_busy && _cloudChoice.Items.Count > 0;
        _cloudRead.IsEnabled = !_busy && _cloudChoice.SelectedItem is ComboBoxItem { Tag: CloudWhitelistProfileDescriptor };
        bool full = _snapshot?.Profiles.Count >= WhitelistProfilesStore.MaximumProfiles;
        _cloudImport.IsEnabled = !_busy && _cloudPreview is not null && !full && _canEdit();
        _cloudName.IsEnabled = !_busy;
        _cloudLimit.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CancelCloudRequest()
    {
        _cloudGeneration++;
        _cloudRequest?.Cancel();
        _cloudChoice.Items.Clear(); _cloudPreview = null;
        _cloudPreviewPanel.Visibility = _cloudName.Visibility = Visibility.Collapsed;
        if (_ownsCloudService)
        {
            _cloudProfiles?.Dispose(); _cloudProfiles = null;
        }
    }
    private async Task LoadCloudProfilesAsync()
    {
        using var request = new CancellationTokenSource(); _cloudRequest = request;
        _cloudChoice.Items.Clear(); _cloudPreview = null;
        _cloudPreviewPanel.Visibility = _cloudName.Visibility = Visibility.Collapsed;
        _cloudProgress.Visibility = Visibility.Visible;
        _cloudSummary.Text = L.T("正在加载云端配置");
        try
        {
            var items = await (_cloudProfiles ??= new CloudWhitelistProfiles()).ListAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            _cloudChoice.Items.Clear(); _cloudPreview = null;
            foreach (var item in items) _cloudChoice.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
            _cloudChoice.SelectedIndex = items.Count > 0 ? 0 : -1;
            _cloudSummary.Text = items.Count == 0 ? L.T("未找到云端配置。") : L.T("选择云端配置");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) { _cloudSummary.Text = error.Message; throw; }
        finally { if (ReferenceEquals(_cloudRequest, request)) _cloudRequest = null; _cloudProgress.Visibility = Visibility.Collapsed; }
    }
    private async Task ReadCloudProfileAsync()
    {
        if (_cloudChoice.SelectedItem is not ComboBoxItem { Tag: CloudWhitelistProfileDescriptor descriptor }) return;
        using var request = new CancellationTokenSource(); _cloudRequest = request;
        _cloudPreview = null; _cloudPreviewPanel.Visibility = Visibility.Collapsed; _cloudName.Visibility = Visibility.Collapsed;
        _cloudProgress.Visibility = Visibility.Visible;
        _cloudSummary.Text = L.T("正在读取云端规则");
        try
        {
            var preview = await (_cloudProfiles ??= new CloudWhitelistProfiles()).ReadAsync(descriptor, request.Token);
            request.Token.ThrowIfCancellationRequested();
            _cloudPreview = preview; _cloudName.Text = descriptor.Name;
            _cloudEditor.Text = preview.Json; _cloudPreviewPanel.Visibility = Visibility.Visible; _cloudName.Visibility = Visibility.Visible;
            _cloudSummary.Text = descriptor.FileName + " | " + L.F($"规则预览 | {preview.Rules.Count} 条规则") + " | " + L.T("仅预览，尚未应用");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) { _cloudSummary.Text = error.Message; throw; }
        finally { if (ReferenceEquals(_cloudRequest, request)) _cloudRequest = null; _cloudProgress.Visibility = Visibility.Collapsed; }
    }
    private async Task ImportCloudProfileAsync()
    {
        RequireEdit();
        if (_cloudPreview is not { } preview) throw new InvalidOperationException(L.T("请先预览云端配置。"));
        if (_snapshot!.Profiles.Count >= WhitelistProfilesStore.MaximumProfiles) throw new InvalidDataException(L.T("最多只能保存 5 套白名单配置。"));
        var generation = _cloudGeneration;
        var originalName = _cloudName.Text.Trim(); var name = originalName;
        for (int suffix = 2; _snapshot.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            var ending = " (" + suffix + ")";
            name = originalName.Substring(0, Math.Min(originalName.Length, 80 - ending.Length)).TrimEnd() + ending;
        }
        if (!await CanDiscardAsync()) return;
        if (generation != _cloudGeneration || !ReferenceEquals(_cloudPreview, preview)) return;
        if (!await _confirm(L.T("导入云端配置？"), name + " | " + L.F($"{preview.Rules.Count} 条规则") + "\n" +
            L.T("将创建新配置并切换当前白名单；不会关闭任何程序。"))) return;
        if (generation != _cloudGeneration || !ReferenceEquals(_cloudPreview, preview)) return;
        RequireEdit();
        var next = await _mutate(() => _store.ImportProfile(name, preview.Rules, _snapshot.Revision, activate: true));
        Apply(next, next.ActiveId); _changed(next); Report(L.T("白名单配置已保存"), true);
    }
}
