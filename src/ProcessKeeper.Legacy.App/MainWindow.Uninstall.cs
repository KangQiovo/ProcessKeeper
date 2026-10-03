using ProcessKeeper.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private UninstallView? _uninstallView;
    private string _uninstallLanguage = "";

    private void EnsureUninstallPage()
    {
        if (_uninstallView is not null && _uninstallLanguage == L.Language) return;
        _uninstallView?.Dispose();
        _uninstallLanguage = L.Language;
        _uninstallView = new UninstallView((title, message) => ConfirmUninstall(title, message), Log,
            () => !_closed && !_busy && !_updateDialogOpen && !_updateDownloading && !_shortcutBusy, _backend.Uninstall, dataDirectory: _directory);
        _uninstallView.ChangingChanged += changing => { if (!_closed) _busy = changing; };
        _uninstallView.PresentationChanged += UninstallPresentationChanged;
        _uninstallView.InventoryChanged += (_, _) => OnUninstallInventoryChanged();
        _uninstallView.ApplyPresentation(_displayCatalog, _view.GroupGamePlatforms, _view.HideMicrosoftApps);
        _uninstallView.ApplyWhitelistProtection(IsUninstallWhitelisted);
        UninstallHost.Content = _uninstallView;
    }

    private async Task<bool> ConfirmUninstall(string title, string message, bool allowBusy = false)
    {
        if (_closed || (_busy && !allowBusy)) return false;
        var previousBusy = _busy; _busy = true;
        try
        {
            if (_backend.Confirm is not null) return await _backend.Confirm(title, message);
            var work = _backend.WorkingArea();
            var dialog = new Window { Owner = this, Title = "Process Keeper | " + title,
                Width = Math.Min(680, Math.Max(320, work.Width - 48)), Height = Math.Min(600, Math.Max(260, work.Height - 48)),
                MinWidth = 320, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, Background = (Brush)FindResource("PageBrush"), Foreground = (Brush)FindResource("InkBrush") };
            var layout = new Grid { Margin = new Thickness(20) };
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var details = new TextBox { Text = message, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent, Foreground = (Brush)FindResource("InkBrush"), BorderThickness = new Thickness(0) };
            layout.Children.Add(details);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Wpf.Ui.Controls.Button { Content = L.T("取消"), IsCancel = true, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 12, 0) };
            var accept = new Wpf.Ui.Controls.Button { Content = L.T("确认"), MinWidth = 90 };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            accept.Click += (_, _) => dialog.DialogResult = true;
            actions.Children.Add(cancel); actions.Children.Add(accept); Grid.SetRow(actions, 1); layout.Children.Add(actions);
            dialog.Content = layout;
            dialog.Loaded += (_, _) => cancel.Focus();
            return dialog.ShowDialog() == true;
        }
        finally { _busy = previousBusy; }
    }
}
