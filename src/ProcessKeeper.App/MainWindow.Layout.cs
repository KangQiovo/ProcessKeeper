using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private bool _layoutReady;
    private bool? _overlayInspector;
    private bool? _compactToolbar;
    private bool _inspectorUpdateQueued;

    private void AuthorLinksSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < 360;
        var links = new[] { AuthorGithubButton, AuthorCoolapkButton, AuthorBilibiliButton };
        for (var index = 0; index < links.Length; index++)
        {
            Grid.SetColumn(links[index], compact ? 0 : index);
            Grid.SetRow(links[index], compact ? index : 0);
            links[index].HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private void InstalledSearchBarChanged(object sender, SizeChangedEventArgs args)
    {
        if (InstalledDriveFilter is null || InstalledSearch is null) return;
        var compact = args.NewSize.Width < 440;
        InstalledDriveColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumnSpan(InstalledSearch, compact ? 2 : 1);
        Grid.SetColumn(InstalledDriveFilter, compact ? 0 : 1);
        Grid.SetRow(InstalledDriveFilter, compact ? 1 : 0);
    }

    private void InitializeResponsiveLayout(object sender, RoutedEventArgs args)
    {
        if (!_layoutReady)
        {
            _layoutReady = true;
            AppsList.SelectionChanged += InspectorSelectionChanged;
            Navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateNavigationFooter());
        }
        ApplyWindowLayout();
        UpdateInspectorAvailability();
    }

    private void WindowLayoutChanged(object sender, SizeChangedEventArgs args) => ApplyWindowLayout();
    private void ApplicationAreaSizeChanged(object sender, SizeChangedEventArgs args) => ApplyApplicationLayout();

    private void ApplyWindowLayout()
    {
        if (!_ready || !_layoutReady || _closed) return;
        var shortWindow = Root.ActualHeight < 660;
        PageContent.Padding = shortWindow ? new Thickness(16, 8, 16, 12) : new Thickness(24, 8, 24, 20);
        PageContent.RowSpacing = shortWindow ? 7 : 12;
        PageHeading.Spacing = shortWindow ? 3 : 6;
        PageTitle.FontSize = shortWindow ? 24 : 28;
        PageSubtitle.FontSize = shortWindow ? 12 : 14;
        AppsPage.RowSpacing = shortWindow ? 6 : 14;
        InstalledPage.RowSpacing = shortWindow ? 6 : 10;
        RunningListHint.Visibility = InstalledListHint.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        RefreshStatus.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        InstalledStatus.MaxLines = shortWindow ? 1 : 0;
        InstalledStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        UpdateNavigationFooter();
        ApplyApplicationLayout();
    }

    private void UpdateNavigationFooter()
    {
        PreviewBuildText.Visibility = BuildInfo.IsPreviewBuild ? Visibility.Visible : Visibility.Collapsed;
        NavigationFooter.Visibility = Navigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyApplicationLayout()
    {
        if (!_ready || !_layoutReady || _closed || InspectorSplit.ActualWidth <= 0) return;
        var overlay = InspectorSplit.ActualWidth < 760 || Root.ActualHeight < 660;
        if (_overlayInspector != overlay)
        {
            _overlayInspector = overlay;
            InspectorSplit.DisplayMode = overlay ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            // Only change open state when crossing the layout breakpoint. Ordinary
            // refreshes and size events must not dismiss a pane opened by the user.
            InspectorSplit.IsPaneOpen = !overlay;
            InspectorToggleButton.Visibility = InspectorCloseButton.Visibility = overlay ? Visibility.Visible : Visibility.Collapsed;
            ApplicationListArea.Margin = new Thickness(0, 0, overlay ? 0 : 16, 0);
            DetailPanel.Padding = overlay ? new Thickness(16, 12, 16, 12) : new Thickness(12, 8, 0, 0);
            if (overlay) InspectorSplit.ClearValue(SplitView.PaneBackgroundProperty);
            else InspectorSplit.PaneBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        InspectorSplit.OpenPaneLength = overlay
            ? Math.Min(400, Math.Max(0, InspectorSplit.ActualWidth))
            : Math.Clamp(InspectorSplit.ActualWidth * 0.42, 330, 470);
        // A short window can scroll the entire inspector instead of clipping its
        // actions. In taller windows the process detail area fills the spare height.
        DetailPanel.Height = Math.Max(420, InspectorSplit.ActualHeight);

        var compact = AppsToolbar.ActualWidth < 650;
        if (_compactToolbar != compact)
        {
            _compactToolbar = compact;
            AppsToolbar.RowSpacing = compact ? 8 : 0;
            SearchColumn.Width = new GridLength(1, GridUnitType.Star);
            CategoryColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(170);
            SortColumn.Width = compact ? GridLength.Auto : new GridLength(140);
            RefreshColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumnSpan(SearchBox, compact ? 3 : 1);
            Grid.SetRow(CategoryFilter, compact ? 1 : 0);
            Grid.SetColumn(CategoryFilter, compact ? 0 : 1);
            Grid.SetRow(SortFilter, compact ? 1 : 0);
            Grid.SetColumn(SortFilter, compact ? 1 : 2);
            Grid.SetRow(RefreshButton, compact ? 1 : 0);
            Grid.SetColumn(RefreshButton, compact ? 2 : 3);

            SummaryBar.RowSpacing = compact ? 8 : 0;
            Grid.SetColumnSpan(SummaryLabels, compact ? 2 : 1);
            Grid.SetRow(SummaryActions, compact ? 1 : 0);
            Grid.SetColumn(SummaryActions, compact ? 0 : 1);
            Grid.SetColumnSpan(SummaryActions, compact ? 2 : 1);

            AppsFooter.RowSpacing = compact ? 8 : 0;
            Grid.SetColumnSpan(FooterLabels, compact ? 2 : 1);
            Grid.SetRow(CloseOthersButton, compact ? 1 : 0);
            Grid.SetColumn(CloseOthersButton, compact ? 0 : 1);
            Grid.SetColumnSpan(CloseOthersButton, compact ? 2 : 1);
            CloseOthersButton.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
        UpdateInspectorAvailability();
    }

    private void InspectorSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // RenderApps updates SelectedItem before committing its selection keys.
        // Observe after that synchronous update has completed, including removals.
        if (_inspectorUpdateQueued) return;
        _inspectorUpdateQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _inspectorUpdateQueued = false;
            if (!_closed) UpdateInspectorAvailability();
        });
    }

    private void UpdateInspectorAvailability()
    {
        InspectorToggleButton.IsEnabled = !_working && SelectedApp is not null;
        if (SelectedApp is null && _overlayInspector == true) InspectorSplit.IsPaneOpen = false;
    }

    private void OpenInspector(object sender, RoutedEventArgs args)
    {
        if (_working || SelectedApp is null) return;
        UpdateDetail();
        InspectorSplit.IsPaneOpen = true;
    }

    private void CloseInspector(object sender, RoutedEventArgs args)
    {
        if (_overlayInspector == true) InspectorSplit.IsPaneOpen = false;
    }
}
