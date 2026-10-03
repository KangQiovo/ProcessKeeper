using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private void InitializeSelectionTrees()
    {
        NativeSelectionTree.Attach(AppsList, row => ((AppRow)row).RowKey, RunningSelectionNodes, () => SelectionTreeChanged(AppsList));
        NativeSelectionTree.Attach(InstalledList, row => ((InstalledRow)row).RowKey, InstalledSelectionNodes, () => SelectionTreeChanged(InstalledList));
        NativeSelectionTree.Attach(RulesList, row => ((RuleRow)row).RowKey, RuleSelectionNodes, RefreshSelectionButtons);
    }

    private void InvalidateSelectionTrees()
    {
        foreach (var list in new[] { AppsList, InstalledList, RulesList })
            NativeSelectionTree.For(list)?.Invalidate(IsActiveList(list));
    }

    private void SelectionTreeChanged(Microsoft.UI.Xaml.Controls.ListView list)
    {
        RefreshSelectionButtons();
        if (list == AppsList && !_rendering && list.SelectedItems.OfType<AppRow>().LastOrDefault() is { } app)
        { _selectedKey = app.Key; _selectedRowKey = app.RowKey; _selectedProcessId = app.ProcessId; UpdateDetail(true); }
        else if (list == InstalledList && !_renderingInstalled && list.SelectedItems.OfType<InstalledRow>().LastOrDefault() is { } installed)
        { _installedSelectedRow = installed.RowKey; InstalledFacts.Text = installed.Details; }
    }

    private IReadOnlyList<NativeSelectionNode> RunningSelectionNodes()
    {
        var nodes = new List<NativeSelectionNode>();
        foreach (var app in _snapshot.Applications)
        {
            var platform = _groupGamePlatforms ? _displayCatalog.FindGamePlatform(app) : null;
            var parent = platform is null ? null : PlatformRowPrefix + platform.Id;
            if (parent is not null) nodes.Add(new(parent));
            nodes.Add(new(app.Key, parent));
            foreach (var process in app.Processes) nodes.Add(new($"{app.Key}|{process.Id}:{process.StartTimeUtcTicks}", app.Key));
        }
        return nodes;
    }

    private IReadOnlyList<NativeSelectionNode> InstalledSelectionNodes()
    {
        var nodes = new List<NativeSelectionNode>();
        var byPath = _snapshot.Processes.Where(process => process.Path.Length > 0).GroupBy(process => process.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var app in AllInstalledApplications())
        {
            var platform = _groupGamePlatforms ? _displayCatalog.FindGamePlatform(app) : null;
            var parent = platform is null ? null : PlatformRowPrefix + platform.Id;
            if (parent is not null) nodes.Add(new(parent));
            var key = "app:" + app.Id; nodes.Add(new(key, parent));
            foreach (var executable in app.Executables)
            {
                var component = InstalledExecutableRowKey(app.Id, executable.Path); nodes.Add(new(component, key));
                foreach (var process in byPath.GetValueOrDefault(executable.Path) ?? [])
                    nodes.Add(new($"pid:{app.Id}:{process.Id}:{process.StartTimeUtcTicks}", component));
            }
        }
        return nodes;
    }

    // Windows file identity is case-insensitive; preserve the application ID's own case semantics.
    private static string InstalledExecutableRowKey(string applicationId, string path) =>
        $"exe:{applicationId}:{path.ToUpperInvariant()}";

    private IReadOnlyList<NativeSelectionNode> RuleSelectionNodes()
    {
        var nodes = new List<NativeSelectionNode>(); var matcher = new RuleProcessMatcher(_snapshot);
        foreach (var group in ApplicationPresentationGroups.GroupRules(_rules, AllInstalledApplications(), _snapshot))
        {
            var parent = group.Rules.Count > 1 ? group.Key : null;
            if (parent is not null) nodes.Add(new(parent));
            foreach (var rule in group.Rules)
            {
                var key = "rule:" + rule.Id; nodes.Add(new(key, parent));
                foreach (var match in matcher.Match(rule)) nodes.Add(new($"rule:{rule.Id}|pid:{match.Process.Id}:{match.Process.StartTimeUtcTicks}", key));
            }
        }
        return nodes;
    }
}
