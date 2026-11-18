using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Converters;

/// <summary>
/// Colors a player name by trust rank. Values are [tags, developerType]; when
/// tags are unavailable the normal text color is used so old saved rows do not
/// look like Visitors.
/// </summary>
public sealed class TrustNameBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 0 && values[0] is IEnumerable<string> tags)
        {
            var developerType = values.Length > 1 ? values[1] as string : null;
            return UserPresentation.TrustBrush(tags, developerType);
        }

        return UserPresentation.DefaultNameBrush;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
