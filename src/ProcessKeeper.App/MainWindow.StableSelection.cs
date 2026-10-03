using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;

namespace ProcessKeeper.App;

internal static class NativeListSelection
{
    internal static void Move<T>(ListView list, ObservableCollection<T> rows, int from, int to, Func<T, string> key) where T : class
    {
        // WinUI removes a moved item's selection while processing its collection event.
        // Restore the keys synchronously, before another user input can change the selection.
        var selectedRows = list.SelectedItems.OfType<T>().ToArray();
        var selected = selectedRows.Select(key).ToHashSet(StringComparer.Ordinal);
        rows.Move(from, to);
        if (selected.Count == 0) return;
        Restore(list, selectedRows, selected, key);
    }

    internal static void Restore<T>(ListView list, IEnumerable<T> rows, HashSet<string> selected, Func<T, string> key) where T : class
    {
        var current = list.SelectedItems.OfType<T>().ToHashSet();
        foreach (var row in rows.Where(row => selected.Contains(key(row))))
            if (current.Add(row)) list.SelectedItems.Add(row);
    }
}
