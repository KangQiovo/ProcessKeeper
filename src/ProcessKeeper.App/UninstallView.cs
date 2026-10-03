using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

/// <summary>Registered uninstallers only. Inventory and launch boundaries can be replaced by a native UI fixture.</summary>
public sealed partial class UninstallView : UserControl, IDisposable
{
    private readonly IUninstallBackend _backend;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string> _log;
    private readonly Func<bool> _canAct;
    private readonly CatalogUpdater? _catalog;
    private CatalogUpdateOffer? _catalogOffer;
    private readonly Button _catalogButton = new() { Content = L.T("规则库") };
    private readonly Button _catalogCheck = new() { Content = L.T("检查规则更新") };
    private readonly Button _catalogApply = new() { Content = L.T("更新规则库"), IsEnabled = false };
    private readonly Button _catalogCancel = new() { Content = L.T("取消"), Visibility = Visibility.Collapsed };
    private readonly TextBlock _catalogInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _catalogFeedback = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ProcessIconService _icons = new();
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _operation;
    private readonly AutoSuggestBox _search = new() { PlaceholderText = L.T("搜索软件、发布者或卸载路径"), QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly ComboBox _filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly PresentationOptions _presentationOptions = new();
    private readonly ToggleSwitch _mode = new() { OffContent = L.T("简单"), OnContent = L.T("复杂"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
    private readonly Button _scan = new() { Content = L.T("重新扫描") };
    private readonly Button _cancel = new() { Content = L.T("取消扫描"), Visibility = Visibility.Collapsed };
    private readonly Button _normal = new() { Content = L.T("卸载"), IsEnabled = false };
    private readonly Button _quiet = new() { Content = L.T("注册的静默卸载"), IsEnabled = false };
    private readonly Button _batch = new() { Content = new TextBlock { Text = L.T("卸载所有建议卸载的应用"), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255,196,43,28)), Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White), IsEnabled = false };
    private readonly Button _coverage = new() { Content = L.T("扫描详情") };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly System.Collections.ObjectModel.ObservableCollection<Row> _rows = new();
    private bool _updatingRows;
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private UninstallSnapshot _snapshot = new();
    private bool _closed, _scanning, _loaded, _filterReady, _confirming, _batchRunning, _catalogBusy;
    private int _renderRevision;
    private readonly HashSet<Button> _expansionButtons = new();
    public bool IsBusy => _scanning || IsChanging || _confirming || _catalogBusy;
    public bool IsChanging { get; private set; }
    public event Action<bool>? ChangingChanged;

    public UninstallView(Func<string, string, Task<bool>> confirm, Action<string> log, Func<bool>? canAct = null, IUninstallBackend? backend = null, string? dataDirectory = null, Action<string>? openLocation = null, Action<string>? copyText = null)
    {
        _backend = backend ?? new UninstallWindowsBackend(); _confirm = confirm; _log = log; _canAct = canAct ?? (() => true);
        _list.ItemsSource = _rows;
        NativeSelectionTree.Attach(_list, row => ((Row)row).Key, SelectionNodes, ShowSelection);
        _openLocation = openLocation ?? OpenApplicationLocation; _copyText = copyText ?? CopyMenuText;
        _catalog = dataDirectory is null ? null : new CatalogUpdater(dataDirectory);
        var root = new Grid { RowSpacing = 10 };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        var toolbar = new Grid { ColumnSpacing = 8 }; toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_search); var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; actions.Children.Add(_selectionToggle); actions.Children.Add(_scan); actions.Children.Add(_cancel); Grid.SetColumn(actions, 1); toolbar.Children.Add(actions); root.Children.Add(toolbar);
        ConfigureFilter();
        var filters = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters.Children.Add(_presentationOptions); Grid.SetColumn(_filter, 1); filters.Children.Add(_filter); Grid.SetColumn(_mode, 2); filters.Children.Add(_mode);
        filters.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 850;
            filters.ColumnDefinitions[1].Width = compact ? GridLength.Auto : new GridLength(220);
            Grid.SetRow(_filter, compact ? 1 : 0); Grid.SetColumn(_filter, compact ? 0 : 1);
            Grid.SetRow(_mode, compact ? 1 : 0); Grid.SetColumn(_mode, 2);
            Grid.SetColumnSpan(_presentationOptions, compact ? 3 : 1); Grid.SetColumnSpan(_filter, compact ? 2 : 1);
        };
        _presentationOptions.Changed += (_, _) => { PresentationChanged?.Invoke(this, EventArgs.Empty); _ = RenderAsync(); };
        Grid.SetRow(filters, 1); root.Children.Add(filters);
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Grid ColumnSpacing="8" Margin="{Binding Indent}"><Grid.ColumnDefinitions><ColumnDefinition Width="24"/><ColumnDefinition Width="32"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <Button x:Name="ExpandGroup" Visibility="{Binding ChevronVisibility}" Width="24" MinWidth="0" Padding="0" Background="Transparent" BorderThickness="0"><FontIcon Glyph="{Binding Chevron}" FontSize="11"/></Button>
                <Image Grid.Column="1" x:Name="AppIcon" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="32" Height="32"/>
                <StackPanel Grid.Column="2" Spacing="4">
                  <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  <Border Name="RecommendationBadge" Background="#C42B1C" CornerRadius="4" Padding="8,2" HorizontalAlignment="Left" Visibility="{Binding RecommendationVisibility}" IsHitTestVisible="False">
                    <TextBlock Text="{Binding RecommendationLabel}" Foreground="White" FontSize="12" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  </Border>
                  <TextBlock Text="{Binding Summary}" FontSize="12" TextTrimming="CharacterEllipsis"/>
                </StackPanel>
                <CheckBox x:Name="RowSelection" Grid.Column="3" HorizontalAlignment="Right" VerticalAlignment="Top" MinWidth="0" FontSize="12"/>
              </Grid>
            </DataTemplate>
            """);
        var style = new Style(typeof(ListViewItem)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); _list.ItemContainerStyle = style;
        _list.SelectionChanged += (_, _) => ShowSelection(); _list.ContainerContentChanging += ContainerChanging; _list.ContextRequested += RowContextRequested;
        _list.KeyDown += (_, args) =>
        {
            if (_list.SelectedItem is not Row { IsGroup: true } row) return;
            if (args.Key == Windows.System.VirtualKey.Right && !row.Expanded || args.Key == Windows.System.VirtualKey.Left && row.Expanded)
            { args.Handled = true; ToggleGroup(row); }
        };
        Grid.SetRow(_list, 2); root.Children.Add(_list);
        var detailPanel = new StackPanel { Spacing = 8 };
        var detailsScroll = new ScrollViewer { Content = _details, MaxHeight = 165, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        detailPanel.Children.Add(detailsScroll);
        SizeChanged += (_, _) => detailsScroll.MaxHeight = Math.Max(40, Math.Min(165, ActualHeight * 0.16));
        var launchActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; launchActions.Children.Add(_normal); launchActions.Children.Add(_quiet); detailPanel.Children.Add(launchActions);
        detailPanel.Children.Add(_batch);
        Grid.SetRow(detailPanel, 3); root.Children.Add(detailPanel);
        var footer = new Grid { ColumnSpacing = 8 }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.Children.Add(_status); Grid.SetColumn(_catalogButton, 1); footer.Children.Add(_catalogButton); Grid.SetColumn(_coverage, 2); footer.Children.Add(_coverage); Grid.SetRow(footer, 4); root.Children.Add(footer);
        var catalogPanel = new StackPanel { Spacing = 10, Width = 400 }; catalogPanel.Children.Add(_catalogInfo); catalogPanel.Children.Add(_catalogFeedback);
        var catalogActions = new StackPanel { Spacing = 8 }; catalogActions.Children.Add(_catalogCheck); catalogActions.Children.Add(_catalogApply); catalogActions.Children.Add(_catalogCancel); catalogPanel.Children.Add(catalogActions);
        var catalogFlyout = new Flyout { Content = catalogPanel };
        void CatalogTheme()
        {
            catalogPanel.RequestedTheme = ActualTheme;
            var presenter = new Style(typeof(FlyoutPresenter)); presenter.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme)); catalogFlyout.FlyoutPresenterStyle = presenter;
        }
        catalogFlyout.Opening += (_, _) => { CatalogTheme(); catalogPanel.Width = Math.Max(200, Math.Min(400, ActualWidth - 40)); UpdateCatalogInfo(); };
        ActualThemeChanged += (_, _) => { CatalogTheme(); if (_rowMenu is not null) ApplyRowMenuTheme(_rowMenu); }; _catalogButton.Flyout = catalogFlyout;
        _catalogCheck.Click += async (_, _) => await UpdateCatalogAsync(false); _catalogApply.Click += async (_, _) => await UpdateCatalogAsync(true); _catalogCancel.Click += (_, _) => _operation?.Cancel();
        Content = root;
        _scan.Click += async (_, _) => await ScanAsync(); _cancel.Click += (_, _) => _operation?.Cancel();
        _selectionToggle.Click += (_, _) => ToggleSelection();
        _normal.Click += async (_, _) => await UninstallAsync(UninstallMode.Normal); _quiet.Click += async (_, _) => await UninstallAsync(UninstallMode.RegisteredQuiet);
        _batch.Click += async (_, _) => await UninstallAllAsync();
        _search.TextChanged += (_, _) => { _searchCollapsedGroups.Clear(); _searchDelay.Stop(); _searchDelay.Start(); }; _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); _ = RenderAsync(); };
        _filter.SelectionChanged += (_, _) => { if (_filterReady) _ = RenderAsync(); }; _filterReady = true;
        _mode.Toggled += (_, _) => { ConfigureFilter(); _ = RenderAsync(); };
        _coverage.Click += async (_, _) => await _confirm(L.T("扫描详情"), L.T("读取当前用户和系统的 32/64 位卸载注册项，包括隐藏项目。未注册软件、商店应用和残留文件不保证包含。") + "\n\n" +
            UninstallRecommendations.Coverage + "\n\n" + string.Join("\n", _snapshot.Warnings));
        Loaded += async (_, _) => { if (!_loaded) { await LoadCatalogAsync(); await ScanAsync(); } };
        UpdateCatalogInfo(); SetBusy();
    }
    public async Task ScanAsync()
    {
        if (_closed || IsBusy) return;
        _scanning = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); SetBusy();
        try { var result = await Task.Run(() => _backend.Scan(_operation.Token), _operation.Token); if (_closed) return; _snapshot = result; _loaded = true; InventoryChanged?.Invoke(this, EventArgs.Empty); await RenderAsync(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L.T("扫描已取消，保留上次结果。"); }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { _scanning = false; _operation?.Dispose(); _operation = null; if (!_closed) SetBusy(); }
    }
    private async Task RenderAsync()
    {
        var revision = ++_renderRevision; var query = _search.Text.Trim(); int filter = _filter.SelectedIndex; var inventory = _snapshot; bool advanced = _mode.IsOn;
        var cached = ReferenceEquals(_groupSnapshot, inventory) ? _groupCache : null;
        var expanded = new HashSet<string>(_expandedGroups, StringComparer.Ordinal); var collapsed = new HashSet<string>(_searchCollapsedGroups, StringComparer.Ordinal);
        var display = _displayCatalog; var groupPlatforms = GroupGamePlatforms; var hideMicrosoft = HideMicrosoftApps;
        var platforms = new HashSet<string>(_collapsedPlatforms, StringComparer.Ordinal);
        var result = await Task.Run(() => BuildDisplayRows(cached ?? UninstallDisplay.Group(inventory.Entries), query, advanced, filter, expanded, collapsed, display, groupPlatforms, hideMicrosoft, platforms));
        if (_closed || revision != _renderRevision) return;
        _groupSnapshot = inventory; _groupCache = result.Groups;
        foreach (var row in result.Rows) { row.SetWhitelistProtected(row.IsGroup && row.Group is not null ? row.Group.Entries.All(IsWhitelistProtected) : !row.IsGroup && IsWhitelistProtected(row.Entry)); row.Notify(); }
        var selected = _list.SelectedItems.OfType<Row>().Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        _updatingRows = true;
        var changedIcons = new List<Row>();
        try
        {
            StableRows.Apply(_rows, result.Rows, row => row.Key, (row, next) =>
            {
                if (!row.IconPath.Equals(next.IconPath, StringComparison.OrdinalIgnoreCase)) changedIcons.Add(row);
                row.Apply(next);
            });
            NativeListSelection.Restore(_list, _rows, selected, row => row.Key);
        }
        finally { _updatingRows = false; }
        NativeSelectionTree.For(_list)?.Invalidate();
        foreach (var row in changedIcons)
            if (_list.ContainerFromItem(row) is ListViewItem { ContentTemplateRoot: FrameworkElement root } && root.FindName("AppIcon") is Image icon)
                _ = LoadRowIconAsync(icon, row);
        _status.Text = L.F($"{result.EntryCount} 个卸载项目") + (inventory.Truncated ? " | " + L.T("结果已截断") : ""); ShowSelection();
    }
    private void ShowSelection()
    {
        if (_updatingRows || NativeSelectionTree.For(_list)?.IsApplying == true) return;
        var entry = SelectedEntry;
        _details.Text = entry is null ? (_list.SelectedItem is Row { IsPlatform: true } ? L.T("仅整理显示；卸载操作仍针对所选软件的具体版本。") : _list.SelectedItem is Row { IsGroup: true } ? L.T("展开查看此软件的所有版本与登记。") : L.T("选择软件后查看卸载入口。")) : entry.Name + "\n" + entry.Registration + "\n" + entry.Command?.Executable + "\n" + entry.Command?.Arguments +
            (entry.ReadOnlyReason.Length > 0 ? "\n" + entry.ReadOnlyReason : "") + (entry.IsRecommended ? "\n" + UninstallRecommendations.Describe(entry) : "");
        var selected = SelectedEntries().Where(item => !IsWhitelistProtected(item)).ToArray();
        _normal.IsEnabled = !IsBusy && selected.Any(item => item.CanUninstall); _quiet.IsEnabled = !IsBusy && selected.Length == 1 && selected[0].CanQuietUninstall;
        _normal.Content = L.T("卸载") + (selected.Length > 1 ? " (" + selected.Length + ")" : "");
        _batch.IsEnabled = !IsBusy && _snapshot.Entries.Any(item => UninstallBatch.IsEligible(item) && !IsWhitelistProtected(item));
        var selectable = _rows.ToArray();
        _selectionToggle.Content = L.T(selectable.Length > 0 && selectable.All(row => _list.SelectedItems.Contains(row)) ? "全不选" : "全选");
        _selectionToggle.IsEnabled = !IsBusy && selectable.Length > 0;
        _catalogCheck.IsEnabled = _catalog is not null && !IsBusy; _catalogApply.IsEnabled = _catalogOffer is not null && !IsBusy;
        _catalogCancel.Visibility = _catalogBusy ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_quiet, L.T("仅使用软件已注册的静默卸载命令，不强删文件、注册表或驱动。"));
    }
    private void SetBusy()
    {
        _scan.IsEnabled = !IsBusy; _search.IsEnabled = _filter.IsEnabled = _mode.IsEnabled = _list.IsEnabled = _presentationOptions.IsEnabled = !IsChanging && !_confirming;
        _cancel.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed; _cancel.Content = L.T(_batchRunning ? "停止批量卸载" : IsChanging ? "停止等待" : "取消扫描"); ShowSelection();
    }
    private async Task UninstallAllAsync()
    {
        if (_closed || IsBusy || !_canAct()) return;
        _confirming = _batchRunning = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); SetBusy();
        try
        {
            var result = await new UninstallBatch(_backend).RunAsync(_snapshot, _confirm, () => !_closed && _canAct(), () =>
            {
                _confirming = false; IsChanging = true; ChangingChanged?.Invoke(true); SetBusy();
                _status.Text = L.T("正在逐个卸载，请完成当前软件的卸载页面。");
            }, _operation.Token, entry => !IsWhitelistProtected(entry));
            if (!_closed) { _status.Text = result.Summary; _details.Text = result.Details; _log(L.T("批量卸载") + " | " + result.Details); }
        }
        catch (Exception ex) { if (!_closed) { _status.Text = ex.Message; _log(ex.Message); } }
        finally
        {
            var changing = IsChanging; IsChanging = _confirming = _batchRunning = false; _operation?.Dispose(); _operation = null;
            if (changing) ChangingChanged?.Invoke(false);
            if (!_closed) { var details = _details.Text; SetBusy(); _details.Text = details; }
        }
    }
    private void UpdateCatalogInfo()
    {
        var metadata = _catalog?.Current ?? UninstallRecommendations.Metadata;
        _catalogInfo.Text = L.F($"建议规则库 {metadata.Version} | {metadata.CheckedOn} | {metadata.RuleCount} 条") + "\n" +
            L.T(metadata.BuiltIn ? "当前使用内置规则。" : "当前使用已验证签名的更新规则。") + "\nGitHub | " + CatalogUpdater.Repository + "\n" + L.T("只更新建议标签，不是病毒查杀库；不会上传软件清单。");
    }
    private async Task LoadCatalogAsync()
    {
        if (_catalog is null) return; _catalogBusy = true; SetBusy();
        try { await _catalog.LoadAsync(_life.Token); }
        catch (Exception ex) { if (!_closed) _catalogFeedback.Text = ex.Message; }
        finally { _catalogBusy = false; if (!_closed) { UpdateCatalogInfo(); SetBusy(); } }
    }
    private async Task UpdateCatalogAsync(bool apply)
    {
        if (_closed || IsBusy || _catalog is null || !_canAct()) return;
        _catalogBusy = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); SetBusy();
        try
        {
            if (!apply)
            {
                _catalogOffer = null; _catalogFeedback.Text = L.T("正在检查官方规则库…");
                _catalogOffer = await _catalog.CheckAsync(_operation.Token);
                if (!_closed) _catalogFeedback.Text = _catalogOffer is null ? L.T("官方提供的规则版本与当前一致。") : L.F($"可用规则版本：{_catalogOffer.Metadata.Version} | {_catalogOffer.Metadata.CheckedOn}。确认更新后生效。");
            }
            else if (_catalogOffer is not null)
            {
                var offer = _catalogOffer;
                _confirming = true;
                bool accepted = await _confirm(L.T("更新建议规则库？"), L.F($"新规则版本：{offer.Metadata.Version} | {offer.Metadata.RuleCount} 条") + "\nGitHub | " + CatalogUpdater.Repository + "\n" + L.T("更新将重新标记当前列表中的建议卸载项，不会卸载任何软件。"));
                _confirming = false;
                if (!accepted || _closed || _operation.IsCancellationRequested || !_canAct()) return;
                await _catalog.ApplyAsync(offer, _operation.Token); _catalogOffer = null;
                _snapshot = _snapshot with { Entries = await Task.Run(() => _snapshot.Entries.Select(e => { var match = UninstallRecommendations.Match(e.Name); return e with { Recommendation = match, IsRecommended = match is not null }; }).ToArray()) };
                if (!_closed) { await RenderAsync(); _catalogFeedback.Text = L.T("规则库已更新，建议标签已刷新。"); _log(_catalogFeedback.Text + " | " + _catalog.Current.Version); }
            }
        }
        catch (OperationCanceledException) { if (!_closed) _catalogFeedback.Text = L.T(_operation.IsCancellationRequested ? "规则更新已取消。" : "规则更新网络超时，当前规则保持不变。"); }
        catch (Exception ex) { if (!_closed) _catalogFeedback.Text = L.T("规则更新失败：") + ex.Message; }
        finally { _catalogBusy = _confirming = false; _operation?.Dispose(); _operation = null; if (!_closed) { UpdateCatalogInfo(); SetBusy(); } }
    }
    private async Task UninstallAsync(UninstallMode mode)
    {
        var selected = SelectedEntries().Where(item => !IsWhitelistProtected(item) && item.CanUninstall).ToArray();
        if (selected.Length > 1 && mode == UninstallMode.Normal) { await UninstallSelectedAsync(selected); return; }
        var entry = selected.Length == 1 ? selected[0] : null; if (_closed || IsBusy || !_canAct() || entry is null) return;
        _confirming = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); SetBusy();
        try
        {
            var manager = new UninstallManager(_backend); var review = await manager.ReviewAsync(entry, mode, _operation.Token);
            if (_closed || _operation.IsCancellationRequested || !await _confirm(L.T("确认卸载此软件？"), UninstallPresentation.Confirmation(entry, mode) + "\n\n" + review.SignatureInformation)) return;
            if (mode == UninstallMode.RegisteredQuiet && !await _confirm(L.T("确认注册的静默卸载？"), L.T("静默卸载可能直接移除软件且无法撤销。确定继续？"))) return;
            if (_closed || _operation.IsCancellationRequested || !_canAct() || IsWhitelistProtected(entry)) return;
            _confirming = false; IsChanging = true; ChangingChanged?.Invoke(true); SetBusy();
            var result = await manager.RunAsync(review.Entry, mode, _operation.Token, item => !IsWhitelistProtected(item));
            if (!_closed) { _status.Text = result.Message; _log(entry.Name + " | " + result.Message); }
        }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L.T("卸载操作已取消。"); }
        catch (Exception ex) { if (!_closed) { _status.Text = ex.Message; _log(entry.Name + " | " + ex.Message); } }
        finally { var changing = IsChanging; IsChanging = _confirming = false; _operation?.Dispose(); _operation = null; if (changing) ChangingChanged?.Invoke(false); if (!_closed) SetBusy(); }
    }
    private async void ContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not Row row || args.ItemContainer.ContentTemplateRoot is not FrameworkElement root || root.FindName("AppIcon") is not Image icon) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, row.Name + " | " + row.Summary + (row.IsGroup ? " | " + L.T(row.Expanded ? "折叠" : "展开") : ""));
        if (root.FindName("RowSelection") is CheckBox selection)
        {
            selection.Content = L.T("选择");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(selection, L.T("选择") + " | " + row.Name);
            RowSelectionCheckBox.SetEnabled(selection, true);
        }
        if (root.FindName("ExpandGroup") is Button expand && _expansionButtons.Add(expand))
            expand.Click += (sender, _) => { if (sender is FrameworkElement { DataContext: Row group }) ToggleGroup(group); };
        if (root.FindName("ExpandGroup") is Button groupButton)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(groupButton, L.T(row.Expanded ? "折叠" : "展开") + " | " + row.Name);
        await LoadRowIconAsync(icon, row);
    }
    private async Task LoadRowIconAsync(Image icon, Row row)
    {
        var path = row.IconPath; var iconKey = row.Key + "|" + path;
        if (Equals(icon.Tag, iconKey) && icon.Source is not null) return;
        icon.Source = null; icon.Tag = iconKey;
        try { var source = await _icons.GetAsync(path); if (!_closed && Equals(icon.Tag, iconKey) && row.IconPath.Equals(path, StringComparison.OrdinalIgnoreCase)) icon.Source = source; }
        catch (Exception) { } // Extraction failure never blocks uninstall selection; only realized rows request icons.
    }
    public void Dispose() { if (_closed) return; _closed = true; _rowMenu?.Hide(); _life.Cancel(); _operation?.Cancel(); _searchDelay.Stop(); _icons.Dispose(); _renderRevision++; }
}
