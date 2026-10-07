using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal sealed class UpdateDownloadWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _sourceText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _pause = new(), _install = new(), _cancel = new();
    private readonly UpdateTransferController _transfer;
    private readonly Func<bool>? _canInstall;
    private readonly UpdateSource[] _nodes = UpdateSources.All.Where(item => !item.IsOfficial).ToArray();
    private bool _closing;
    internal UpdateDownloadWindow(Window owner, UpdateTransferController transfer, UpdateRelease release, UpdateAsset asset, Action cancel, Action install, bool activate = true, Func<bool>? canInstall = null)
    {
        Owner = owner; Resources = owner.Resources; ShowActivated = activate;
        Title = "Process Keeper | " + L.T("更新器"); Width = 620; Height = 560; MinWidth = 480; MinHeight = 400;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/ProcessKeeperUpdater.ico", UriKind.Absolute));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PageBrush"); SetResourceReference(ForegroundProperty, "InkBrush");
        _transfer = transfer; _canInstall = canInstall;
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = MainWindow.DisplayUpdateReleaseTag(release), FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(new TextBlock { Text = asset.Name, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(_status); body.Children.Add(_bar); body.Children.Add(_detail); body.Children.Add(_sourceText);
        body.Children.Add(new TextBlock { Text = L.T("更新源"), Margin = new Thickness(0, 0, 0, 8) });
        foreach (var text in new[] { L.T("自动（优先 GitHub 官方源）"), L.T("官方 GitHub"), L.T("第三方加速") }) _source.Items.Add(text);
        foreach (var node in _nodes) _source.Items.Add(node.Name);
        _source.SelectedIndex = transfer.Preferences.SourceMode == UpdateSourceMode.Auto ? 0 : transfer.Preferences.SourceMode == UpdateSourceMode.Official ? 1 :
            transfer.Preferences.ThirdPartySourceId == "auto" ? 2 : Math.Max(2, Array.FindIndex(_nodes, node => node.Id == transfer.Preferences.ThirdPartySourceId) + 3);
        if (Application.Current.TryFindResource(typeof(ComboBox)) is Style sourceStyle) _source.Style = sourceStyle;
        // The native ComboBox keeps a light system surface, including its disabled state.
        _source.Foreground = Brushes.Black; _source.Background = Brushes.White; body.Children.Add(_source);
        body.Children.Add(new TextBlock { Text = L.T("自动模式优先使用 GitHub 官方源；不可用时再尝试第三方源。"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(new TextBlock { Text = L.T("暂停后可切换更新源；返回应用后下载仍会继续。"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _pause.Content = L.T("暂停下载"); _install.Content = L.T(asset.DistributionKind == UpdateDistributionKind.Installer ? "退出并启动安装程序" : "更新并重新启动"); _cancel.Content = L.T("取消下载");
        foreach (var button in new[] { _pause, _install, _cancel }) { button.Style = (Style)owner.Resources[typeof(Button)]; actions.Children.Add(button); }
        body.Children.Add(actions);
        var back = new Button { Content = L.T("返回应用"), Style = (Style)owner.Resources[typeof(Button)], HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_, _) => Hide(); body.Children.Add(back);
        var backdrop = new Border { Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        backdrop.SetResourceReference(Border.BackgroundProperty, "PageBrush"); Content = backdrop;
        Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
        _pause.Click += (_, _) => { if (_transfer.Snapshot.Phase == UpdateTransferPhase.Paused) _transfer.Resume(SelectedPreferences()); else _transfer.Pause(); };
        _cancel.Click += (_, _) => cancel(); _install.Click += (_, _) => { if (_install.IsEnabled) install(); }; Refresh(transfer.Snapshot);
    }
    internal void Reveal() { if (!_closing) { Show(); if (ShowActivated) Activate(); } }
    internal void Finish() { _closing = true; Close(); }
    private UpdatePreferences SelectedPreferences() => _transfer.Preferences with
    { SourceMode = _source.SelectedIndex == 0 ? UpdateSourceMode.Auto : _source.SelectedIndex == 1 ? UpdateSourceMode.Official : UpdateSourceMode.ThirdParty,
        ThirdPartySourceId = _source.SelectedIndex < 3 ? "auto" : _nodes[_source.SelectedIndex - 3].Id };
    internal void Refresh(UpdateTransferSnapshot state)
    {
        if (_closing) return;
        _status.Text = state.Message; _detail.Text = state.Detail;
        _sourceText.Text = L.T("更新源") + " | " + (UpdateSources.All.FirstOrDefault(node => node.Id == state.SourceId)?.Name ?? state.SourceId);
        _bar.IsIndeterminate = state.Total <= 0 || state.Phase is UpdateTransferPhase.Starting or UpdateTransferPhase.Pausing; _bar.Value = state.Percent;
        _source.IsEnabled = state.Phase == UpdateTransferPhase.Paused;
        _pause.Content = L.T(state.Phase == UpdateTransferPhase.Paused ? "继续下载" : "暂停下载");
        _pause.IsEnabled = _transfer.CanPause && state.Phase is UpdateTransferPhase.Paused or UpdateTransferPhase.Downloading;
        _install.IsEnabled = state.Phase == UpdateTransferPhase.Ready && (_canInstall?.Invoke() ?? true);
        _install.Visibility = _install.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = L.T("取消下载"); _cancel.IsEnabled = state.Phase != UpdateTransferPhase.Installing;
    }
}
