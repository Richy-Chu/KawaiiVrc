using System;
using System.Globalization;
using System.Windows.Data;

namespace VrcCompanion.Converters;

/// <summary>
/// Converts a boolean to one of two text values (e.g., "★" / "☆").
/// Format: "trueValue|falseValue"
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter is not string format || !format.Contains('|'))
            return value;

        var parts = format.Split('|');
        var isTrue = value is bool b && b;
        return isTrue ? parts[0] : parts[1];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}