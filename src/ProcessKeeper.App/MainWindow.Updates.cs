using Microsoft.UI.Xaml;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private UpdatesView? _updatesView;
    private UpdateService? _updateService;
    private LauncherContext? _launcherContext;
    private string _launcherContextError = "";

    private void InitializeUpdates()
    {
        _updateService = new UpdateService();
        LauncherContext Context() => _launcherContext ?? throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(_launcherContextError) ? L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。") : _launcherContextError);
        var backend = new UpdateUiBackend
        {
            Check = (settings, version, token) => _updateService.CheckAsync(settings, version, token),
            Probe = (settings, release, asset, progress, token) => _updateService.ProbeAsync(settings, release, asset, progress, token),
            Download = (settings, release, asset, stage, progress, token) => _updateService.DownloadAsync(settings, release, asset, stage, progress, token),
            CreateSession = (release, asset, stage) => _updateService.CreateDownloadSession(release, asset, stage),
            ResumeDownload = (settings, session, progress, token) => _updateService.DownloadResumableAsync(settings, session, progress, token),
            DiscardSession = session => _updateService.DiscardDownloadSession(session),
            ConfirmExternalLink = async url =>
            {
                var dialog = new Windows.UI.Popups.MessageDialog(L.T("将在默认浏览器打开以下网址：") + "\n" + url, L.T("打开项目主页？"));
                dialog.Commands.Add(new Windows.UI.Popups.UICommand(L.T("打开"), null, "open"));
                dialog.Commands.Add(new Windows.UI.Popups.UICommand(L.T("取消"), null, "cancel"));
                dialog.DefaultCommandIndex = dialog.CancelCommandIndex = 1;
                WinRT.Interop.InitializeWithWindow.Initialize(dialog, WinRT.Interop.WindowNative.GetWindowHandle(this));
                return (await dialog.ShowAsync()).Id as string == "open";
            },
            CreateStage = () => UpdateInstaller.CreateDownloadStage(Context()),
            DiscardStage = stage => UpdateInstaller.DiscardDownloadStage(Context(), stage),
            Install = InstallApplicationUpdateAsync,
            ExitAfterUpdate = () => { _closeApproved = true; Close(); Microsoft.UI.Xaml.Application.Current.Exit(); },
            Shortcut = async token =>
            {
                token.ThrowIfCancellationRequested();
                var result = await Task.Run(() => DesktopShortcutService.Ensure(Context()), token);
                if (!result.Success) throw new IOException(result.Message);
                return result.Message;
            },
            UnavailableReason = () => _launcherContext is null
                ? string.IsNullOrWhiteSpace(_launcherContextError) ? L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。") : _launcherContextError
                : null
        };
        _updatesView = new UpdatesView(backend)
        {
            Present = ShowDialog,
            CanPresent = () => !_closed && !_closingIntent && !_closingMotion && !_savingBeforeTransition && !_replacementPreparing && !_working && !HasPendingTool && !_dialogOpen && !_windowOperationRunning && _autorunsView?.IsChanging != true,
            Log = Log
        };
        _updatesView.BusyChanged += busy => { if (!_closed) { LanguageChoice.IsEnabled = !busy; ReopenIntroductionButton.IsEnabled = !busy; } };
        UpdatesHost.Children.Add(_updatesView);
        var started = false;
        Root.Loaded += async (_, _) =>
        {
            if (started || _closed) return; started = true;
            await Task.Run(() => LauncherContextReader.TryGetCurrent(out _launcherContext, out _launcherContextError));
            if (!_closed) await _updatesView.StartOnLaunchAsync();
        };
        Closed += (_, _) => { _updatesView.Close(); _updateService.Dispose(); };
    }

    private async Task InstallApplicationUpdateAsync(UpdateDownloadResult download, CancellationToken token)
    {
        if (_closed || _working || HasPendingTool || _dialogOpen || _windowOperationRunning || _autorunsView?.IsChanging == true)
            throw new UpdateInstallDeferredException();
        var context = _launcherContext ?? throw new InvalidOperationException(L.T("当前文件不支持应用内更新，请从原始单文件 EXE 启动。"));
        _working = true; Navigation.IsEnabled = AppsList.IsEnabled = false;
        PreparedUpdate? prepared = null; var handedOff = false;
        try
        {
            await FlushPerformanceAsync();
            prepared = await Task.Run(() => UpdateInstaller.Prepare(context, download, token), token);
            token.ThrowIfCancellationRequested();
            if (_closed || HasPendingTool || _dialogOpen || _windowOperationRunning || _autorunsView?.IsChanging == true)
                throw new UpdateInstallDeferredException();
            var result = await Task.Run(() => UpdateInstaller.Start(prepared));
            if (!result.Success) throw new IOException(result.Message);
            handedOff = true;
            Log(L.T("更新并重新启动") + " | " + download.Release.Tag);
        }
        finally
        {
            try
            {
                if (!handedOff && prepared is not null)
                    try { await Task.Run(() => UpdateInstaller.DiscardPrepared(prepared)); }
                    catch (Exception exception) { Log(L.T("更新失败") + " | " + exception.Message); }
            }
            finally
            {
                if (!_closed) { _working = false; Navigation.IsEnabled = AppsList.IsEnabled = true; }
            }
        }
    }
}
