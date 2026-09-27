using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed class PresentationOptions : UserControl
{
    private readonly Grid _layout = new() { ColumnSpacing = 18, RowSpacing = 4 };
    private readonly CheckBox _group = new() { Content = L.T("按游戏平台分组"), MinWidth = 0, FontSize = 12 };
    private readonly CheckBox _hide = new() { Content = L.T("隐藏 Microsoft 应用"), MinWidth = 0, FontSize = 12 };
    private bool _applying;
    public event EventHandler? Changed;
    public bool GroupGames => _group.IsChecked == true;
    public bool HideMicrosoft => _hide.IsChecked == true;

    public PresentationOptions()
    {
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_hide, 1);
        _layout.Children.Add(_group); _layout.Children.Add(_hide); Content = _layout;
        ToolTipService.SetToolTip(_group, L.T("仅整理显示；关闭与白名单操作仍只针对原应用。"));
        ToolTipService.SetToolTip(_hide, L.T("仅隐藏已核实的 Microsoft 应用；未知身份仍显示。"));
        _group.Click += ChangedByUser; _hide.Click += ChangedByUser;
        SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 470;
            Grid.SetRow(_hide, compact ? 1 : 0); Grid.SetColumn(_hide, compact ? 0 : 1);
            Grid.SetColumnSpan(_group, compact ? 2 : 1); Grid.SetColumnSpan(_hide, compact ? 2 : 1);
        };
    }
    public void Apply(bool groupGames, bool hideMicrosoft)
    {
        _applying = true;
        try { _group.IsChecked = groupGames; _hide.IsChecked = hideMicrosoft; }
        finally { _applying = false; }
    }
    private void ChangedByUser(object sender, RoutedEventArgs args) { if (!_applying) Changed?.Invoke(this, EventArgs.Empty); }
}
