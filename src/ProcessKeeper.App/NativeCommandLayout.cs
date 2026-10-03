using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProcessKeeper.App;

/// <summary>Arranges existing native buttons into native StackPanel rows at their natural widths.</summary>
internal static class NativeCommandLayout
{
    private sealed class State
    {
        internal UIElement[] Items = Array.Empty<UIElement>();
        internal string Rows = "";
        internal bool Arranging;
    }
    private static readonly ConditionalWeakTable<StackPanel, State> States = new();
    internal static void Reflow(StackPanel host)
    {
        if (host.ActualWidth <= 0) return;
        var state = States.GetValue(host, _ => new State());
        if (state.Arranging) return;
        state.Arranging = true;
        try
        {
            if (state.Items.Length == 0) state.Items = host.Children.OfType<Control>().Cast<UIElement>().ToArray();
            if (state.Items.Length == 0) return;
            var groups = new List<List<UIElement>>();
            var row = new List<UIElement>();
            var used = 0d;
            var boundaries = new List<int> { 0 };
            for (var index = 0; index < state.Items.Length; index++)
            {
                var item = state.Items[index];
                item.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var width = Math.Ceiling(item.DesiredSize.Width);
                if (row.Count > 0 && used + width > host.ActualWidth)
                { groups.Add(row); row = new(); used = 0; boundaries.Add(index); }
                row.Add(item); used += width;
            }
            groups.Add(row);
            var signature = string.Join(",", boundaries);
            if (state.Rows == signature) return;
            // Only crossing a wrap boundary reparents controls. Ordinary list refreshes
            // retain the same buttons, focus and native theme resources.
            foreach (var item in state.Items)
                if (item is FrameworkElement { Parent: Panel parent }) parent.Children.Remove(item);
            host.Children.Clear();
            foreach (var group in groups)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var item in group) panel.Children.Add(item);
                host.Children.Add(panel);
            }
            state.Rows = signature;
        }
        finally { state.Arranging = false; }
    }
}
