using System.ComponentModel;

namespace ProcessKeeper.Core;

/// <summary>Publishes only changed bound values; a periodic text update must not rebind images or row actions.</summary>
public sealed class PresentationChanges
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    public void Publish(object sender, PropertyChangedEventHandler? handler, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (_values.TryGetValue(name, out var previous) && Equals(previous, value)) continue;
            _values[name] = value;
            handler?.Invoke(sender, new PropertyChangedEventArgs(name));
        }
    }
}
