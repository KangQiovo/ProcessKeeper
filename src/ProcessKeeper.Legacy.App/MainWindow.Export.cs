using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private sealed record ExportOptions(bool WhitelistOnly, bool ForSharing);

    private async Task<ExportOptions?> ChooseExportOptions()
    {
        var area = _backend.WorkingArea();
        var window = new Window
        {
            Owner = this, Title = "Process Keeper | " + L.T("导出"),
            Width = Math.Min(540, area.Width > 0 ? area.Width : 540),
            MaxHeight = area.Height > 0 ? area.Height : double.PositiveInfinity,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = (Brush)Resources["PageBrush"], Foreground = (Brush)Resources["InkBrush"]
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        window.Resources["PanelBrush"] = Resources["PanelBrush"];
        window.Resources["InkBrush"] = Resources["InkBrush"];
        panel.Children.Add(Text(L.T("导出内容")));
        var content = new ComboBox
        {
            Tag = "export-content", ItemsSource = new[] { L.T("全部设置与白名单"), L.T("仅白名单") },
            SelectedIndex = 0, MinHeight = 34, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.Black, Background = Brushes.White
        };
        panel.Children.Add(content);
        var description = Text(L.T("包含白名单、外观与列表设置。"), 12);
        content.SelectionChanged += (_, _) => description.Text = L.T(content.SelectedIndex == 1 ? "仅包含白名单规则，不包含语言、外观或其他设置。" : "包含白名单、外观与列表设置。");
        panel.Children.Add(description);
        panel.Children.Add(Text(L.T("导出用途")));
        var purpose = new ComboBox
        {
            Tag = "export-purpose", ItemsSource = new[] { L.T("分享给其他电脑（仅通用规则）"), L.T("完整本机备份（保留所有规则与路径）") },
            SelectedIndex = 0, MinHeight = 34, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.Black, Background = Brushes.White
        };
        panel.Children.Add(purpose);
        panel.Children.Add(Text(L.F($"分享模式保留 {RulesForExport(true).Count} 条通用规则；完整备份包含 {_rules.Count} 条规则。"), 12));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("取消"), Style = (Style)Resources[typeof(Button)], IsCancel = true, Tag = "export-cancel" };
        cancel.Click += (_, _) => window.DialogResult = false;
        var export = new Button { Content = L.T("导出"), Style = (Style)Resources[typeof(Button)], IsDefault = true, Tag = "export-confirm" };
        export.Click += (_, _) => window.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(export); panel.Children.Add(actions);
        window.Content = new ScrollViewer { Content = panel, Background = window.Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        await Task.Yield();
        return window.ShowDialog() == true ? new ExportOptions(content.SelectedIndex == 1, purpose.SelectedIndex == 0) : null;
    }
}
