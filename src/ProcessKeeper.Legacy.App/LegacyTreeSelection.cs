namespace ProcessKeeper.App;

/// <summary>Selection is stored by leaf identity, so collapsing a native list never drops children.</summary>
internal sealed class LegacyTreeSelection
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _families = new(StringComparer.Ordinal);
    private Dictionary<string, string[]> _members = new(StringComparer.Ordinal);
    public void Configure(Dictionary<string, string[]> members)
    {
        _members = members;
        _families.RemoveWhere(key => !members.ContainsKey(key));
        _selected.IntersectWith(members.Values.SelectMany(keys => keys));
        foreach (var key in _families) _selected.UnionWith(members[key]);
    }
    public bool? State(string key)
    {
        if (!_members.TryGetValue(key, out var members) || members.Length == 0) return false;
        int count = members.Count(_selected.Contains);
        return count == 0 ? false : count == members.Length ? true : null;
    }
    public void Select(string key, bool selected)
    {
        if (!_members.TryGetValue(key, out var members)) return;
        if (selected)
        {
            _selected.UnionWith(members);
            if (members.Length > 1 || !members.Contains(key, StringComparer.Ordinal)) _families.Add(key);
        }
        else
        {
            _selected.ExceptWith(members);
            var removed = new HashSet<string>(members, StringComparer.Ordinal);
            _families.RemoveWhere(family => family == key || _members[family].Any(removed.Contains));
        }
    }
    public void Clear() { _selected.Clear(); _families.Clear(); }
    public static Dictionary<string, string[]> Membership(IReadOnlyList<LegacyRow> rows)
    {
        var byId = rows.GroupBy(row => row.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var parents = rows.Where(row => !row.IsChild && !row.IsPresentationGroup).OrderByDescending(row => row.Id.Length).ToArray();
        var children = byId.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var row in byId.Values)
        {
            string? parent = row.IsChild ? parents.FirstOrDefault(item => row.Id.StartsWith(item.Id + "|", StringComparison.Ordinal))?.Id :
                !row.IsPresentationGroup && row.PresentationPlatformId.Length > 0 ? "presentation-platform:" + row.PresentationPlatformId : null;
            if (parent is not null && children.TryGetValue(parent, out var descendants)) descendants.Add(row.Id);
        }
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] Leaves(string key)
        {
            if (result.TryGetValue(key, out var existing)) return existing;
            return result[key] = children[key].Count == 0 ? new[] { key } : children[key].SelectMany(Leaves).Distinct(StringComparer.Ordinal).ToArray();
        }
        foreach (var key in byId.Keys) Leaves(key);
        return result;
    }
}
