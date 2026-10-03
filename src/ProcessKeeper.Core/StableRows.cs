using System.Collections.ObjectModel;

namespace ProcessKeeper.Core;

/// <summary>Keeps row objects and selection containers alive when a new display snapshot arrives.</summary>
public static class StableRows
{
    public static void Apply<T>(ObservableCollection<T> current, IReadOnlyList<T> incoming, Func<T, string> key,
        Action<T, T> update, IEqualityComparer<string>? comparer = null) where T : class
    {
        comparer ??= StringComparer.Ordinal;
        var desired = new HashSet<string>(incoming.Select(key), comparer);
        for (int i = current.Count - 1; i >= 0; i--) if (!desired.Contains(key(current[i]))) current.RemoveAt(i);
        var existing = current.ToDictionary(key, comparer);
        for (int i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i];
            if (existing.TryGetValue(key(next), out var row))
            {
                if (!ReferenceEquals(current[i], row)) current.Move(current.IndexOf(row), i);
                update(row, next);
            }
            else current.Insert(i, next);
        }
    }
}
