using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VrcCompanion.ViewModels;

namespace VrcCompanion;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly MainViewModel _viewModel;
    private readonly bool _initializeOnLoad;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, ContentControl> _sectionViews = new(StringComparer.Ordinal);

    public MainWindow()
        : this(new MainViewModel(), initializeOnLoad: true)
    {
    }

    internal MainWindow(MainViewModel viewModel, bool initializeOnLoad)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _initializeOnLoad = initializeOnLoad;
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowSelectedSection();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (!_initializeOnLoad)
        {
            return;
        }

        await _viewModel.InitializeAsync();
        _refreshTimer.Start();

        if (!_viewModel.IsLoggedIn && !_viewModel.IsTwoFactorRequired)
        {
            UsernameBox.Focus();
        }
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!_viewModel.IsLoggedIn || _viewModel.IsBusy || WindowState == WindowState.Minimized || !IsVisible)
        {
            return;
        }

        // Background refresh only pulls what changes often; the full friend
        // download stays on startup and on the manual Refresh button.
        await _viewModel.RefreshInBackgroundAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsLaunchChooserOpen)
            {
                _viewModel.CancelLaunchChoiceCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.HasWorldDialog)
            {
                _viewModel.CloseWorldDialogCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.HasSelectedFriend)
            {
                _viewModel.CloseFriendDetailCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Username = UsernameBox.Text;

        await _viewModel.LoginAsync(_viewModel.Username, PasswordBox.Password);

        if (_viewModel.IsLoggedIn || _viewModel.IsTwoFactorRequired)
        {
            PasswordBox.Clear();
        }

        if (_viewModel.IsTwoFactorRequired)
        {
            TwoFactorBox.Focus();
            Keyboard.Focus(TwoFactorBox);
        }
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!_viewModel.IsBusy)
        {
            Login_Click(sender, e);
        }
    }

    private void UsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        PasswordBox.Focus();
    }

    private void TwoFactorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.VerifyTwoFactorCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        _viewModel.VerifyTwoFactorCommand.Execute(null);
    }

    private void BraveSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.BraveBrowserViewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.BraveBrowserViewModel.SearchCommand.Execute(null);
        }
    }

    private void DiscordSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.DiscordBrowserViewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.DiscordBrowserViewModel.SearchCommand.Execute(null);
        }
    }

    private void WorldSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.SearchWorldsCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        _viewModel.SearchWorldsCommand.Execute(null);
    }

    private void QuickJoinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.QuickJoinCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        _viewModel.QuickJoinCommand.Execute(null);
    }

    private void ForgotPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://vrchat.com/home/password")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedSection))
        {
            ShowSelectedSection();
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.IsTwoFactorRequired) || !_viewModel.IsTwoFactorRequired)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            TwoFactorBox.Focus();
            Keyboard.Focus(TwoFactorBox);
        }));
    }

    /// <summary>
    /// Builds a section once and keeps it in the visual tree. Hiding a cached
    /// view instead of swapping the ContentTemplate preserves WebView pages,
    /// active calls, search text, and scroll position across tab changes.
    /// </summary>
    private void ShowSelectedSection()
    {
        var key = _viewModel.SelectedSection.Key;
        if (!_sectionViews.TryGetValue(key, out var view))
        {
            var template = FindResource(TemplateKeyFor(key)) as DataTemplate
                ?? throw new InvalidOperationException($"No section template for '{key}'.");

            view = new ContentControl
            {
                Content = _viewModel,
                ContentTemplate = template,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Focusable = false,
                Visibility = Visibility.Collapsed
            };

            _sectionViews.Add(key, view);
            SectionHost.Children.Add(view);
        }

        foreach (var section in _sectionViews)
        {
            section.Value.Visibility = string.Equals(section.Key, key, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static string TemplateKeyFor(string key)
        => key switch
        {
            "friends" => "FriendsTemplate",
            "notifications" => "NotificationsTemplate",
            "worlds" => "WorldsTemplate",
            "settings" => "SettingsTemplate",
            "avatars" => "AvatarsTemplate",
            "favorites" => "FavoritesTemplate",
            "avatarsearch" => "AvatarSearchTemplate",
            "groups" => "GroupsTemplate",
            "resources" => "CreatorResourcesTemplate",
            "brave" => "BraveBrowserTemplate",
            "discord" => "DiscordBrowserTemplate",
            "activity" => "ActivityTemplate",
            "accounts" => "AccountsTemplate",
            "media" => "MediaTemplate",
            "chatbox" => "ChatboxTemplate",
            _ => "ProfileTemplate"
        };

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));

            // COLORREF values are 0x00BBGGRR; these match the app palette.
            var caption = ToColorRef(0x0B, 0x0D, 0x12);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));

            var text = ToColorRef(0xE9, 0xED, 0xF5);
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));

            var border = ToColorRef(0x23, 0x2B, 0x3B);
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));

            var corners = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corners, sizeof(int));
        }
        catch (Exception ex)
        {
            // These attributes are cosmetic and unavailable on older Windows builds.
            Debug.WriteLine(ex);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }

    private static int ToColorRef(byte red, byte green, byte blue)
        => red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
