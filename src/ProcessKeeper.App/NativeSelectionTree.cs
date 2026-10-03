using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProcessKeeper.App;

internal sealed record NativeSelectionNode(string Key, string? ParentKey = null, bool HasSelector = true);

/// <summary>Remembers logical child selections while native virtualized rows are collapsed.</summary>
internal sealed class NativeSelectionTree
{
    private static readonly ConditionalWeakTable<ListView, NativeSelectionTree> Trees = new();
    private readonly ListView _list;
    private readonly Func<object, string> _key;
    private readonly Func<IReadOnlyList<NativeSelectionNode>> _capture;
    private readonly Action? _changed;
    private readonly Dictionary<CheckBox, ListViewItem> _boxes = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedFamilies = new(StringComparer.Ordinal);
    private Dictionary<string, NativeSelectionNode> _nodes = new(StringComparer.Ordinal);
    private Dictionary<string, string[]> _children = new(StringComparer.Ordinal);
    private bool _applying, _queued, _updatingBoxes, _dirty = true;

    internal bool IsApplying => _applying;

    private NativeSelectionTree(ListView list, Func<object, string> key, Func<IReadOnlyList<NativeSelectionNode>> capture, Action? changed)
    {
        _list = list; _key = key; _capture = capture; _changed = changed;
        list.SelectionChanged += SelectionChanged;
        list.Items.VectorChanged += (_, _) => { _dirty = true; QueueRefresh(); };
        // WinUI can reuse an already-loaded template for another row without raising Loaded again.
        // Refresh its selection state after reuse; the logical inventory itself has not changed.
        list.ContainerContentChanging += (_, args) => { if (!args.InRecycleQueue) QueueRefresh(); };
    }

    internal static void Attach(ListView list, Func<object, string> key, Func<IReadOnlyList<NativeSelectionNode>> capture, Action? changed = null)
    { if (!Trees.TryGetValue(list, out _)) Trees.Add(list, new NativeSelectionTree(list, key, capture, changed)); }

    internal static NativeSelectionTree? For(ListView list) => Trees.TryGetValue(list, out var tree) ? tree : null;

    internal void Register(CheckBox checkbox, ListViewItem container)
    {
        _boxes[checkbox] = container;
        if (_applying) { QueueRefresh(); return; }
        Capture();
        var wasUpdating = _updatingBoxes;
        _updatingBoxes = true;
        try { if (container.Content is { } row) checkbox.IsChecked = State(_key(row)); }
        finally { _updatingBoxes = wasUpdating; }
        QueueRefresh();
    }
    internal void Unregister(CheckBox checkbox) => _boxes.Remove(checkbox);
    internal bool IsSelected(string key) { Capture(); return _selected.Contains(key); }

    internal void CheckboxChanged(CheckBox checkbox)
    {
        if (_applying || _updatingBoxes || !_boxes.TryGetValue(checkbox, out var container) || container.Content is null || !_list.Items.Contains(container.Content)) return;
        Capture();
        ChangeFamily(_key(container.Content), checkbox.IsChecked == true);
        Apply();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_applying) return;
        Capture();
        foreach (var row in args.RemovedItems)
        {
            // A collapsed or moved native item can leave SelectedItems without being deselected by the user.
            if (!_list.Items.Contains(row)) continue;
            var key = _key(row); _selected.Remove(key); RemoveFamilyPolicies(key);
            if (_nodes.TryGetValue(key, out var node) && node.HasSelector && _children.ContainsKey(key)) ChangeFamily(key, false);
        }
        foreach (var row in args.AddedItems)
        {
            var key = _key(row); _selected.Add(key);
            if (_nodes.TryGetValue(key, out var node) && node.HasSelector && _children.ContainsKey(key)) ChangeFamily(key, true);
        }
        QueueRefresh();
    }

    private void ChangeFamily(string key, bool selected)
    {
        RemoveFamilyPolicies(key);
        foreach (var member in Family(key)) { if (selected) _selected.Add(member); else _selected.Remove(member); }
        if (selected && _children.ContainsKey(key)) _selectedFamilies.Add(key);
    }

    private void RemoveFamilyPolicies(string key)
    {
        foreach (var policy in _selectedFamilies.Where(parent => Family(parent).Contains(key) || Family(key).Contains(parent)).ToArray())
            _selectedFamilies.Remove(policy);
    }

    private IEnumerable<string> Family(string key)
    {
        var pending = new Stack<string>(); var visited = new HashSet<string>(StringComparer.Ordinal); pending.Push(key);
        while (pending.Count > 0)
        {
            var next = pending.Pop(); if (!visited.Add(next)) continue; yield return next;
            if (_children.TryGetValue(next, out var children)) foreach (var child in children) pending.Push(child);
        }
    }

    private bool? State(string key)
    {
        if (!_children.ContainsKey(key)) return _selected.Contains(key);
        var leaves = Family(key).Where(member => !_children.ContainsKey(member)).ToArray();
        int selected = leaves.Count(_selected.Contains);
        return selected == 0 ? false : selected == leaves.Length ? true : null;
    }

    private void Capture()
    {
        if (!_dirty) return;
        _dirty = false;
        // Include current rows even during an inventory transition; no guessed executable/PID relationships.
        var captured = _capture().GroupBy(node => node.Key, StringComparer.Ordinal).Select(group => group.Last()).ToDictionary(node => node.Key, StringComparer.Ordinal);
        foreach (var row in _list.Items) { var key = _key(row); if (!captured.ContainsKey(key)) captured[key] = new NativeSelectionNode(key); }
        _nodes = captured;
        _children = _nodes.Values.Where(node => node.ParentKey is not null && _nodes.ContainsKey(node.ParentKey)).GroupBy(node => node.ParentKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(node => node.Key).ToArray(), StringComparer.Ordinal);
        _selected.RemoveWhere(key => !_nodes.ContainsKey(key)); _selectedFamilies.RemoveWhere(key => !_nodes.ContainsKey(key));
    }

    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { _queued = false; if (_list.IsLoaded) Refresh(); });
    }

    internal void Refresh() { if (_applying) return; Capture(); Apply(); }
    internal void Invalidate(bool schedule = true) { _dirty = true; if (schedule && _list.IsLoaded) QueueRefresh(); }

    private void Apply()
    {
        bool changed = false;
        _applying = true;
        try
        {
            foreach (var parent in _selectedFamilies.ToArray()) foreach (var member in Family(parent)) _selected.Add(member);
            foreach (var parent in _nodes.Values.Where(node => node.HasSelector && _children.ContainsKey(node.Key)))
            { if (State(parent.Key) == true) _selected.Add(parent.Key); else _selected.Remove(parent.Key); }
            foreach (var row in _list.Items)
            {
                bool desired = _selected.Contains(_key(row)), current = _list.SelectedItems.Contains(row);
                if (desired && !current) { _list.SelectedItems.Add(row); changed = true; }
                else if (!desired && current) { _list.SelectedItems.Remove(row); changed = true; }
            }
            _updatingBoxes = true;
            try { foreach (var box in _boxes.ToArray()) if (box.Value.Content is { } row) box.Key.IsChecked = State(_key(row)); }
            finally { _updatingBoxes = false; }
        }
        finally { _applying = false; }
        if (changed) _changed?.Invoke();
    }
}
