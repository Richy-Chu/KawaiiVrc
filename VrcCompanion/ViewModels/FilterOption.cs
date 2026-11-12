namespace VrcCompanion.ViewModels;

/// <summary>A small chip used for friend and notification filters.</summary>
public sealed class FilterOption
{
    public FilterOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }
}
