using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly HashSet<string> _searchCollapsedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _searchCollapsedRules = new(StringComparer.Ordinal);

    private void InitializeSearch()
    {
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            if (_closed) return;
            if (AppsPage.Visibility == Visibility.Visible) RenderApps();
            if (RulesPage.Visibility == Visibility.Visible) RenderRules();
        };
        Closed += (_, _) => _searchDelay.Stop();
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready) return;
        _searchCollapsedApps.Clear();
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void RulesSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready) return;
        _searchCollapsedRules.Clear();
        _ruleRevision++;
        _searchDelay.Stop();
        _searchDelay.Start();
    }
}
