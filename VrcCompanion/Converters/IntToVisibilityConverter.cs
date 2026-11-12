using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VrcCompanion.Converters;

/// <summary>
/// Converts an int to Visibility.
/// Format: "invert" - if present, inverts the logic (0 = Visible, >0 = Collapsed)
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        var count = System.Convert.ToInt32(value, CultureInfo.InvariantCulture);

        var isVisible = invert ? count == 0 : count > 0;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}