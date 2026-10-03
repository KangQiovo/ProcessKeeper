using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ProcessKeeper.App;

/// <summary>Shows a selection checkbox only when the row's existing action checkbox is hidden.</summary>
public sealed class HiddenActionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
