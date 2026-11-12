using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VrcCompanion.Converters;

/// <summary>
/// Converts a null value to Visibility.Visible and a non-null value to
/// Visibility.Collapsed. Useful for hiding a fallback (e.g. initials) when an
/// image is loaded. Use ConverterParameter="invert" to reverse the behavior.
/// </summary>
public sealed class ObjectNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value is null;
        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return (isNull ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
