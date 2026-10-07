using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using ProcessKeeper.Core;
using System.Runtime.InteropServices;

namespace ProcessKeeper.App;

/// <summary>A modeless native progress window. Closing its chrome hides the transfer rather than canceling it.</summary>
internal sealed class UpdateDownloadWindow : Window
{
    private readonly StackPanel _body = new() { Spacing = 12, Margin = new Thickness(24) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _sourceText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly ComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Header = L.T("更新源") };
    private readonly Button _pause = new(), _install = new(), _cancel = new();
    private readonly UpdateTransferController _transfer;
    private readonly Action _cancelAction;
    private readonly Action _installAction;
    private readonly UpdateSource[] _nodes = UpdateSources.All.Where(item => !item.IsOfficial).ToArray();
    private bool _closing;
    private readonly bool _activate;
    private readonly ScrollViewer _viewport;
    private ThemeSettings? _captionThemeSettings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _captionDispatcher;
    internal UpdateDownloadWindow(UpdateTransferController transfer, UpdateRelease release, UpdateAsset asset, ElementTheme theme, Action cancel, Action install, bool activate = true)
    {
        _activate = activate;
        _captionDispatcher = _body.DispatcherQueue;
        _transfer = transfer; _cancelAction = cancel; _installAction = install;
        Title = "Process Keeper | " + L.T("更新器"); _body.RequestedTheme = theme;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProcessKeeperUpdater.ico"));
        _body.Children.Add(new TextBlock { Text = UpdatesView.DisplayReleaseTag(release), FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        _body.Children.Add(new TextBlock { Text = asset.Name, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        _body.Children.Add(_status); _body.Children.Add(_bar); _body.Children.Add(_detail); _body.Children.Add(_sourceText);
        foreach (var text in new[] { L.T("自动（优先 GitHub 官方源）"), L.T("官方 GitHub"), L.T("第三方加速") }) _source.Items.Add(text);
        foreach (var node in _nodes) _source.Items.Add(node.Name);
        _source.SelectedIndex = transfer.Preferences.SourceMode == UpdateSourceMode.Auto ? 0 : transfer.Preferences.SourceMode == UpdateSourceMode.Official ? 1 :
            transfer.Preferences.ThirdPartySourceId == "auto" ? 2 : Math.Max(2, Array.FindIndex(_nodes, node => node.Id == transfer.Preferences.ThirdPartySourceId) + 3);
        _body.Children.Add(_source);
        _body.Children.Add(new TextBlock { Text = L.T("暂停后可切换更新源；返回应用后下载仍会继续。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        _pause.Content = L.T("暂停下载"); _install.Content = L.T(asset.DistributionKind == UpdateDistributionKind.Installer ? "退出并启动安装程序" : "更新并重新启动"); _cancel.Content = L.T("取消下载");
        actions.Children.Add(_pause); actions.Children.Add(_install); actions.Children.Add(_cancel);
        foreach (var button in new[] { _pause, _install, _cancel }) button.Margin = new Thickness(0, 0, 8, 4);
        actions.SizeChanged += (_, _) => NativeCommandLayout.Reflow(actions);
        _body.Children.Add(actions);
        var back = new Button { Content = L.T("返回应用"), HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => AppWindow.Hide(); _body.Children.Add(back);
        _viewport = (ScrollViewer)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<ScrollViewer xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource ApplicationPageBackgroundThemeBrush}' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'/>");
        _viewport.Content = _body; _viewport.RequestedTheme = theme; Content = _viewport;
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                _captionThemeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
                _captionThemeSettings.Changed += CaptionThemeChanged;
            }
        }
        catch (Exception error) when (IsCaptionInteropFailure(error)) { }
        _body.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        UpdateCaptionTheme();
        Closed += (_, _) =>
        {
            _closing = true;
            try { if (_captionThemeSettings is not null) _captionThemeSettings.Changed -= CaptionThemeChanged; }
            catch (Exception error) when (IsCaptionInteropFailure(error)) { }
            _captionThemeSettings = null;
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(620, 560));
        WindowSizePolicy.Attach(this, 480, 400);
        AppWindow.Closing += (_, args) => { if (!_closing) { args.Cancel = true; AppWindow.Hide(); } };
        _pause.Click += (_, _) => { if (_transfer.Snapshot.Phase == UpdateTransferPhase.Paused) _transfer.Resume(SelectedPreferences()); else _transfer.Pause(); };
        _cancel.Click += (_, _) => _cancelAction(); _install.Click += (_, _) => _installAction();
        Refresh(transfer.Snapshot);
    }
    internal void ApplyTheme(ElementTheme theme) { _body.RequestedTheme = theme; _viewport.RequestedTheme = theme; UpdateCaptionTheme(); }
    private static bool IsCaptionInteropFailure(Exception error) => error is COMException or InvalidOperationException or NotSupportedException or ArgumentException;
    private void CaptionThemeChanged(ThemeSettings sender, object args)
    {
        if (_closing) return;
        try { _captionDispatcher.TryEnqueue(() => { if (!_closing && ReferenceEquals(sender, _captionThemeSettings)) UpdateCaptionTheme(); }); }
        catch (Exception error) when (IsCaptionInteropFailure(error)) { }
    }
    private void UpdateCaptionTheme()
    {
        if (_closing) return;
        try
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            // Keep native high-contrast and button palettes, as the main window does.
            var theme = TitleBarTheme.Legacy;
            try { if (_captionThemeSettings is { HighContrast: false }) theme = _body.ActualTheme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark; }
            catch (Exception error) when (IsCaptionInteropFailure(error)) { }
            AppWindow.TitleBar.PreferredTheme = theme;
        }
        catch (Exception error) when (IsCaptionInteropFailure(error)) { }
    }
    internal void Reveal() { if (!_closing) { AppWindow.Show(_activate); if (_activate) Activate(); } }
    internal void Finish() { _closing = true; Close(); }
    private UpdatePreferences SelectedPreferences() => _transfer.Preferences with
    { SourceMode = _source.SelectedIndex == 0 ? UpdateSourceMode.Auto : _source.SelectedIndex == 1 ? UpdateSourceMode.Official : UpdateSourceMode.ThirdParty,
        ThirdPartySourceId = _source.SelectedIndex < 3 ? "auto" : _nodes[_source.SelectedIndex - 3].Id };
    internal void Refresh(UpdateTransferSnapshot state, bool canInstall = true)
    {
        if (_closing) return;
        _status.Text = state.Message; _detail.Text = state.Detail;
        _sourceText.Text = L.T("更新源") + " | " + (UpdateSources.All.FirstOrDefault(node => node.Id == state.SourceId)?.Name ?? state.SourceId);
        _bar.IsIndeterminate = state.Total <= 0 || state.Phase is UpdateTransferPhase.Starting or UpdateTransferPhase.Pausing;
        _bar.Value = state.Percent;
        _source.IsEnabled = state.Phase == UpdateTransferPhase.Paused;
        _pause.Content = L.T(state.Phase == UpdateTransferPhase.Paused ? "继续下载" : "暂停下载");
        _pause.IsEnabled = _transfer.CanPause && state.Phase is UpdateTransferPhase.Paused or UpdateTransferPhase.Downloading;
        _install.Visibility = state.Phase == UpdateTransferPhase.Ready && canInstall ? Visibility.Visible : Visibility.Collapsed;
        _install.IsEnabled = state.Phase == UpdateTransferPhase.Ready && canInstall;
        _cancel.Content = L.T("取消下载");
        _cancel.IsEnabled = state.Phase != UpdateTransferPhase.Installing;
    }
}
