using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class UninstallView
{
    private IReadOnlyList<NativeSelectionNode> SelectionNodes()
    {
        var nodes = new List<NativeSelectionNode>();
        foreach (var group in _groupCache ?? UninstallDisplay.Group(_snapshot.Entries))
        foreach (var segment in group.Entries.GroupBy(entry => GroupGamePlatforms ? _displayCatalog.FindGamePlatform(entry)?.Id : null))
        {
            var platform = segment.Key is null ? null : "group:platform:" + segment.Key;
            if (platform is not null) nodes.Add(new(platform));
            var parent = group.Entries.Count > 1 ? "group:" + (segment.Key ?? "") + "|" + group.Key : platform;
            if (group.Entries.Count > 1) nodes.Add(new(parent!, platform));
            foreach (var entry in segment) nodes.Add(new("entry:" + entry.Id + "|" + entry.Registration + "|" + entry.Fingerprint, parent));
        }
        return nodes;
    }
}
