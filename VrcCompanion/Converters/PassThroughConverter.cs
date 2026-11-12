using System;
using System.Globalization;
using System.Windows.Data;

namespace VrcCompanion.Converters;

/// <summary>
/// Passes the value through unchanged. Useful for binding to properties
/// that need a converter wrapper in XAML.
/// </summary>
public sealed class PassThroughConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value;
}