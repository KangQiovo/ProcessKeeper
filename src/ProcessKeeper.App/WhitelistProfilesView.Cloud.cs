using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal sealed partial class WhitelistProfilesView
{
    private readonly Func<CloudWhitelistProfiles> _cloudFactory;
    private readonly Button _cloudLoad = new();
    private readonly Button _cloudImport = new();
    private readonly ComboBox _cloudChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _cloudPreviewText = new()
    {
        IsReadOnly = true, AcceptsReturn = true, Height = 200,
        TextWrapping = TextWrapping.NoWrap, FontSize = 12,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
    };
    private readonly TextBlock _cloudSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _cloudLimit = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _cloudProgress = new() { Width = 20, Height = 20 };
    private CloudWhitelistProfiles? _cloudService;
    private CancellationTokenSource? _cloudCancellation;
    private CloudWhitelistProfilePreview? _cloudPreview;
    private bool _cloudSelecting;
    private long _cloudGeneration;

    private Expander CreateCloudProfilesPanel()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = L.T("仅主动加载；导入计入五套配置。"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L.T("配置来源 | KangQiovo/ProcessKeeper/community/profiles"),
            FontSize = 12, TextWrapping = TextWrapping.Wrap });
        _cloudLoad.Content = L.T("从 GitHub 加载");
        _cloudLoad.Click += async (_, _) => await RunAsync(LoadCloudProfilesAsync);
        var loadRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        loadRow.Children.Add(_cloudLoad); loadRow.Children.Add(_cloudProgress); panel.Children.Add(loadRow);
        _cloudChoice.Header = L.T("选择云端配置");
        AutomationProperties.SetName(_cloudChoice, L.T("选择云端配置"));
        _cloudChoice.SelectionChanged += async (_, _) =>
        {
            if (_cloudSelecting || _busy) return;
            _cloudPreview = null; _cloudPreviewText.Text = ""; _cloudSummary.Text = "";
            UpdateCloudControls();
            if (_cloudChoice.SelectedItem is ComboBoxItem { Tag: CloudWhitelistProfileDescriptor descriptor })
                await RunAsync(() => PreviewCloudProfileAsync(descriptor));
        };
        panel.Children.Add(_cloudChoice); panel.Children.Add(_cloudSummary);
        _cloudPreviewText.Header = L.T("云端规则预览");
        ScrollViewer.SetHorizontalScrollBarVisibility(_cloudPreviewText, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_cloudPreviewText, ScrollBarVisibility.Auto);
        panel.Children.Add(_cloudPreviewText);
        panel.Children.Add(new TextBlock { Text = L.T("云端配置仅供参考，请核对后导入。"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        _cloudImport.Content = L.T("导入并应用");
        _cloudImport.Click += async (_, _) => await RunAsync(ImportCloudProfileAsync);
        panel.Children.Add(_cloudImport);
        _cloudLimit.Text = L.T("本地配置已达五套，请先删除未使用的配置。");
        panel.Children.Add(_cloudLimit); UpdateCloudControls();
        return new Expander { Header = L.T("云端白名单配置"), Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void UpdateCloudControls()
    {
        _cloudLoad.IsEnabled = !_busy;
        _cloudChoice.IsEnabled = !_busy && _cloudChoice.Items.Count > 0;
        var full = _snapshot?.Profiles.Count >= WhitelistProfilesStore.MaximumProfiles;
        _cloudImport.IsEnabled = !_busy && _cloudPreview is not null && _snapshot is not null && !full && _canEdit();
        _cloudLimit.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        _cloudProgress.IsActive = _busy && _cloudCancellation is not null;
        _cloudProgress.Visibility = _cloudProgress.IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseCloudProfiles()
    {
        ++_cloudGeneration;
        _cloudCancellation?.Cancel(); _cloudCancellation?.Dispose(); _cloudCancellation = null;
        _cloudService?.Dispose(); _cloudService = null; _cloudPreview = null;
        _cloudSelecting = true;
        try { _cloudChoice.Items.Clear(); }
        finally { _cloudSelecting = false; }
        _cloudPreviewText.Text = ""; _cloudSummary.Text = ""; UpdateCloudControls();
    }

    private async Task LoadCloudProfilesAsync()
    {
        CloseCloudProfiles();
        var generation = _cloudGeneration;
        _cloudService = _cloudFactory();
        using var cancellation = new CancellationTokenSource(); _cloudCancellation = cancellation;
        _cloudSummary.Text = L.T("正在加载云端配置"); UpdateCloudControls();
        try
        {
            var descriptors = await _cloudService.ListAsync(cancellation.Token);
            if (generation != _cloudGeneration) return;
            _cloudSelecting = true;
            try
            {
                foreach (var item in descriptors)
                    _cloudChoice.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
            }
            finally { _cloudSelecting = false; }
            _cloudSummary.Text = descriptors.Count == 0 ? L.T("未找到云端配置。") : L.T("先加载并选择云端配置。");
        }
        catch (OperationCanceledException) when (generation != _cloudGeneration) { }
        catch (Exception) when (generation != _cloudGeneration) { }
        finally { if (generation == _cloudGeneration) { _cloudCancellation = null; UpdateCloudControls(); } }
    }

    private async Task PreviewCloudProfileAsync(CloudWhitelistProfileDescriptor descriptor)
    {
        var service = _cloudService;
        if (service is null) return;
        var generation = _cloudGeneration;
        using var cancellation = new CancellationTokenSource(); _cloudCancellation = cancellation;
        _cloudSummary.Text = L.T("正在读取云端规则"); UpdateCloudControls();
        try
        {
            var preview = await service.ReadAsync(descriptor, cancellation.Token);
            if (generation != _cloudGeneration) return;
            _cloudPreview = preview; _cloudPreviewText.Text = preview.Json;
            _cloudSummary.Text = L.F($"规则预览 | {preview.Rules.Count} 条规则");
        }
        catch (OperationCanceledException) when (generation != _cloudGeneration) { }
        catch (Exception) when (generation != _cloudGeneration) { }
        finally { if (generation == _cloudGeneration) { _cloudCancellation = null; UpdateCloudControls(); } }
    }

    private async Task ImportCloudProfileAsync()
    {
        RequireEdit();
        var preview = _cloudPreview ?? throw new InvalidOperationException(L.T("先加载并选择云端配置。"));
        var snapshot = _snapshot!;
        if (snapshot.Profiles.Count >= WhitelistProfilesStore.MaximumProfiles)
            throw new InvalidOperationException(L.T("本地配置已达五套，请先删除未使用的配置。"));
        var generation = _cloudGeneration;
        if (!await CanDiscardAsync() || generation != _cloudGeneration || !ReferenceEquals(preview, _cloudPreview)) return;
        if (!await _confirm(L.T("导入云端配置？"), preview.Descriptor.Name + "\n" +
            L.F($"规则预览 | {preview.Rules.Count} 条规则") + "\n" +
            L.T("将创建新配置并切换当前白名单；不会关闭任何程序。"))) return;
        if (generation != _cloudGeneration || !ReferenceEquals(preview, _cloudPreview)) return;
        RequireEdit();
        var name = preview.Descriptor.Name;
        var suffix = 2;
        while (snapshot.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = preview.Descriptor.Name.Substring(0, Math.Min(70, preview.Descriptor.Name.Length)) + " (" + suffix++ + ")";
        var next = await _mutate(() => _store.ImportProfile(name, preview.Rules, snapshot.Revision, activate: true));
        Apply(next, next.ActiveId); _changed(next); Report(L.T("白名单配置已保存"), true);
    }
}
