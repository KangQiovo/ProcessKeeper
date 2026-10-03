using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ProcessKeeper.App;

public sealed class RoleKindVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (parameter?.ToString() ?? "").Split('|').Contains(value?.ToString(), StringComparer.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
