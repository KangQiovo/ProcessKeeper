using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private bool _checkingCompatibility;

    private async Task CheckCompatibilityAsync()
    {
        if (_closed || _busy || _checkingCompatibility || _updateDialogOpen || _updateDownloading || _shortcutBusy) return;
        _checkingCompatibility = true;
        try
        {
            RuntimeEnvironmentReport? report = null;
            try { report = await Task.Run(_backend.CaptureEnvironment, _life.Token); }
            catch (OperationCanceledException) when (_closed) { return; }
            catch (Exception) { }
            if (_closed || _busy || _updateDialogOpen || _updateDownloading || _shortcutBusy) return;
            var work = _backend.WorkingArea();
            var dialog = new Window { Owner = this, Title = "Process Keeper | " + L.T("运行环境检测"),
                Width = Math.Min(660, Math.Max(320, work.Width - 48)), Height = Math.Min(540, Math.Max(260, work.Height - 48)),
                MinWidth = 320, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = (Brush)FindResource("PageBrush"), Foreground = (Brush)FindResource("InkBrush") };
            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var body = new StackPanel();
            bool passed = report?.NecessaryConditionsPassed == true && report.MemoryAvailable;
            body.Children.Add(new Wpf.Ui.Controls.InfoBar { IsOpen = true, IsClosable = false,
                Severity = passed ? Wpf.Ui.Controls.InfoBarSeverity.Success : Wpf.Ui.Controls.InfoBarSeverity.Warning,
                Title = L.T(passed ? "环境检测通过" : "环境检测需要关注"), Margin = new Thickness(0, 0, 0, 12) });
            var details = new TextBox { Text = report?.Description ?? L.T("部分运行环境无法读取，可重新检测。"),
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = (Brush)FindResource("InkBrush") };
            body.Children.Add(details);
            var links = new List<(string Name, string Url)>();
            if (report is null || !report.RuntimeSupported)
                links.Add(("Microsoft .NET Framework 4.6.2", "https://dotnet.microsoft.com/en-us/download/dotnet-framework/net462"));
            if (report is null || !report.OperatingSystemSupported)
                links.Add(("Windows 7 SP1", "https://www.catalog.update.microsoft.com/Search.aspx?q=KB976932"));
            if (report is not null && !report.Administrator)
                links.Add((L.T("管理员权限：未获得"), "https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/"));
            if (report is not null && !report.MemoryAvailable)
                links.Add((L.T("物理内存：无法读取"), "https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex"));
            foreach (var link in links)
            {
                var button = new Wpf.Ui.Controls.Button { Content = link.Name, HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 12, 0, 0), ToolTip = link.Url };
                button.Click += (_, _) =>
                {
                    if (MessageBox.Show(dialog, L.T("将在默认浏览器打开以下网址：") + "\n\n" + link.Url,
                        "Process Keeper", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
                        OpenWebsite(link.Url);
                };
                body.Children.Add(button);
            }
            grid.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var close = new Wpf.Ui.Controls.Button { Content = L.T("关闭"), IsCancel = true, MinWidth = 90,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            close.Click += (_, _) => dialog.Close(); Grid.SetRow(close, 1); grid.Children.Add(close);
            dialog.Content = grid; dialog.ShowDialog();
        }
        catch (Exception ex) { if (!_closed) Notice(L.T("环境检测需要关注") + " | " + ex.Message); }
        finally { _checkingCompatibility = false; }
    }
}
