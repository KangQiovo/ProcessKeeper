using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ProcessKeeper.App;

/// <summary>Uses a normal native checkbox where a row has no Keep or Enable checkbox.</summary>
public static class RowSelectionCheckBox
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RowSelectionCheckBox), new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);

    private static void EnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not CheckBox checkbox) return;
        checkbox.Loaded -= Loaded;
        checkbox.Unloaded -= Unloaded;
        checkbox.Checked -= Changed; checkbox.Unchecked -= Changed;
        if (args.NewValue is true)
        {
            checkbox.Loaded += Loaded;
            checkbox.Unloaded += Unloaded;
            checkbox.Checked += Changed; checkbox.Unchecked += Changed;
            if (checkbox.IsLoaded) Bind(checkbox);
        }
        else Unregister(checkbox);
    }

    private static void Loaded(object sender, RoutedEventArgs args) => Bind((CheckBox)sender);
    private static void Unloaded(object sender, RoutedEventArgs args) => Unregister((CheckBox)sender);
    private static void Changed(object sender, RoutedEventArgs args)
    { if (sender is CheckBox checkbox && checkbox.Tag is NativeSelectionTree tree) tree.CheckboxChanged(checkbox); }
    private static void Unregister(CheckBox checkbox)
    { if (checkbox.Tag is NativeSelectionTree tree) tree.Unregister(checkbox); checkbox.Tag = null; }

    private static void Bind(CheckBox checkbox)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(checkbox); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not ListViewItem container) continue;
            for (DependencyObject? parent = VisualTreeHelper.GetParent(container); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not ListView list || NativeSelectionTree.For(list) is not { } tree) continue;
                Unregister(checkbox); checkbox.Tag = tree; tree.Register(checkbox, container); break;
            }
            return;
        }
    }
}
