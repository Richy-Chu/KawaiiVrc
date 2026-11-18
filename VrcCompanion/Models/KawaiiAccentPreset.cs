using System.Windows.Media;

namespace VrcCompanion.Models;

public sealed record KawaiiAccentPreset(string Name, string Hex)
{
    public Brush Brush { get; } = CreateBrush(Hex);

    private static Brush CreateBrush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        }
        catch
        {
            return Brushes.MediumPurple;
        }
    }
}
