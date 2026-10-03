using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly UpdateSource[] _nodes = UpdateSources.All.Where(item => !item.IsOfficial).ToArray();
    private bool _closing;
    internal UpdateDownloadWindow(Window owner, UpdateTransferController transfer, UpdateRelease release, UpdateAsset asset, Action cancel, Action install, bool activate = true)
    {
        Owner = owner; Resources = owner.Resources; ShowActivated = activate;
        Title = "Process Keeper | " + L.T("下载并更新"); Width = 620; Height = 560; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PageBrush"); SetResourceReference(ForegroundProperty, "InkBrush");
        _transfer = transfer;
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = release.Tag, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(new TextBlock { Text = asset.Name, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(_status); body.Children.Add(_bar); body.Children.Add(_detail); body.Children.Add(_sourceText);
        body.Children.Add(new TextBlock { Text = L.T("更新源"), Margin = new Thickness(0, 0, 0, 8) });
        foreach (var text in new[] { L.T("自动选择最低延迟"), L.T("官方 GitHub"), L.T("第三方加速") }) _source.Items.Add(text);
        foreach (var node in _nodes) _source.Items.Add(node.Name);
        _source.SelectedIndex = transfer.Preferences.SourceMode == UpdateSourceMode.Auto ? 0 : transfer.Preferences.SourceMode == UpdateSourceMode.Official ? 1 :
            transfer.Preferences.ThirdPartySourceId == "auto" ? 2 : Math.Max(2, Array.FindIndex(_nodes, node => node.Id == transfer.Preferences.ThirdPartySourceId) + 3);
        if (Application.Current.TryFindResource(typeof(ComboBox)) is Style sourceStyle) _source.Style = sourceStyle;
        _source.SetResourceReference(ForegroundProperty, "InkBrush"); _source.SetResourceReference(BackgroundProperty, "PanelBrush"); body.Children.Add(_source);
        body.Children.Add(new TextBlock { Text = L.T("暂停后可切换更新源；返回应用后下载仍会继续。"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _pause.Content = L.T("暂停下载"); _install.Content = L.T("更新并重新启动"); _cancel.Content = L.T("取消下载");
        foreach (var button in new[] { _pause, _install, _cancel }) { button.Style = (Style)owner.Resources[typeof(Button)]; actions.Children.Add(button); }
        body.Children.Add(actions);
        var back = new Button { Content = L.T("返回应用"), Style = (Style)owner.Resources[typeof(Button)], HorizontalAlignment = HorizontalAlignment.Left }; back.Click += (_, _) => Hide(); body.Children.Add(back);
        var backdrop = new Border { Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        backdrop.SetResourceReference(Border.BackgroundProperty, "PageBrush"); Content = backdrop;
        Closing += (_, args) => { if (!_closing) { args.Cancel = true; Hide(); } };
        _pause.Click += (_, _) => { if (_transfer.Snapshot.Phase == UpdateTransferPhase.Paused) _transfer.Resume(SelectedPreferences()); else _transfer.Pause(); };
        _cancel.Click += (_, _) => cancel(); _install.Click += (_, _) => install(); Refresh(transfer.Snapshot);
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
        _install.Visibility = state.Phase == UpdateTransferPhase.Ready ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = L.T("取消下载"); _cancel.IsEnabled = state.Phase != UpdateTransferPhase.Installing;
    }
}
