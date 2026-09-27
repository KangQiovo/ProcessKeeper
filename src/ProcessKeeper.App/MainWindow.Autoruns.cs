using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private AutorunsView? _autorunsView;

    private void InitializeAutorunsPage()
    {
        _autorunsView = new AutorunsView();
        _autorunsView.Connect(_icons, Log, OpenRowLocation, ShowNotice, ShowDialog);
        AutorunsPage.Children.Add(_autorunsView);
    }
}
