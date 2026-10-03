using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProcessKeeper.App;

public sealed class RoleKindVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (parameter?.ToString() ?? "").Split('|').Contains(value?.ToString(), StringComparer.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
