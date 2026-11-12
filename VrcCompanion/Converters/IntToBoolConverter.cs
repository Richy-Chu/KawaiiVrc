using System;
using System.Globalization;
using System.Windows.Data;

namespace VrcCompanion.Converters;

/// <summary>
/// Converts an int to bool based on a threshold parameter.
/// Format: "threshold" - returns true if value &gt; threshold
/// </summary>
public sealed class IntToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int intValue || parameter is not string thresholdStr)
            return false;

        if (int.TryParse(thresholdStr, out var threshold))
            return intValue > threshold;

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}