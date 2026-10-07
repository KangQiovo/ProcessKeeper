using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ProcessKeeper.Core;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace ProcessKeeper.App;

public sealed class ResourceDownloadView : UserControl, IDisposable
{
    private readonly TextBox _url = new() { HorizontalAlignment = HorizontalAlignment.Stretch },
        _folder = new() { IsReadOnly = true, HorizontalAlignment = HorizontalAlignment.Stretch }, _name = new() { HorizontalAlignment = HorizontalAlignment.Stretch },
        _agent = new() { Text = "ProcessKeeper/" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.7.1"), HorizontalAlignment = HorizontalAlignment.Stretch }, _sha = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _source = new() { HorizontalAlignment = HorizontalAlignment.Stretch },
        _connections = new() { ItemsSource = Enumerable.Range(1, 32).ToArray(), SelectedIndex = 15, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _start = new() { Content = L.T("开始下载") }, _pause = new() { Content = L.T("暂停"), IsEnabled = false },
        _cancel = new() { Content = L.T("取消下载"), IsEnabled = false }, _browse = new() { Content = L.T("选择目录") }, _locate = new() { Content = L.T("打开保存目录") };
    private readonly ProgressBar _bar = new() { Maximum = 100, Height = 5, Visibility = Visibility.Collapsed };
    private readonly TextBlock _progress = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _result = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 12 };
    private readonly InfoBar _notice = new() { IsClosable = true, IsOpen = false };
    private readonly DispatcherTimer _successTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string> _openLocation, _log;
    private readonly Func<bool> _canStart;
    private readonly Func<ResourceDownloadRequest, ResourceDownloadJob> _createJob;
    private ResourceDownloadJob? _job;
    private ResourceDownloadRequest? _request;
    private Task _task = Task.CompletedTask;
    private bool _disposed;
    private bool _choosingFolder;
    private long _epoch;
    public bool IsBusy { get; private set; }
    public bool HasPendingJob => _job is not null;

    public ResourceDownloadView(Func<Task<string?>> pickFolder, Func<string, string, Task<bool>> confirm,
        Action<string> openLocation, Action<string> log, Func<bool> canStart,
        Func<ResourceDownloadRequest, ResourceDownloadJob>? createJob = null)
    {
        _pickFolder = pickFolder; _confirm = confirm; _openLocation = openLocation; _log = log; _canStart = canStart;
        _createJob = createJob ?? new Func<ResourceDownloadRequest, ResourceDownloadJob>(request => new ResourceDownloadJob(request));
        _browse.Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Folder24, FontSize = 20 };
        _browse.ToolTip = L.T("选择目录"); _browse.MinWidth = 40;
        System.Windows.Automation.AutomationProperties.SetName(_browse, L.T("选择目录"));
        _source.ItemsSource = ResourceDownloadSources.All.Select(source => source.Id == "official" ? L.T("原始直链") : source.Id == "auto" ? L.T("自动选择 GitHub 加速源") : source.Name + " | " + L.T("第三方")).ToArray(); _source.SelectedIndex = 0;
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        void Add(FrameworkElement control) { control.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(control); }
        StackPanel Field(string label, FrameworkElement control) { var field = new StackPanel(); field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) }); field.Children.Add(control); return field; }
        Add(new TextBlock { Text = L.T("文件直链高速下载 | 自动拆分任务与失败重试，速度取决于服务器和网络。"), TextWrapping = TextWrapping.Wrap });
        var folderRow = new Grid();
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _browse.Margin = new Thickness(8, 0, 0, 0); _browse.VerticalAlignment = VerticalAlignment.Center;
        folderRow.Children.Add(_folder); Grid.SetColumn(_browse, 1); folderRow.Children.Add(_browse);
        Add(Field(L.T("下载地址"), _url)); Add(Field(L.T("保存目录"), folderRow)); Add(Field(L.T("文件名"), _name));
        var folders = new WrapPanel(); _locate.Margin = new Thickness(0, 0, 0, 4); folders.Children.Add(_locate); Add(folders);
        var advanced = new StackPanel(); foreach (var field in new[] { Field(L.T("最大并发连接"), _connections), Field("User-Agent", _agent), Field(L.T("SHA256（可选）"), _sha), Field(L.T("GitHub 镜像（可选）"), _source) }) { field.Margin = new Thickness(0, 0, 0, 12); advanced.Children.Add(field); }
        Add(new Expander { Header = L.T("下载选项"), Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var actions = new WrapPanel(); foreach (var button in new[] { _start, _pause, _cancel }) { button.Margin = new Thickness(0, 0, 10, 4); actions.Children.Add(button); } Add(actions);
        Add(_bar); Add(_progress); Add(_notice); Add(_result); Content = panel;
        _url.TextChanged += (_, _) => { if (_name.Text.Length != 0) return; try { _name.Text = Uri.UnescapeDataString(Path.GetFileName(ResourceDownloadJob.NormalizeUrl(_url.Text).AbsolutePath)); } catch (ArgumentException) { } };
        _browse.Click += async (_, _) =>
        {
            if (_disposed || IsBusy || HasPendingJob || _choosingFolder) return;
            _choosingFolder = true; _browse.IsEnabled = _start.IsEnabled = false;
            try { var path = await _pickFolder(); if (!_disposed && path is not null) _folder.Text = path; }
            catch (Exception error) { if (!_disposed) Notice(error); }
            finally { _choosingFolder = false; if (!_disposed) UpdateControls(IsBusy); }
        };
        _locate.Click += (_, _) => { if (_disposed || _folder.Text.Length == 0) return; try { _openLocation(_folder.Text); } catch (Exception error) { Notice(error); } };
        _start.Click += async (_, _) => { if (_disposed || IsBusy || _choosingFolder || !_canStart()) return; _task = RunAsync(); await _task; };
        _pause.Click += (_, _) => _job?.Pause();
        _cancel.Click += (_, _) =>
        {
            var cleanupFailed = false;
            try { _job?.Dispose(); }
            catch (Exception error) { cleanupFailed = true; if (!_disposed) { Notice(error); _progress.Text = L.T("下载已取消，部分临时文件未能清理。"); } }
            finally
            {
                _job = null;
                if (!IsBusy && !_disposed) { UpdateControls(false); _bar.IsIndeterminate = false; if (!cleanupFailed) _progress.Text = L.T("下载已取消"); }
            }
        };
        _successTimer.Tick += (_, _) => { _successTimer.Stop(); if (!_disposed && _notice.Severity == InfoBarSeverity.Success) _notice.IsOpen = false; };
    }
    private async Task RunAsync()
    {
        var epoch = ++_epoch;
        IsBusy = true; UpdateControls(true); _notice.IsOpen = false; _successTimer.Stop(); _bar.Visibility = Visibility.Visible; _bar.IsIndeterminate = true;
        try
        {
            if (_job is null)
            {
                var uri = ResourceDownloadJob.NormalizeUrl(_url.Text);
                var source = ResourceDownloadSources.All[Math.Max(0, _source.SelectedIndex)];
                var thirdParty = source.Id != "official" && (source.Id != "auto" || ResourceDownloadJob.CanUseGitHubMirror(uri));
                if (thirdParty && !await _confirm(L.T("使用第三方下载源？"), L.T("下载链接将发送到所选加速服务。第三方服务可能记录链接或修改文件，仅适用于公开链接，请勿填写私有链接。带查询参数的链接不参与加速。建议填写可信来源提供的 SHA256。是否继续？"))) return;
                if (_disposed) return;
                _request = new(_url.Text, _folder.Text, _name.Text, _agent.Text, _connections.SelectedItem is int connections ? connections : 16, source.Id, _sha.Text.Trim(), thirdParty);
                _result.Text = ""; _job = _createJob(_request);
            }
            var job = _job;
            var progress = new Progress<ResourceDownloadProgress>(value =>
            {
                if (_disposed || epoch != _epoch || !ReferenceEquals(_job, job) || !IsBusy) return;
                _progress.Text = ToolboxText.DownloadProgress(value); _bar.IsIndeterminate = !value.Total.HasValue || value.Total == 0;
                if (value.Total > 0) _bar.Value = Math.Max(0, Math.Min(99.99, value.Bytes * 100d / value.Total.Value));
            });
            var result = await job.RunAsync(progress);
            if (_disposed) return;
            ++_epoch;
            _result.Text = result.Path + "\nSHA256 | " + result.Sha256;
            _progress.Text = ToolboxText.DownloadProgress(new(result.Bytes, result.Bytes, 0, result.Source, "completed"));
            _notice.Severity = InfoBarSeverity.Success; _notice.Title = L.T("下载已完成"); _notice.Message = ""; _notice.IsOpen = true; _successTimer.Start(); _bar.Value = 100; _bar.IsIndeterminate = false;
            _log(L.T("下载已完成") + " | " + _request!.FileName + " | " + result.Source + " | SHA256 " + result.Sha256);
            job.Dispose(); _job = null;
        }
        catch (OperationCanceledException) { if (!_disposed) _progress.Text = L.T(_job is null ? "下载已取消" : "下载已暂停，可继续。"); }
        catch (Exception error) { if (!_disposed) { Notice(error); _log(L.T("下载失败") + " | " + ToolboxText.DownloadError(error)); } }
        finally
        {
            ++_epoch; IsBusy = false;
            if (!_disposed) { _bar.IsIndeterminate = false; if (_job is null && !_notice.IsOpen) _bar.Visibility = Visibility.Collapsed; UpdateControls(false); }
        }
    }
    private void Notice(Exception error) { _notice.Severity = InfoBarSeverity.Error; _notice.Title = L.T("下载失败"); _notice.Message = ToolboxText.DownloadError(error); _notice.IsOpen = true; }
    private void UpdateControls(bool running)
    {
        foreach (var control in new Control[] { _url, _folder, _name, _source, _connections, _agent, _sha, _browse }) control.IsEnabled = !running && _job is null;
        _browse.IsEnabled = !running && _job is null && !_choosingFolder;
        _start.IsEnabled = !running && !_choosingFolder; _start.Content = L.T(_job is null ? "开始下载" : "继续下载"); _pause.IsEnabled = running; _cancel.IsEnabled = _job is not null || running;
    }
    public async Task CancelAndWaitAsync()
    {
        var job = _job; _job = null;
        try { job?.Dispose(); } finally { await _task; }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _successTimer.Stop();
        try { _job?.Dispose(); }
        catch (Exception error) { _log(L.T("临时下载文件未能清理") + " | " + ToolboxText.DownloadError(error)); }
        finally { _job = null; }
    }
}
