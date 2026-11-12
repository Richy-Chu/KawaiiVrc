using System.Windows.Media;

namespace VrcCompanion.ViewModels;

public sealed class StatusOption
{
    public StatusOption(string value, string label, string color)
    {
        Value = value;
        Label = label;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Brush = brush;
    }

    public string Value { get; }

    public string Label { get; }

    public Brush Brush { get; }
}
