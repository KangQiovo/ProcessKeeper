using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class AutorunsView
{
    private IReadOnlyList<NativeSelectionNode> SelectionNodes()
    {
        var nodes = new List<NativeSelectionNode>(); var index = new AutorunSearchIndex(_processes, _installed);
        foreach (var entry in _inventory.Entries)
        {
            var platform = GroupGamePlatforms ? _displayCatalog.FindGamePlatform(entry) : null;
            var parent = platform is null ? null : PlatformRowPrefix + platform.Id;
            if (parent is not null) nodes.Add(new(parent));
            nodes.Add(new(entry.Id, parent));
            foreach (var process in CreateRow(entry, index, true, "", false).Processes)
                nodes.Add(new($"{entry.Id}|pid:{process.Id}:{process.StartTimeUtcTicks}", entry.Id));
        }
        return nodes;
    }
}
