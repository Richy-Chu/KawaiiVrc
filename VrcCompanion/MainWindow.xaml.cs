using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Behaviors;
using VrcCompanion.Controls;
using VrcCompanion.Services;
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
    private readonly DispatcherTimer _backgroundTrimTimer;
    private readonly UiStallMonitor _stallMonitor;
    private readonly Dictionary<string, ContentControl> _sectionViews = new(StringComparer.Ordinal);
    private static readonly TimeSpan SectionDataGrace = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The avatar library is the heaviest tab by a wide margin: its rows hold
    /// every decoded avatar image, which measured ~230 MB on a first visit. It
    /// is handed back as soon as the tab is left instead of waiting out the
    /// grace period the cheaper tabs get.
    /// </summary>
    private static readonly TimeSpan AvatarDataGrace = TimeSpan.Zero;

    /// <summary>
    /// The picture library is the other heavy tab: the scan's rows, the metadata
    /// read out of every file and the viewer's full-size decode came to ~600 MB
    /// on a large library, and the scan's 256 KB/512 KB buffers are the garbage
    /// a tab switch has to give back. The index is cached on disk, so returning
    /// still paints immediately.
    /// </summary>
    private static readonly TimeSpan MediaDataGrace = TimeSpan.Zero;

    private string _shownSectionKey = string.Empty;
    private readonly Dictionary<string, CancellationTokenSource> _pendingReleases = new(StringComparer.Ordinal);
    private bool _settleScheduled;
    private int _settleCount;
    private readonly List<string> _sectionOrder = new();
    private readonly HashSet<string> _themeBrushKeys = new();
    // Every cached section view keeps its whole visual tree, item containers
    // and decoded images alive. Two is enough to keep back-and-forth snappy
    // without paying for tabs visited long ago.
    /// <summary>
    /// One page is enough on a machine that chose lite mode, where the point is to hold as
    /// little as possible; the normal limit keeps two so stepping back and forth stays instant.
    /// </summary>
    private static int MaxCachedSectionViews => AppPerformance.LiteMode ? 1 : 2;
    private bool _isClosed;

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
        _viewModel.ChatboxViewModel.PropertyChanged += OnChatboxPropertyChanged;
        ApplyKawaiiTheme();
        ShowSelectedSection();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;

        // WPF keeps a lot of native render memory resident even when the window
        // is inactive. Let the OS page it out after the app has been in the
        // background for a while; it pages back in on the next activation.
        _backgroundTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _backgroundTrimTimer.Tick += OnBackgroundTrimTick;
        AppPerformance.Changed += OnPerformanceChanged;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnWindowClosed;
        PreviewMouseWheel += OnPreviewMouseWheel;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
        StateChanged += OnWindowStateChanged;

        // A background-priority timer that only ticks when the dispatcher is
        // free: the gap between ticks is exactly the time the UI could not
        // respond. Started before the window shows so startup freezes are
        // recorded too, and quiet while minimised where none of it is visible.
        // A job that takes long enough to feel is worth a line of its own: the stall line names
        // what was in flight, and this names which of them was doing the work.
        UiJobTrace.Shared.OnSlowJob = (name, milliseconds) =>
            LifecycleLog.Write($"ui job {name} {milliseconds}ms");

        _stallMonitor = new UiStallMonitor(
            stall =>
            {
                // Naming the job in flight is the part of a stall that can be acted on. Without
                // it the line only says the UI stopped, and the next freeze repeats the mystery.
                // The thread's own processor time says whether it was burning CPU or waiting,
                // which tells the next investigation where to look before anything is guessed.
                var during = UiJobTrace.Shared.Describe(stall.StartedTicks, stall.EndedTicks);
                var cpu = UiStallMonitor.DescribeCpu(stall.Milliseconds, stall.CpuMilliseconds);
                LifecycleLog.Write(
                    during is null
                        ? $"ui stall {stall.Milliseconds}ms {cpu}"
                        : $"ui stall {stall.Milliseconds}ms {cpu} across {during}");
            },
            () => WindowState == WindowState.Minimized);
        _stallMonitor.Start();

        LifecycleLog.Write($"window-ready {Program.Startup.ElapsedMilliseconds}ms");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (!_initializeOnLoad)
        {
            return;
        }

        LifecycleLog.Write($"window-loaded {Program.Startup.ElapsedMilliseconds}ms");
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
        try
        {
            await _viewModel.RefreshInBackgroundAsync();
        }
        catch (Exception ex)
        {
            // Nobody asked for this tick and the next one is five minutes away, so a failed
            // refresh is logged rather than reported: the manual Refresh shows its own errors.
            LifecycleLog.Write($"refresh: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings the window back after a second launch handed over: restore it first, because
    /// activating a minimized window changes nothing, then raise it above whatever had focus.
    /// </summary>
    internal void BringToFront()
    {
        if (_isClosed)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();

        // Windows can refuse the foreground to a process the user did not just start, and
        // Activate alone then only flashes the taskbar. The momentary topmost raise is what
        // actually brings the window up, and clearing the flag again keeps it from staying above
        // other windows.
        Topmost = true;
        Topmost = false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TryHandleBrowserFocusShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            // The sign-out card is the topmost overlay; Escape cancels it and
            // never signs out. `ConfirmLogoutCommand` is the only sign-out.
            if (_viewModel.IsLogoutConfirmationOpen)
            {
                _viewModel.CancelLogoutCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.IsInviteComposerOpen)
            {
                _viewModel.CancelInviteComposerCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.IsResponseComposerOpen)
            {
                _viewModel.CancelResponseComposerCommand.Execute(null);
                e.Handled = true;
                return;
            }

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

            // These three fill the window with a backdrop but were missing from
            // the chain, so Escape did nothing to them. In z-order: recent players
            // (25), friend changes (24), friend locations (23); one press closes
            // the popup on top.
            if (_viewModel.RecentPlayersViewModel is { IsOpen: true })
            {
                _viewModel.CloseRecentPlayersCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.IsFriendChangesOpen)
            {
                _viewModel.CloseFriendChangesCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.IsFriendLocationsOpen)
            {
                _viewModel.CloseFriendLocationsCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (FindVisibleBrowser() is { } focusedBrowser &&
                focusedBrowser.TryCancelAddressEdit())
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.ExitBrowserFocus())
            {
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    private void ChatboxMessageBox_KeyDown(object sender, KeyEventArgs e)
    {
        ChatboxComposer_KeyDown(sender, e);
    }

    private void ChatboxTab_Click(object sender, RoutedEventArgs e)
    {
        var current = sender as DependencyObject;
        while (current is not null && current is not ScrollViewer)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        (current as ScrollViewer)?.ScrollToTop();
    }

    private void RecentPlayersList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox list || !list.IsVisible)
        {
            return;
        }

        // The virtualizing list can retain its offset between popup visits.
        // Bring the first person back into view, otherwise their header clips.
        list.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (list.Items.Count > 0)
                {
                    list.ScrollIntoView(list.Items[0]);
                }

                RequestVisibleRecentAvatars(list);
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RecentPlayersList_Loaded(object sender, RoutedEventArgs e)
    {
        RequestVisibleRecentAvatars(sender);
    }

    private void RecentPlayersList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0)
        {
            RequestVisibleRecentAvatars(sender);
        }
    }

    private void RequestVisibleRecentAvatars(object sender)
    {
        if (sender is not ListBox list || _viewModel.RecentPlayersViewModel is not { } recent)
        {
            return;
        }

        foreach (var container in FindVisualChildren<ListBoxItem>(list))
        {
            if (container.DataContext is RecentPlayerItem player)
            {
                recent.RequestAvatar(player);
            }
        }
    }

    private void ChatboxComposer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            var complete = _viewModel.ChatboxViewModel.AcceptAutocompleteCommand;
            if (complete.CanExecute(null))
            {
                complete.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            var stop = _viewModel.ChatboxViewModel.StopChatCommand;
            if (stop.CanExecute(null))
            {
                stop.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        var command = _viewModel.ChatboxViewModel.SendChatCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Makes the mouse wheel act on the nearest scrollable surface under the
    /// pointer, then continue outward when that surface reaches its edge. This
    /// fixes nested lists in Context, Continuity, profiles, and popups where the
    /// inner list used to swallow the wheel and the outer page would not move.
    /// </summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (ScrollViewerBehavior.HandleMouseWheel(e.OriginalSource as DependencyObject, e.Delta))
        {
            e.Handled = true;
        }
    }

    private bool TryHandleBrowserFocusShortcut(KeyEventArgs e)
        => TryHandleBrowserFocusShortcut(
            e.Key == Key.System ? e.SystemKey : e.Key,
            Keyboard.Modifiers);

    private bool TryHandleBrowserFocusShortcut(Key key, ModifierKeys modifiers)
    {
        if (!_viewModel.IsAnyBrowserFocused)
        {
            return false;
        }

        var browser = FindVisibleBrowser();
        if (browser is null)
        {
            return false;
        }

        if (key == Key.Left && modifiers.HasFlag(ModifierKeys.Alt))
        {
            browser.GoBack();
            return true;
        }

        if (key == Key.Right && modifiers.HasFlag(ModifierKeys.Alt))
        {
            browser.GoForward();
            return true;
        }

        if (key == Key.L &&
            modifiers.HasFlag(ModifierKeys.Control) &&
            !modifiers.HasFlag(ModifierKeys.Alt))
        {
            browser.FocusAddressBar();
            return true;
        }

        if (key == Key.F5 ||
            (key == Key.R && modifiers.HasFlag(ModifierKeys.Control)))
        {
            browser.Reload();
            return true;
        }

        return false;
    }

    private EmbeddedBrowser? FindVisibleBrowser()
    {
        foreach (var browser in FindVisualChildren<EmbeddedBrowser>(SectionHost))
        {
            if (browser.IsVisible)
            {
                return browser;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
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

    private void OnChatboxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatboxViewModel.KawaiiAccentHex))
        {
            ApplyKawaiiTheme();
        }
    }

    private void ApplyKawaiiTheme()
    {
        var chatbox = _viewModel.ChatboxViewModel;

        // Remove old theme brushes so they don't accumulate in the resource
        // dictionary on every accent change.
        foreach (var key in _themeBrushKeys)
        {
            Resources.Remove(key);
        }
        _themeBrushKeys.Clear();

        var brushes = new Dictionary<string, Brush>
        {
            { "KawaiiAccentBrush", new SolidColorBrush(chatbox.KawaiiAccentColor) },
            { "KawaiiAccentSoftBrush", new SolidColorBrush(chatbox.KawaiiAccentSoftColor) },
            { "KawaiiHeaderStartBrush", new SolidColorBrush(chatbox.KawaiiHeaderStartColor) },
            { "KawaiiHeaderEndBrush", new SolidColorBrush(chatbox.KawaiiHeaderEndColor) },
            { "KawaiiHeaderTextBrush", new SolidColorBrush(chatbox.KawaiiHeaderTextColor) },
            { "KawaiiPanelBrush", new SolidColorBrush(chatbox.KawaiiPanelColor) },
            { "KawaiiBorderBrush", new SolidColorBrush(chatbox.KawaiiBorderColor) },
            { "KawaiiChipBrush", new SolidColorBrush(chatbox.KawaiiChipColor) },
            { "KawaiiMutedBrush", new SolidColorBrush(chatbox.KawaiiMutedColor) },
            { "KawaiiShellBrush", new SolidColorBrush(chatbox.KawaiiShellColor) },
            { "KawaiiTabStripBrush", new SolidColorBrush(chatbox.KawaiiTabStripColor) },
            { "KawaiiListBrush", new SolidColorBrush(chatbox.KawaiiListColor) },
        };

        foreach (var pair in brushes)
        {
            pair.Value.Freeze();
            Resources[pair.Key] = pair.Value;
            _themeBrushKeys.Add(pair.Key);
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(chatbox.KawaiiHeaderStartColor, 0),
                new GradientStop(BlendColor(chatbox.KawaiiHeaderStartColor, chatbox.KawaiiHeaderEndColor, 0.62), 0.62),
                new GradientStop(chatbox.KawaiiHeaderEndColor, 1)
            }
        };
        gradient.Freeze();
        Resources["KawaiiHeaderBrush"] = gradient;
        _themeBrushKeys.Add("KawaiiHeaderBrush");
    }

    private static Color BlendColor(Color from, Color to, double amount)
    {
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
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
    /// Builds a section once and keeps a small recent set in the visual tree.
    /// Hiding a view preserves search text and scroll position, but keeping
    /// every visited page alive (especially WebView2 pages) costs too much RAM.
    /// Discord stays cached so an active voice call is never interrupted.
    /// </summary>
    private void ShowSelectedSection()
    {
        var key = _viewModel.SelectedSection.Key;
        ReleaseDataOnLeave(key);
        // Every switch settles, not only the ones that hand a page back. Two light
        // tabs both stay in the cache, so nothing was evicted - but the page that
        // was just left still dropped its garbage and its artwork is still in the
        // decode cache. The flag coalesces a burst of hops into one collection.
        if (_shownSectionKey.Length > 0 &&
            !string.Equals(_shownSectionKey, key, StringComparison.Ordinal))
        {
            ScheduleSettle();
        }
        _shownSectionKey = key;
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

        CancelDataRelease(key);
        _sectionOrder.Remove(key);
        _sectionOrder.Add(key);
        LifecycleLog.Write($"section: {key} live={_sectionViews.Count}");

        // Brave and Creator Resources normally have no background work, so drop
        // their WebView when leaving. Keep them when audio or a download is active.
        if (key != "brave")
        {
            EvictSectionView("brave");
        }
        if (key != "resources")
        {
            EvictSectionView("resources");
        }

        while (_sectionOrder.Count > MaxCachedSectionViews)
        {
            var oldest = _sectionOrder.FirstOrDefault(candidate =>
                !string.Equals(candidate, key, StringComparison.Ordinal) &&
                !ShouldKeepSectionView(candidate));
            if (oldest is null)
            {
                break;
            }
            EvictSectionView(oldest);
        }

        foreach (var section in _sectionViews)
        {
            section.Value.Visibility = string.Equals(section.Key, key, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // The leave hook trusts the previous-section key, which can be stale if
        // the selection moved twice before layout settled. Sweep cached avatar
        // sections so their library is handed back regardless.
        if (_viewModel.AvatarViewModel is { } avatarViewModel &&
            (avatarViewModel.HasLoaded ||
             avatarViewModel.Avatars.Count > 0 ||
             avatarViewModel.GlobalResults.Count > 0))
        {
            foreach (var cachedKey in _sectionViews.Keys.Where(IsAvatarSectionKey).ToList())
            {
                if (!string.Equals(cachedKey, key, StringComparison.Ordinal))
                {
                    ReleaseSectionDataNow(cachedKey);
                }
            }
        }
    }

    /// <summary>How long to wait before retrying a release that was refused.</summary>
    internal TimeSpan SectionReleaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The settles that have run; a test uses it to prove a switch settled.</summary>
    internal int SettleCount => Volatile.Read(ref _settleCount);

    private static bool IsAvatarSectionKey(string key)
        => string.Equals(key, "avatars", StringComparison.Ordinal) ||
           string.Equals(key, "avatarsearch", StringComparison.Ordinal);

    /// <summary>
    /// Avatar data only counts as live while an avatar tab is actually on
    /// screen. A cached but hidden avatar view keeps no claim on its rows.
    /// </summary>
    private List<string> LiveSectionKeys()
    {
        var selectedKey = _viewModel.SelectedSection.Key;
        return _sectionViews.Keys
            .Where(candidate => !IsAvatarSectionKey(candidate) ||
                                string.Equals(candidate, selectedKey, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// The weight a section may keep after its page is left. Most tabs keep their
    /// rows for the grace period so hopping back is free; the avatar library and
    /// the picture library are the two that cost hundreds of megabytes, so they
    /// are handed back the moment they leave the screen.
    /// </summary>
    private static TimeSpan DataGraceFor(string key) => key switch
    {
        "media" => MediaDataGrace,
        _ when IsAvatarSectionKey(key) => AvatarDataGrace,
        _ => SectionDataGrace
    };

    private void ReleaseDataOnLeave(string key)
    {
        if (string.IsNullOrEmpty(_shownSectionKey) ||
            string.Equals(_shownSectionKey, key, StringComparison.Ordinal))
        {
            return;
        }

        var left = _shownSectionKey;

        // Avatars and Avatar Search draw the same library, so moving between
        // them is not leaving it.
        if (IsAvatarSectionKey(left) && IsAvatarSectionKey(key))
        {
            return;
        }

        if (DataGraceFor(left) > TimeSpan.Zero)
        {
            return;
        }

        ReleaseSectionDataNow(left);
    }

    private void ReleaseSectionDataNow(string key)
    {
        CancelDataRelease(key);
        // The section being released never counts as live, even if the selected
        // key and the cached-view bookkeeping briefly disagree during a switch.
        var liveKeys = LiveSectionKeys()
            .Where(candidate => !string.Equals(candidate, key, StringComparison.Ordinal))
            .ToList();
        var released = _viewModel.ReleaseSectionData(key, liveKeys);
        if (LifecycleLog.Verbose)
        {
            var avatar = IsAvatarSectionKey(key)
                ? $" busy={_viewModel.AvatarViewModel?.IsBusy}" +
                  $" rows={_viewModel.AvatarViewModel?.Avatars.Count}"
                : string.Empty;
            LifecycleLog.Write(
                $"section-release: {key} released={released}{avatar} " +
                $"selected={_viewModel.SelectedSection.Key} live=[{string.Join(",", liveKeys)}] " +
                $"cached=[{string.Join(",", _sectionViews.Keys)}]");
        }
        if (!released)
        {
            // The library was still arriving; hand it back once it has landed.
            ScheduleDataRelease(key, SectionReleaseRetryDelay);
            return;
        }

        ScheduleSettle();
    }

    private void CancelAllDataReleases()
    {
        foreach (var pending in _pendingReleases.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }

        _pendingReleases.Clear();
    }

    private void EvictSectionView(string key)
    {
        if (string.Equals(key, _viewModel.SelectedSection.Key, StringComparison.Ordinal) ||
            ShouldKeepSectionView(key) ||
            !_sectionViews.Remove(key, out var view))
        {
            return;
        }

        _sectionOrder.Remove(key);
        ReleaseBrowsers(view);
        SectionHost.Children.Remove(view);
        view.Content = null;
        view.ContentTemplate = null;
        ScheduleDataRelease(key);
        ScheduleSettle();
    }

    /// <summary>
    /// A section that was just left keeps its loaded data for a while. Dropping
    /// it immediately made every hop back to a tab refetch from scratch, which
    /// showed up as memory dropping and then climbing straight back up. The data
    /// is released once the tab has stayed closed long enough that returning is
    /// a deliberate act, and returning cancels the release.
    /// </summary>
    private void ScheduleDataRelease(string key) => ScheduleDataRelease(key, SectionDataGrace);

    private void ScheduleDataRelease(string key, TimeSpan delay)
    {
        CancelDataRelease(key);
        var cts = new CancellationTokenSource();
        _pendingReleases[key] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _pendingReleases.Remove(key);
                var liveKeys = LiveSectionKeys()
                    .Where(candidate => !string.Equals(candidate, key, StringComparison.Ordinal))
                    .ToList();
                if (!_viewModel.ReleaseSectionData(key, liveKeys))
                {
                    // A retry can land while the library is still busy. Keeping
                    // the retry armed is what makes the release eventually happen.
                    ScheduleDataRelease(key, SectionReleaseRetryDelay);
                }

                ScheduleSettle();
            });
        });
    }

    private void CancelDataRelease(string key)
    {
        if (_pendingReleases.Remove(key, out var pending))
        {
            pending.Cancel();
            pending.Dispose();
        }
    }

    /// <summary>
    /// Dropping a section view releases its visual tree, but the objects it held
    /// are only returned once a collection runs. Visiting a heavy tab used to
    /// leave a permanently higher footprint because the settle only sent a
    /// non-blocking hint, which is allowed to do nothing - and did. This now
    /// collects for real, including the large object heap, and logs what it took
    /// back. Every switch settles once: the flag coalesces a burst of hops into
    /// the one collection at the end, instead of skipping switches inside a
    /// time window.
    /// </summary>
    private void ScheduleSettle()
    {
        if (_settleScheduled)
        {
            return;
        }

        _settleScheduled = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // Let the new page lay out first: a collect suspends every thread, so it
                // must not land in the middle of the paint that follows a switch.
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                // Timed from here, after the settle's own deliberate second: timing the whole
                // method made every run report a second of "slow work" that was this delay.
                // The collect below touches the whole process, so a stall it caused has to name
                // it instead of looking like an unexplained freeze.
                using var job = UiJobTrace.Shared.Begin("settle");
                // The evicted page's images are only reachable through the decode
                // cache now; dropping it costs a re-decode from the disk cache and
                // frees up to 24 MB of the heap the collect is about to walk.
                var result = MemorySettle.Run(ImageLoader.Shared.TrimMemory);
                Interlocked.Increment(ref _settleCount);
                LifecycleLog.Write(
                    $"memory: settled in {result.Milliseconds}ms " +
                    $"freed={result.FreedBytes / (1024 * 1024)}MB");

                // The working set is deliberately not emptied: handing every page back to
                // the pagefile left a window that faulted half a gigabyte back in on the next
                // paint, seconds of a dead UI. Compacting the heap is what actually returns the
                // memory the tab's garbage was holding.
                LogMemory("after-evict");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                _settleScheduled = false;
            }
        });
    }

    private bool SectionHasBrowserActivity(string key)
    {
        return _sectionViews.TryGetValue(key, out var view) && ContainsBrowserActivity(view);
    }

    private static bool ContainsBrowserActivity(DependencyObject root)
    {
        if (root is EmbeddedBrowser browser && browser.HasBackgroundActivity)
        {
            return true;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (ContainsBrowserActivity(VisualTreeHelper.GetChild(root, index)))
            {
                return true;
            }
        }

        return false;
    }

    private static void ReleaseBrowsers(DependencyObject root)
    {
        if (root is EmbeddedBrowser browser)
        {
            browser.ReleaseForMemory();
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            ReleaseBrowsers(VisualTreeHelper.GetChild(root, index));
        }
    }

    private bool ShouldKeepSectionView(string key)
    {
        var (browserFocused, browserActive) = key switch
        {
            "brave" => (_viewModel.BraveBrowserViewModel.IsFocusMode, SectionHasBrowserActivity(key)),
            "resources" => (_viewModel.CreatorResourcesViewModel.IsFocusMode, SectionHasBrowserActivity(key)),
            _ => (false, false)
        };

        return ShouldKeepSectionView(
            key,
            _viewModel.DiscordBrowserViewModel.IsBrowserOpen,
            browserFocused,
            browserActive);
    }

    /// <summary>
    /// A focused or playing browser is an active workspace; its view owns the layout and the
    /// WebView state, and keeping it is what stops a section switch from restarting the page.
    /// Discord is kept whenever its page is open: evicting the view disposes the WebView, and
    /// a reload drops a voice call. A call is not always audible, so "playing audio" cannot
    /// stand in for it, and the page has to stay instead.
    /// </summary>
    internal static bool ShouldKeepSectionView(
        string key,
        bool discordOpen,
        bool browserFocused,
        bool browserActive)
        => key switch
        {
            "discord" => discordOpen,
            "brave" or "resources" => browserFocused || browserActive,
            _ => false
        };

    private static string TemplateKeyFor(string key)
        => key switch
        {
            "friends" => "FriendsTemplate",
            "instance" => "InstanceTemplate",
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
            "continuity" => "ContinuityTemplate",
            "context" => "ContextTemplate",
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

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // A closing window raises Deactivated after Closing stopped the timer;
        // restarting it there would keep the window and a later cache trim alive.
        if (!_isClosed)
        {
            _backgroundTrimTimer.Start();
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _backgroundTrimTimer.Stop();

        // Returning to a window that has been out of sight is a good moment to
        // notice a release published since the last check, so the bottom update
        // bar fills in without opening Settings.
        try
        {
            _ = _viewModel.UpdateViewModel?.RequestCheckIfStale(TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("updater activation check: " + ex.Message);
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _backgroundTrimTimer.Start();
            ImageLoader.Shared.TrimMemory();
        }
        else if (IsActive)
        {
            _backgroundTrimTimer.Stop();
        }
    }

    private static void LogMemory(string stage)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            LifecycleLog.Write(
                $"memory: {stage} working-set={process.WorkingSet64 / (1024 * 1024)}MB private={process.PrivateMemorySize64 / (1024 * 1024)}MB");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnBackgroundTrimTick(object? sender, EventArgs e)
    {
        _backgroundTrimTimer.Stop();
        if (!TryTrimBackgroundImages())
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // Only hint: a blocking collect freezes every thread in the process for the
                // length of the sweep. The decoded-image cache has already been dropped by the
                // caller, and the working set is left to Windows - emptying it here is what made
                // the window dead for seconds after every trim, faulting half a gigabyte of pages
                // back in the moment anything touched it.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("background memory trim: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Drops decoded artwork while the app is in the background. A closed window
    /// must never do this: its timer can outlive the page that armed it.
    /// </summary>
    internal bool TryTrimBackgroundImages()
    {
        if (!ShouldTrimBackgroundImages(_isClosed, IsActive, WindowState))
        {
            return false;
        }

        ImageLoader.Shared.TrimMemory();
        return true;
    }

    internal static bool ShouldTrimBackgroundImages(
        bool isClosed,
        bool isActive,
        WindowState state)
        => !isClosed && !(isActive && state != WindowState.Minimized);

    internal bool IsBackgroundTrimScheduledForTest => _backgroundTrimTimer.IsEnabled;

    /// <summary>
    /// A mode change while the window is up has to take effect without a restart: turning
    /// lite mode on drops cached pages down to the new limit straight away, and turning it
    /// off costs nothing until the next section switch.
    /// </summary>
    private void OnPerformanceChanged()
    {
        if (_isClosed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnPerformanceChanged));
            return;
        }

        while (_sectionOrder.Count > MaxCachedSectionViews)
        {
            var oldest = _sectionOrder.FirstOrDefault(candidate =>
                !string.Equals(candidate, _shownSectionKey, StringComparison.Ordinal) &&
                !ShouldKeepSectionView(candidate));
            if (oldest is null)
            {
                break;
            }

            EvictSectionView(oldest);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        AppPerformance.Changed -= OnPerformanceChanged;
        _isClosed = true;
        _refreshTimer.Stop();
        _backgroundTrimTimer.Stop();
        CancelAllDataReleases();
        _stallMonitor.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        LifecycleLog.Write($"main window closing shutdownMode={Application.Current?.ShutdownMode}");
        _refreshTimer.Stop();
        _backgroundTrimTimer.Stop();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }


    private static int ToColorRef(byte red, byte green, byte blue)
        => red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

}
