using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VrcCompanion.Converters;

/// <summary>
/// Converts a string to Visibility: Visible when the string is null or empty,
/// Collapsed otherwise. Useful for showing a fallback (e.g. initials) when
/// no image URL is available.
/// </summary>
public sealed class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
