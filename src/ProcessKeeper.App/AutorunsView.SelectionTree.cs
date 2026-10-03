using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class AutorunsView
{
    private IReadOnlyList<NativeSelectionNode> SelectionNodes()
    {
        var nodes = new List<NativeSelectionNode>(); var index = new AutorunSearchIndex(_processes, _installed);
        foreach (var group in ApplicationPresentationGroups.GroupAutoruns(_inventory.Entries, _installed, _processes))
        {
            var platforms = group.Entries.Select(_displayCatalog.FindGamePlatform).ToArray();
            var platform = GroupGamePlatforms && platforms.Length > 0 && platforms.All(item => item is not null && item.Id == platforms[0]?.Id)
                ? platforms[0] : null;
            var parent = platform is null ? null : PlatformRowPrefix + platform.Id;
            if (parent is not null) nodes.Add(new(parent));
            if (group.Entries.Count > 1) { nodes.Add(new(group.Key, parent)); parent = group.Key; }
            foreach (var entry in group.Entries)
            {
                nodes.Add(new(entry.Id, parent));
                foreach (var process in CreateRow(entry, index, true, "", false).Processes)
                    nodes.Add(new($"{entry.Id}|pid:{process.Id}:{process.StartTimeUtcTicks}", entry.Id));
            }
        }
        return nodes;
    }
}
