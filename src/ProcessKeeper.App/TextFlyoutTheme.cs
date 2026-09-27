using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ProcessKeeper.App;

internal static class TextFlyoutTheme
{
    // WinUI caches the overflow popup of TextCommandBarFlyout outside the owner's
    // visual tree. Reusing it after a theme switch can retain the old text color.
    // Recreate only the built-in text flyouts; custom application menus are intact.
    internal static void Refresh(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is UIElement element && element.ContextFlyout is TextCommandBarFlyout context)
            { context.Hide(); element.ContextFlyout = new TextCommandBarFlyout(); }
            switch (node)
            {
                case TextBox box when box.SelectionFlyout is TextCommandBarFlyout selection:
                    selection.Hide(); box.SelectionFlyout = new TextCommandBarFlyout(); break;
                case TextBlock block when block.SelectionFlyout is TextCommandBarFlyout selection:
                    selection.Hide(); block.SelectionFlyout = new TextCommandBarFlyout(); break;
                case RichEditBox edit when edit.SelectionFlyout is TextCommandBarFlyout selection:
                    selection.Hide(); edit.SelectionFlyout = new TextCommandBarFlyout(); break;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                pending.Push(VisualTreeHelper.GetChild(node, index));
        }
    }
}
