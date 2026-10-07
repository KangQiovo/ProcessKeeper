using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ProcessKeeper.App;

/// <summary>Row bodies invoke expansion; selection remains an explicit checkbox operation.</summary>
public sealed class ExpansionListView : ListView
{
    internal event Action<object>? RowInvoked;

    public ExpansionListView() => DefaultStyleKey = typeof(ListView);

    protected override DependencyObject GetContainerForItemOverride() => new ExpansionListViewItem(this);

    private sealed class ExpansionListViewItem : ListViewItem
    {
        private readonly ExpansionListView _owner;

        internal ExpansionListViewItem(ExpansionListView owner)
        {
            _owner = owner;
            // Selectable description text can consume the routed tap. Row behavior must
            // still apply there, while interactive controls keep their own gesture.
            AddHandler(TappedEvent, new TappedEventHandler(RowTapped), true);
            AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(RowDoubleTapped), true);
        }
        protected override void OnPointerPressed(PointerRoutedEventArgs args)
        {
            // Skip ListViewItem's native selection gesture, including container padding.
            // Child controls receive their input before it bubbles here.
            args.Handled = true;
        }

        protected override void OnPointerReleased(PointerRoutedEventArgs args) => args.Handled = true;

        private void RowTapped(object sender, TappedRoutedEventArgs args)
        {
            args.Handled = true;
            InvokeRow(args.OriginalSource as DependencyObject);
        }

        private void RowDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
        {
            // WinUI routes the second rapid primary click here instead of Tapped.
            args.Handled = true;
            InvokeRow(args.OriginalSource as DependencyObject);
        }

        private void InvokeRow(DependencyObject? originalSource)
        {
            for (var source = originalSource; source is not null && source != this;
                 source = VisualTreeHelper.GetParent(source))
                if (source is ButtonBase or TextBox or ComboBox or MenuFlyoutPresenter or ScrollBar) return;
            if (Content is { } row) _owner.RowInvoked?.Invoke(row);
        }
    }
}
