using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class UninstallView : UserControl, IDisposable
{
    private readonly IUninstallBackend _backend;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string> _log;
    private readonly Func<bool> _canAct;
    private readonly CatalogUpdater? _catalog;
    private CatalogUpdateOffer? _catalogOffer;
    private readonly Button _catalogButton = new() { Content = L.T("规则库"), Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,0,8,0) };
    private readonly Button _catalogCheck = new() { Content = L.T("检查规则更新"), Padding = new Thickness(10,5,10,5) };
    private readonly Button _catalogApply = new() { Content = L.T("更新规则库"), IsEnabled = false, Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,8,0,0) };
    private readonly Button _catalogCancel = new() { Content = L.T("取消"), Visibility = Visibility.Collapsed, Padding = new Thickness(10,5,10,5), Margin = new Thickness(0,8,0,0) };
    private readonly TextBlock _catalogInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,0,0,10) };
    private readonly TextBlock _catalogFeedback = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0,0,0,10) };
    private Wpf.Ui.Appearance.ThemeChangedEvent? _catalogThemeChanged;
    private readonly LegacyIconCache _icons = new();
    private readonly SemaphoreSlim _iconSlots = new(4, 4);
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _operation;
    private readonly TextBox _search = new() { MinWidth = 0, ToolTip = L.T("搜索软件、发布者或卸载路径"), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ComboBox _filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly CheckBox _groupPlatforms = new() { Content = L.T("按游戏平台分组"), FontSize = 12, Margin = new Thickness(0, 0, 18, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _hideMicrosoft = new() { Content = L.T("隐藏 Microsoft 应用"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
    private bool _applyingPresentation;
    private readonly Wpf.Ui.Controls.ToggleSwitch _mode = new() { OffContent = L.T("简单"), OnContent = L.T("复杂"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _scan = new() { Content = L.T("重新扫描"), Padding = new Thickness(10, 5, 10, 5) };
    private readonly Button _cancel = new() { Content = L.T("取消扫描"), Visibility = Visibility.Collapsed, Padding = new Thickness(10, 5, 10, 5) };
    private readonly Button _normal = new() { Content = L.T("卸载"), IsEnabled = false, Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _quiet = new() { Content = L.T("注册的静默卸载"), IsEnabled = false, Padding = new Thickness(12, 5, 12, 5) };
    private readonly Button _batch = new Wpf.Ui.Controls.Button { Content = new TextBlock { Text = L.T("卸载所有建议卸载的应用"), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, Padding = new Thickness(12,5,12,5), Margin = new Thickness(0,8,0,0), IsEnabled = false };
    private readonly Button _coverage = new() { Content = L.T("扫描详情"), Padding = new Thickness(10, 5, 10, 5) };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0) };
    private readonly System.Collections.ObjectModel.ObservableCollection<Row> _rows = new();
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private UninstallSnapshot _snapshot = new();
    private bool _closed, _scanning, _loaded, _filterReady, _confirming, _batchRunning, _catalogBusy;
    private int _renderRevision;
    public bool IsBusy => _scanning || IsChanging || _confirming || _catalogBusy;
    public bool IsChanging { get; private set; }
    public event Action<bool>? ChangingChanged;
    public UninstallView(Func<string, string, Task<bool>> confirm, Action<string> log, Func<bool>? canAct = null, IUninstallBackend? backend = null, string? dataDirectory = null, Action<string>? openLocation = null, Action<string>? copyText = null)
    {
        _backend = backend ?? new UninstallWindowsBackend(); _confirm = confirm; _log = log; _canAct = canAct ?? (() => true);
        _list.ItemsSource = _rows;
        _openLocation = openLocation ?? OpenApplicationLocation; _copyText = copyText ?? Clipboard.SetText;
        _catalog = dataDirectory is null ? null : new CatalogUpdater(dataDirectory);
        var root = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = height });
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 10) }; toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.Children.Add(_search);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(_selectionToggle); actions.Children.Add(_scan); actions.Children.Add(_cancel); _selectionToggle.Margin = _scan.Margin = _cancel.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(actions, 1); toolbar.Children.Add(actions); root.Children.Add(toolbar);
        ConfigureFilter();
        var filters = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var presentation = new WrapPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center }; presentation.Children.Add(_groupPlatforms); presentation.Children.Add(_hideMicrosoft);
        _groupPlatforms.ToolTip = L.T("仅整理显示；卸载操作仍针对所选软件的具体版本。"); _hideMicrosoft.ToolTip = L.T("仅隐藏已核实的 Microsoft 应用；未知身份仍显示。");
        filters.Children.Add(presentation); Grid.SetColumn(_filter, 1); filters.Children.Add(_filter); Grid.SetColumn(_mode, 2); filters.Children.Add(_mode);
        filters.SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 850;
            filters.ColumnDefinitions[1].Width = compact ? GridLength.Auto : new GridLength(220);
            Grid.SetRow(_filter, compact ? 1 : 0); Grid.SetColumn(_filter, compact ? 0 : 1); Grid.SetRow(_mode, compact ? 1 : 0);
            Grid.SetColumnSpan(presentation, compact ? 3 : 1); Grid.SetColumnSpan(_filter, compact ? 2 : 1);
        };
        void PresentationChoice(object sender, RoutedEventArgs args) { if (!_applyingPresentation) { PresentationChanged?.Invoke(this, EventArgs.Empty); _ = RenderAsync(); } }
        _groupPlatforms.Click += PresentationChoice; _hideMicrosoft.Click += PresentationChoice;
        Grid.SetRow(filters, 1); root.Children.Add(filters);
        VirtualizingPanel.SetIsVirtualizing(_list, true); VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(_list, true); ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel)); _list.ItemsPanel = new ItemsPanelTemplate(panel);
        var row = new FrameworkElementFactory(typeof(DockPanel)); row.SetBinding(FrameworkElement.MarginProperty, new System.Windows.Data.Binding("Indent"));
        var selection = new FrameworkElementFactory(typeof(CheckBox)); selection.SetValue(DockPanel.DockProperty, Dock.Right); selection.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top); selection.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right); selection.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 3, 0, 0));
        selection.SetValue(FrameworkElement.WidthProperty, 20d); selection.SetValue(FrameworkElement.HeightProperty, 20d); selection.SetValue(FrameworkElement.MinWidthProperty, 0d); selection.SetValue(FrameworkElement.MinHeightProperty, 0d); selection.SetValue(Control.PaddingProperty, new Thickness(0)); selection.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Name"));
        selection.SetValue(System.Windows.Controls.Primitives.ToggleButton.IsThreeStateProperty, true);
        selection.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("SelectionState") { Mode = System.Windows.Data.BindingMode.OneWay });
        selection.AddHandler(Button.ClickEvent, new RoutedEventHandler(SelectionCheckboxClicked)); row.AppendChild(selection);
        var expand = new FrameworkElementFactory(typeof(Button)); expand.SetValue(FrameworkElement.WidthProperty, 24d); expand.SetValue(DockPanel.DockProperty, Dock.Left); expand.SetValue(Control.PaddingProperty, new Thickness(0)); expand.SetValue(Control.BackgroundProperty, Brushes.Transparent); expand.SetValue(Control.BorderThicknessProperty, new Thickness(0)); expand.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("ChevronVisibility"));
        expand.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, args) => { if (sender is FrameworkElement { DataContext: Row item }) ToggleGroup(item); args.Handled = true; }));
        var chevron = new FrameworkElementFactory(typeof(Wpf.Ui.Controls.SymbolIcon)); chevron.SetValue(Control.FontSizeProperty, 11d); chevron.SetBinding(Wpf.Ui.Controls.SymbolIcon.SymbolProperty, new System.Windows.Data.Binding("Chevron")); expand.AppendChild(chevron); row.AppendChild(expand);
        var icon = new FrameworkElementFactory(typeof(Image)); icon.SetValue(FrameworkElement.WidthProperty, 32d); icon.SetValue(FrameworkElement.HeightProperty, 32d); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0)); icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(IconLoaded)); row.AppendChild(icon);
        var stack = new FrameworkElementFactory(typeof(StackPanel)); var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name")); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); stack.AppendChild(name);
        var badge = new FrameworkElementFactory(typeof(Border)) { Name = "RecommendationBadge" };
        badge.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(196, 43, 28)));
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); badge.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
        badge.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); badge.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
        badge.SetValue(UIElement.IsHitTestVisibleProperty, false); badge.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("RecommendationVisibility"));
        var badgeText = new FrameworkElementFactory(typeof(TextBlock)); badgeText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RecommendationLabel"));
        badgeText.SetValue(TextBlock.ForegroundProperty, Brushes.White); badgeText.SetValue(TextBlock.FontSizeProperty, 12d); badgeText.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); badgeText.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        badge.AppendChild(badgeText); stack.AppendChild(badge);
        var summary = new FrameworkElementFactory(typeof(TextBlock)); summary.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Summary")); summary.SetValue(TextBlock.FontSizeProperty, 12d); summary.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); stack.AppendChild(summary); row.AppendChild(stack); _list.ItemTemplate = new DataTemplate { VisualTree = row };
        _list.SelectionChanged += UninstallSelectionChanged; _list.ContextMenu = new ContextMenu(); _list.ContextMenuOpening += RowContextOpening; Grid.SetRow(_list, 2); root.Children.Add(_list);
        _list.PreviewKeyDown += (_, args) =>
        {
            if (_list.SelectedItem is not Row { IsGroup: true } item) return;
            if (args.Key == System.Windows.Input.Key.Right && !item.Expanded || args.Key == System.Windows.Input.Key.Left && item.Expanded)
            { args.Handled = true; ToggleGroup(item); }
        };
        var detailPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        var detailsScroll = new ScrollViewer { Content = _details, MaxHeight = 165, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        detailPanel.Children.Add(detailsScroll);
        SizeChanged += (_, _) => detailsScroll.MaxHeight = Math.Max(40, Math.Min(165, ActualHeight * 0.16));
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; buttons.Children.Add(_normal); _quiet.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(_quiet); detailPanel.Children.Add(buttons); Grid.SetRow(detailPanel, 3); root.Children.Add(detailPanel);
        detailPanel.Children.Add(_batch);
        var footer = new DockPanel(); DockPanel.SetDock(_coverage, Dock.Right); footer.Children.Add(_coverage); DockPanel.SetDock(_catalogButton, Dock.Right); footer.Children.Add(_catalogButton); footer.Children.Add(_status); Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root;
        var catalogPanel = new StackPanel { Width = 400 }; catalogPanel.Children.Add(_catalogInfo); catalogPanel.Children.Add(_catalogFeedback); catalogPanel.Children.Add(_catalogCheck); catalogPanel.Children.Add(_catalogApply); catalogPanel.Children.Add(_catalogCancel);
        var catalogMenu = new ContextMenu { PlacementTarget = _catalogButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Custom };
        catalogMenu.CustomPopupPlacementCallback = (popup, target, offset) => new[] { new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal) };
        catalogMenu.Items.Add(new MenuItem { Header = catalogPanel, StaysOpenOnClick = true, Focusable = false });
        _catalogInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush"); _catalogFeedback.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _catalogButton.ContextMenu = catalogMenu; _catalogButton.Click += (_, _) => { Wpf.Ui.Appearance.ApplicationThemeManager.Apply(catalogMenu); catalogPanel.Width = Math.Max(200, Math.Min(400, ActualWidth - 40)); UpdateCatalogInfo(); catalogMenu.IsOpen = true; };
        _catalogThemeChanged = (_, _) => { if (!_closed && catalogMenu.IsOpen) Wpf.Ui.Appearance.ApplicationThemeManager.Apply(catalogMenu); if (!_closed && _rowMenu?.IsOpen == true) Wpf.Ui.Appearance.ApplicationThemeManager.Apply(_rowMenu); };
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += _catalogThemeChanged;
        _catalogCheck.Click += async (_, _) => await UpdateCatalogAsync(false); _catalogApply.Click += async (_, _) => await UpdateCatalogAsync(true); _catalogCancel.Click += (_, _) => _operation?.Cancel();
        _scan.Click += async (_, _) => await ScanAsync(); _cancel.Click += (_, _) => _operation?.Cancel();
        _selectionToggle.Click += (_, _) => ToggleSelection();
        _normal.Click += async (_, _) => await UninstallAsync(UninstallMode.Normal); _quiet.Click += async (_, _) => await UninstallAsync(UninstallMode.RegisteredQuiet);
        _batch.Click += async (_, _) => await UninstallAllAsync();
        _search.TextChanged += (_, _) => { _searchCollapsedGroups.Clear(); _searchDelay.Stop(); _searchDelay.Start(); }; _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); _ = RenderAsync(); };
        _filter.SelectionChanged += (_, _) => { if (_filterReady) _ = RenderAsync(); }; _filterReady = true;
        _mode.Checked += (_, _) => { ConfigureFilter(); _ = RenderAsync(); }; _mode.Unchecked += (_, _) => { ConfigureFilter(); _ = RenderAsync(); };
        _coverage.Click += async (_, _) => await _confirm(L.T("扫描详情"), L.T("读取当前用户和系统的 32/64 位卸载注册项，包括隐藏项目。未注册软件、商店应用和残留文件不保证包含。") + "\n\n" + UninstallRecommendations.Coverage + "\n\n" + string.Join("\n", _snapshot.Warnings));
        Loaded += async (_, _) => { if (!_loaded) { await LoadCatalogAsync(); await ScanAsync(); } }; UpdateCatalogInfo(); SetBusy();
    }
    public async Task ScanAsync()
    {
        if (_closed || IsBusy) return; _scanning = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); SetBusy();
        try { var result = await Task.Run(() => _backend.Scan(_operation.Token), _operation.Token); if (_closed) return; _snapshot = result; _loaded = true; InventoryChanged?.Invoke(this, EventArgs.Empty); await RenderAsync(); }
        catch (OperationCanceledException) { if (!_closed) _status.Text = L.T("扫描已取消，保留上次结果。"); }
        catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
        finally { _scanning = false; _operation?.Dispose(); _operation = null; if (!_closed) SetBusy(); }
    }
    private async Task RenderAsync()
    {
        var revision = ++_renderRevision; var query = _search.Text.Trim(); int filter = _filter.SelectedIndex; var inventory = _snapshot; bool advanced = _mode.IsChecked == true;
        var cached = ReferenceEquals(_groupSnapshot, inventory) ? _groupCache : null;
        var expanded = new HashSet<string>(_expandedGroups, StringComparer.Ordinal); var collapsed = new HashSet<string>(_searchCollapsedGroups, StringComparer.Ordinal);
        var display = _displayCatalog; var groupPlatforms = GroupGamePlatforms; var hideMicrosoft = HideMicrosoftApps;
        var platforms = new HashSet<string>(_collapsedPlatforms, StringComparer.Ordinal);
        var result = await Task.Run(() => BuildDisplayRows(cached ?? UninstallDisplay.Group(inventory.Entries), query, advanced, filter, expanded, collapsed, display, groupPlatforms, hideMicrosoft, platforms));
        if (_closed || revision != _renderRevision) return;
        _groupSnapshot = inventory; _groupCache = result.Groups;
        foreach (var row in result.Rows) { row.SetWhitelistProtected(row.IsGroup && row.Group is not null ? row.Group.Entries.All(IsWhitelistProtected) : !row.IsGroup && IsWhitelistProtected(row.Entry)); row.Notify(); }
        var changedIcons = new List<Row>();
        _restoringSelection = true;
        try { StableRows.Apply(_rows, result.Rows, row => row.Key, (row, next) =>
        {
            if (!row.IconPath.Equals(next.IconPath, StringComparison.OrdinalIgnoreCase)) changedIcons.Add(row);
            row.Apply(next);
        }); } finally { _restoringSelection = false; }
        ConfigureTreeSelection(); SyncTreeSelection();
        foreach (var row in changedIcons)
            if (_list.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container && FindRowIcon(container) is Image icon)
                _ = SetIconAsync(icon);
        _status.Text = L.F($"{result.EntryCount} 个卸载项目") + (inventory.Truncated ? " | " + L.T("结果已截断") : ""); ShowSelection();
    }
    private void ShowSelection()
    {
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
        _quiet.ToolTip = L.T("仅使用软件已注册的静默卸载命令，不强删文件、注册表或驱动。");
    }
    private void SetBusy()
    {
        _scan.IsEnabled = !IsBusy; _search.IsEnabled = _filter.IsEnabled = _mode.IsEnabled = _list.IsEnabled = _groupPlatforms.IsEnabled = _hideMicrosoft.IsEnabled = !IsChanging && !_confirming;
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
    private async void IconLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image icon) return;
        icon.DataContextChanged -= IconContextChanged; icon.DataContextChanged += IconContextChanged; await SetIconAsync(icon);
    }
    private async void IconContextChanged(object sender, DependencyPropertyChangedEventArgs args) { if (sender is Image icon) await SetIconAsync(icon); }
    private async Task SetIconAsync(Image icon)
    {
        if (_closed || icon.DataContext is not Row row) return;
        var path = row.IconPath; var iconKey = row.Key + "|" + path;
        if (Equals(icon.Tag, iconKey) && icon.Source is not null) return;
        icon.Source = null; icon.Tag = iconKey;
        bool entered = false;
        try { await _iconSlots.WaitAsync(_life.Token); entered = true; if (_closed || !ReferenceEquals(icon.DataContext, row) || !Equals(icon.Tag, iconKey)) return; var source = await _icons.Get(path, _life.Token); if (!_closed && ReferenceEquals(icon.DataContext, row) && Equals(icon.Tag, iconKey) && row.IconPath.Equals(path, StringComparison.OrdinalIgnoreCase)) icon.Source = source; }
        catch (Exception) { }
        finally { if (entered) _iconSlots.Release(); }
    }
    private static Image? FindRowIcon(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is Image image) return image;
            if (FindRowIcon(child) is { } nested) return nested;
        }
        return null;
    }
    public void Dispose() { if (_closed) return; _closed = true; if (_rowMenu is not null) _rowMenu.IsOpen = false; _life.Cancel(); _operation?.Cancel(); _searchDelay.Stop(); if (_catalogThemeChanged is not null) Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= _catalogThemeChanged; _renderRevision++; }
}
