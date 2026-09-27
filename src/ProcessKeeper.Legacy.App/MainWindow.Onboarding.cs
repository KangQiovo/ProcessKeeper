using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public partial class MainWindow
{
    private int _onboardingPage;
    private bool _reviewOnboarding;
    private Border OverlayCompatibilityNotice()
    {
        var notice = new Border { Background = new SolidColorBrush(Color.FromRgb(252, 230, 233)), Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 18), Visibility = CompatibilityNoticeState.ShouldShow(true) ? Visibility.Visible : Visibility.Collapsed };
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); notice.Child = grid;
        var text = new TextBlock { Text = L.T("当前使用兼容运行环境，部分效果和功能可能与新系统不同。"), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(176, 0, 20)), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(text);
        var close = new Button { Content = "×", Tag = "compatibility-close", Background = Brushes.Transparent, Foreground = text.Foreground, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Automation.AutomationProperties.SetName(close, L.T("关闭")); Grid.SetColumn(close, 1); grid.Children.Add(close);
        close.Click += (_, args) => { DismissCompatibility(close, args); notice.Visibility = Visibility.Collapsed; };
        return notice;
    }
    private void ShowPermission()
    {
        Overlay.Visibility = Visibility.Visible; Shell.IsEnabled = false; Overlay.Children.Clear();
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 580, Margin = new Thickness(32) };
        panel.Children.Add(Text("Process Keeper", 32)); panel.Children.Add(Text(L.T("需要管理员权限"), 24));
        panel.Children.Add(Text(L.T("请重新运行分享的 Process Keeper.exe，并在系统提示中允许管理员权限。")));
        panel.Children.Add(Button(L.T("退出"), Close)); Overlay.Children.Add(panel);
    }
    private void ShowOnboarding(bool review)
    {
        _onboardingPage = 0; _reviewOnboarding = review; Overlay.Visibility = Visibility.Visible; Shell.IsEnabled = false; RenderOnboarding();
    }
    private void RenderOnboarding()
    {
        Overlay.Children.Clear();
        var page = new Grid { Margin = new Thickness(42) }; page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(OverlayCompatibilityNotice());
        var content = new StackPanel { MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); page.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(0, 24, 0, 0) }; Grid.SetRow(footer, 2); page.Children.Add(footer);
        var progress = Text(($"{_onboardingPage + 1} / 5")); progress.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(progress);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions);
        if (_reviewOnboarding) actions.Children.Add(Button(L.T("返回应用"), CloseOnboarding));
        if (_onboardingPage > 0 && _onboardingPage < 4) actions.Children.Add(Button(L.T("返回"), () => { _onboardingPage--; RenderOnboarding(); }));
        var next = Button(L.T(_onboardingPage == 4 ? "开始使用" : "继续"), () => { if (_onboardingPage == 4) { try { if (!_reviewOnboarding) new OnboardingStore(_directory).Complete(); CloseOnboarding(); } catch (Exception ex) { Notice(ex.Message); } } else { _onboardingPage++; RenderOnboarding(); } }); actions.Children.Add(next);
        Overlay.Children.Add(page);
        if (_onboardingPage == 0)
        {
            next.IsEnabled = false; content.Children.Add(Text(L.T("运行环境检测"), 30)); var checking = Text(L.T("正在读取运行环境…"), 16); content.Children.Add(checking);
            _ = Check();
            async Task Check()
            {
                try
                {
                    var report = await Task.Run(_backend.CaptureEnvironment);
                    if (_closed || _onboardingPage != 0 || !Overlay.Children.Contains(page)) return;
                    checking.Text = report.Description;
                    if (!report.NecessaryConditionsPassed)
                    {
                        content.Children.Add(Button(L.T("重新检测"), RenderOnboarding));
                        content.Children.Add(Button("Microsoft .NET Framework 4.6.2", () => OpenWebsite("https://dotnet.microsoft.com/en-us/download/dotnet-framework/net462")));
                        content.Children.Add(Button("Windows 7 SP1", () => OpenWebsite("https://support.microsoft.com/en-us/windows/install-windows-7-service-pack-1-sp1-b3da2c0f-cdb6-0572-8596-bab972897f61")));
                        return;
                    }
                    await Task.Delay(450);
                    if (_closed || _onboardingPage != 0 || !Overlay.Children.Contains(page)) return;
                    content.Children.Clear(); content.Children.Add(Text("Process Keeper", 36)); content.Children.Add(Text(L.T("欢迎使用 Process Keeper"), 24)); content.Children.Add(Text(L.T("看清每个程序，让需要的留下。"), 17));
                    content.Children.Add(new Expander { Header = L.T("运行环境检测"), Content = Text(report.Description), Margin = new Thickness(0, 20, 0, 0) });
                    if (SystemParameters.ClientAreaAnimation) content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                    next.IsEnabled = true;
                }
                catch (Exception ex) { checking.Text = ex.Message; content.Children.Add(Button(L.T("重新检测"), RenderOnboarding)); }
            }
        }
        else if (_onboardingPage == 1)
        {
            content.Children.Add(Text(L.T("选择语言"), 30));
            string[] values = { "zh-Hans", "zh-Hant", "en" }; string[] names = { "简体中文", "繁體中文", "English" };
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i]; var card = new RadioButton { Content = names[i], GroupName = "OnboardingLanguages", IsChecked = L.Language == value, FontSize = 23, Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 72, Style = (Style)Resources["LanguageCardStyle"] };
                card.Checked += (_, _) => { _view = _view with { Language = value }; SaveView(); L.Language = value; ConfigureLanguage(); RenderOnboarding(); }; content.Children.Add(card);
            }
        }
        else if (_onboardingPage == 2) { content.Children.Add(Text(L.T("白名单"), 30)); content.Children.Add(Text(L.T("把需要的程序留下，规则由你决定。"), 18)); content.Children.Add(Text(L.T("搜索软件、进程、PID 或路径"))); }
        else if (_onboardingPage == 3) { content.Children.Add(Text(L.T("显示窗口"), 30)); content.Children.Add(Text(L.T("查看程序与进程关系，保留需要的程序。"), 18)); content.Children.Add(Text(L.T("重启前需要确认，命令行窗口不代表图形界面已打开。"))); }
        else { content.Children.Add(Text(L.T("准备就绪"), 30)); content.Children.Add(Text(L.T("请先保存工作。"), 18)); if (BuildInfo.IsPreviewBuild) content.Children.Add(Text(L.T("测试版本不代表最终品质"))); }
    }
    private void CloseOnboarding() { Overlay.Children.Clear(); Overlay.Visibility = Visibility.Collapsed; Shell.IsEnabled = true; ConfigureLanguage(); _ = RenderAsync(); }
}
