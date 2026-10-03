using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed class MemoryOptimizationView : UserControl, IDisposable
{
    private readonly ComboBox _mode = new() { Header = L.T("优化范围"), HorizontalAlignment = HorizontalAlignment.Stretch,
        ItemsSource = new[] { L.T("标准优化"), L.T("深度优化") }, SelectedIndex = 0 };
    private readonly Button _start = new() { Content = L.T("一键优化内存") }, _stop = new() { Content = L.T("取消后续步骤"), IsEnabled = false };
    private readonly ProgressRing _ring = new() { Width = 20, Height = 20, IsActive = false };
    private readonly TextBox _report = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), MaxHeight = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly DispatcherTimer _successTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly MemoryOptimizationService _service;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<bool> _canStart;
    private readonly Action<string> _log;
    private CancellationTokenSource? _operation;
    private Task _task = Task.CompletedTask;
    private bool _disposed;
    private long _epoch;
    public bool IsBusy { get; private set; }
    public event Action<bool>? BusyChanged;
    public MemoryOptimizationView(Func<string, string, Task<bool>> confirm, Func<bool> canStart, Action<string> log, IMemoryOptimizationBackend? backend = null)
    {
        _confirm = confirm; _canStart = canStart; _log = log;
        _service = new MemoryOptimizationService(backend ?? new WindowsMemoryOptimizationBackend());
        var panel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = L.T("按需执行，可能短暂卡顿。白名单不豁免。"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_mode);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(_start); actions.Children.Add(_stop); actions.Children.Add(_ring); panel.Children.Add(actions);
        panel.Children.Add(_notice); panel.Children.Add(_report); Content = panel;
        _start.Click += async (_, _) => { if (IsBusy || _disposed || !_canStart()) return; _task = RunAsync(); await _task; };
        _stop.Click += (_, _) => _operation?.Cancel();
        _successTimer.Tick += (_, _) => { _successTimer.Stop(); if (!_disposed && _notice.Severity == InfoBarSeverity.Success) _notice.IsOpen = false; };
    }
    private async Task RunAsync()
    {
        IsBusy = true; _mode.IsEnabled = _start.IsEnabled = false; _stop.IsEnabled = true; _ring.IsActive = true; BusyChanged?.Invoke(true);
        var epoch = ++_epoch;
        _operation = new CancellationTokenSource(); _successTimer.Stop(); _notice.IsOpen = false; _notice.Message = ""; _report.Text = "";
        try
        {
            var mode = _mode.SelectedIndex == 1 ? MemoryOptimizationMode.Deep : MemoryOptimizationMode.Standard;
            var progress = new Progress<MemoryOperation>(step => { if (!_disposed && IsBusy && epoch == _epoch) _report.Text += ToolboxText.MemoryLine(step) + "\n"; });
            var result = await _service.RunAsync(mode, () => _confirm(L.T("优化整机内存？"), ToolboxText.MemoryConfirmation(mode)), progress, _operation.Token);
            if (_disposed || !result.Confirmed) return;
            ++_epoch; _report.Text = ToolboxText.MemoryReport(result);
            var failed = result.Operations.Any(s => s.Status != MemoryOperationStatus.Succeeded);
            _notice.Severity = result.Cancelled || failed ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            _notice.Title = L.T(result.Cancelled ? "内存优化已停止" : failed ? "部分优化步骤未完成" : "内存优化已完成"); _notice.IsOpen = true;
            _log(_notice.Title + " | " + _report.Text.Replace("\n", " | "));
            if (_notice.Severity == InfoBarSeverity.Success) _successTimer.Start();
        }
        catch (Exception error) { if (!_disposed) { _notice.Severity = InfoBarSeverity.Error; _notice.Title = L.T("内存优化失败"); _notice.Message = error.Message; _notice.IsOpen = true; _log(_notice.Title + " | " + error.Message); } }
        finally
        {
            ++_epoch; _operation.Dispose(); _operation = null; IsBusy = false;
            if (!_disposed) { _mode.IsEnabled = _start.IsEnabled = true; _stop.IsEnabled = false; _ring.IsActive = false; BusyChanged?.Invoke(false); }
        }
    }
    public async Task StopAndWaitAsync() { _operation?.Cancel(); await _task; }
    public void Dispose() { if (_disposed) return; _disposed = true; _operation?.Cancel(); _successTimer.Stop(); }
}
