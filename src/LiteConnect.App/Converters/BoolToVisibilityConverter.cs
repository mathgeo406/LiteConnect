using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LiteConnect.App.Converters;

/// <summary>
/// Converts bool → Visibility.
/// Pass <c>ConverterParameter="inverse"</c> to flip the logic.
/// Also converts any non-zero integer (e.g. count) → Visible.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b   => b,
            int i    => i > 0,
            object o => o is not null,
            _        => false
        };

        bool inverse = parameter is string s && s.Equals("inverse", StringComparison.OrdinalIgnoreCase);
        return (flag ^ inverse) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Inverts a bool.</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
