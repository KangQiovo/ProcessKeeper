using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private UninstallView? _uninstallView;

    private void InitializeUninstallPage()
    {
        _uninstallView = new UninstallView(
            async (title, message) =>
            {
                var viewport = new ScrollViewer { MaxHeight = Math.Max(180, (Root.XamlRoot?.Size.Height ?? 800) - 220),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } };
                void Resize(object sender, SizeChangedEventArgs args) => viewport.MaxHeight = Math.Max(180, Root.ActualHeight - 220);
                Root.SizeChanged += Resize;
                try { return await ShowDialog(NewDialog(title, viewport, L.T("确认"))) == ContentDialogResult.Primary; }
                finally { Root.SizeChanged -= Resize; }
            },
            Log, () => !_closed && !_working && !_dialogOpen && !_windowOperationRunning && _updatesView?.IsBusy != true && _autorunsView?.IsChanging != true,
            dataDirectory: Path.GetDirectoryName(_store.FilePath)!);
        _uninstallView.ChangingChanged += changing => { if (!_closed) _working = changing; };
        _uninstallView.PresentationChanged += (_, _) => ApplyPresentationChoice(_uninstallView.GroupGamePlatforms, _uninstallView.HideMicrosoftApps);
        _uninstallView.InventoryChanged += (_, _) => OnUninstallInventoryChanged();
        _uninstallView.ApplyPresentation(_displayCatalog, _groupGamePlatforms, _hideMicrosoftApps);
        UninstallPage.Children.Add(_uninstallView);
    }
}
