using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ProcessKeeper.App;

/// <summary>Native row selection is a projection of explicit checkbox choices.</summary>
internal static class LegacyRowInteraction
{
    internal static void Attach(ListBox list) => list.PreviewMouseDown += (_, args) =>
    {
        var container = Container(list, args.OriginalSource as DependencyObject);
        if (container is not null && (args.ChangedButton == MouseButton.Right ||
            args.ChangedButton == MouseButton.Left && !IsControl(args.OriginalSource as DependencyObject, container)))
            args.Handled = true;
    };

    internal static ListBoxItem? BodyContainer(ListBox list, MouseButtonEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        var container = Container(list, source);
        return container is not null && !IsControl(source, container) ? container : null;
    }

    private static ListBoxItem? Container(ListBox list, DependencyObject? source) =>
        source is null ? null : ItemsControl.ContainerFromElement(list, source) as ListBoxItem;

    private static bool IsControl(DependencyObject? source, ListBoxItem container)
    {
        while (source is not null && !ReferenceEquals(source, container))
        {
            if (source is ButtonBase or MenuItem or Hyperlink or TextBoxBase) return true;
            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) :
                source is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
