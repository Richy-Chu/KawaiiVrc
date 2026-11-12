namespace VrcCompanion.ViewModels;

public sealed class NavItem : ViewModelBase
{
    private string _badge = string.Empty;

    public NavItem(string key, string title, string glyph)
    {
        Key = key;
        Title = title;
        Glyph = glyph;
    }

    public string Key { get; }

    public string Title { get; }

    public string Glyph { get; }

    public string Badge
    {
        get => _badge;
        set
        {
            if (SetProperty(ref _badge, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);
}
