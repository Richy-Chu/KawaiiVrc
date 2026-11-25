using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Controls;

/// <summary>
/// Hosts WebView2 lazily. The owner keeps one cached instance per tab, so the
/// page and any active call stay alive while the tab is hidden.
/// </summary>
public partial class EmbeddedBrowser : UserControl, IEmbeddedBrowserAutomation
{
    public static readonly DependencyProperty CanGoBackProperty = DependencyProperty.Register(
        nameof(CanGoBack),
        typeof(bool),
        typeof(EmbeddedBrowser),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CanGoForwardProperty = DependencyProperty.Register(
        nameof(CanGoForward),
        typeof(bool),
        typeof(EmbeddedBrowser),
        new PropertyMetadata(false));

    public static readonly DependencyProperty AdBlockEnabledProperty = DependencyProperty.Register(
        nameof(AdBlockEnabled),
        typeof(bool),
        typeof(EmbeddedBrowser),
        new PropertyMetadata(true, OnAdBlockEnabledChanged));

    private const string FocusShortcutScript = """
        window.__vrcFocusMode = false;
        window.addEventListener('keydown', (event) => {
          if (event.key === 'Escape') {
            window.chrome.webview.postMessage('browser-focus-escape');
            return;
          }

          if (window.__vrcFocusMode !== true) return;

          if (event.altKey && event.key === 'ArrowLeft') {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-back');
          } else if (event.altKey && event.key === 'ArrowRight') {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-forward');
          } else if (event.ctrlKey && !event.altKey && event.key.toLowerCase() === 'l') {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-address');
          } else if (event.key === 'F5' || (event.ctrlKey && event.key.toLowerCase() === 'r')) {
            event.preventDefault();
            window.chrome.webview.postMessage('browser-focus-refresh');
          }
        }, true);
        """;

    private const string FallbackAdBlockCss =
        "[data-ad-slot], [data-ad-unit], [data-ad-client],\n" +
        "[id^=\"google_ads\"], [id^=\"div-gpt-ad\"], [id^=\"ad-slot\"], [id^=\"ad_banner\"],\n" +
        "ins.adsbygoogle,\n" +
        "iframe[src*=\"doubleclick.net\"],\n" +
        "iframe[src*=\"googlesyndication.com\"],\n" +
        "iframe[src*=\"amazon-adsystem.com\"],\n" +
        "[aria-label=\"Advertisement\"] { display: none !important; }";

    private const string ApplyAdBlockCssTemplate = """
        (() => {
          const id = 'vrc-adblock-style';
          let style = document.getElementById(id);
          if (!style) {
            style = document.createElement('style');
            style.id = id;
            (document.head || document.documentElement).appendChild(style);
          }
          style.textContent = {0};
          return 'added';
        })()
        """;

    private const string RemoveAdBlockCssScript =
        "document.getElementById('vrc-adblock-style')?.remove();";

    private const string FingerprintProtectionScript = """
        (() => {
          if (window.__vrcFingerprintShield) return;
          window.__vrcFingerprintShield = true;

          const define = (target, property, value) => {
            try {
              Object.defineProperty(target, property, {
                configurable: true,
                get: () => value
              });
            } catch (_) {}
          };

          define(navigator, 'hardwareConcurrency', 4);
          define(navigator, 'deviceMemory', 8);
          define(navigator, 'plugins', []);
          define(navigator, 'mimeTypes', []);
          if (navigator.languages) define(navigator, 'languages', ['en-US', 'en']);

          const patchWebGl = (prototype) => {
            if (!prototype || prototype.__vrcShieldPatched) return;
            const original = prototype.getParameter;
            prototype.getParameter = function (parameter) {
              if (parameter === 37445) return 'Google Inc.';
              if (parameter === 37446) return 'ANGLE (Intel, Intel(R) UHD Graphics, OpenGL 4.1)';
              return original.call(this, parameter);
            };
            prototype.__vrcShieldPatched = true;
          };
          patchWebGl(window.WebGLRenderingContext && WebGLRenderingContext.prototype);
          patchWebGl(window.WebGL2RenderingContext && WebGL2RenderingContext.prototype);

          const addCanvasNoise = (data) => {
            for (let i = 0; i < data.length; i += 997) {
              data[i] = (data[i] + 1) % 256;
            }
          };

          if (window.CanvasRenderingContext2D) {
            const originalGetImageData = CanvasRenderingContext2D.prototype.getImageData;
            CanvasRenderingContext2D.prototype.getImageData = function () {
              const result = originalGetImageData.apply(this, arguments);
              try { addCanvasNoise(result.data); } catch (_) {}
              return result;
            };
          }

          if (window.HTMLCanvasElement) {
            const originalToDataUrl = HTMLCanvasElement.prototype.toDataURL;
            HTMLCanvasElement.prototype.toDataURL = function () {
              try {
                const context = this.getContext('2d');
                if (context && this.width && this.height) {
                  const image = context.getImageData(0, 0, this.width, this.height);
                  addCanvasNoise(image.data);
                  context.putImageData(image, 0, 0);
                }
              } catch (_) {}
              return originalToDataUrl.apply(this, arguments);
            };
          }
        })();
        """;

    private const string StrictFingerprintProtectionScript = """
        (() => {
          if (window.__vrcFingerprintShieldStrict) return;
          window.__vrcFingerprintShieldStrict = true;

          const define = (target, property, value) => {
            try {
              Object.defineProperty(target, property, {
                configurable: true,
                get: () => value
              });
            } catch (_) {}
          };

          define(navigator, 'hardwareConcurrency', 2);
          define(navigator, 'deviceMemory', 4);
          define(navigator, 'plugins', []);
          define(navigator, 'mimeTypes', []);
          if (navigator.languages) define(navigator, 'languages', ['en-US', 'en']);

          const blankCanvas = () => {
            try {
              const canvas = document.createElement('canvas');
              canvas.width = 1;
              canvas.height = 1;
              return 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL1WQAAAABJRU5ErkJggg==';
            } catch (_) {
              return 'data:image/png;base64,';
            }
          };

          if (window.CanvasRenderingContext2D) {
            const originalGetImageData = CanvasRenderingContext2D.prototype.getImageData;
            CanvasRenderingContext2D.prototype.getImageData = function () {
              const result = originalGetImageData.apply(this, arguments);
              try { result.data.fill(0); } catch (_) {}
              return result;
            };
          }

          if (window.HTMLCanvasElement) {
            HTMLCanvasElement.prototype.toDataURL = function () {
              return blankCanvas();
            };
            const originalToBlob = HTMLCanvasElement.prototype.toBlob;
            HTMLCanvasElement.prototype.toBlob = function (callback) {
              if (typeof callback === 'function') {
                const binary = atob(blankCanvas().split(',')[1]);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
                callback(new Blob([bytes], { type: 'image/png' }));
              }
            };
          }

          const patchWebGl = (prototype) => {
            if (!prototype || prototype.__vrcStrictShieldPatched) return;
            const original = prototype.getParameter;
            prototype.getParameter = function (parameter) {
              if (parameter === 37445) return 'Google Inc.';
              if (parameter === 37446) return 'ANGLE (Intel, Intel(R) UHD Graphics, OpenGL 4.1)';
              return original.call(this, parameter);
            };
            prototype.__vrcStrictShieldPatched = true;
          };
          patchWebGl(window.WebGLRenderingContext && WebGLRenderingContext.prototype);
          patchWebGl(window.WebGL2RenderingContext && WebGL2RenderingContext.prototype);
        })();
        """;

    // WebRTC normally advertises this machine through mDNS. Windows Firewall
    // treats that as a new inbound service and prompts for access, even after
    // the user has allowed an earlier WebView2 runtime build. Discord voice and
    // the other embedded pages still work without mDNS host candidates.
    private const string BrowserArguments = "--disable-features=WebRtcHideLocalIpsWithMdns";

    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment =
        new(CreateSharedEnvironmentAsync);

    /// <summary>Tests point the browser profile at a temp folder instead of the user's.</summary>
    internal static string? UserDataFolderOverride { get; set; }

    /// <summary>
    /// Cookies, logins and site storage live in WebView2's user data folder. Left at its
    /// default that folder sits next to the executable, which is the build output locally
    /// and a new versioned folder with every Velopack update — so Discord and the other
    /// tabs asked for a fresh sign-in after each build. Keep one profile in the app's own
    /// data folder so sessions survive rebuilds and updates.
    /// </summary>
    internal static string ResolveUserDataFolder(string? overridePath, string profileRoot)
        => !string.IsNullOrWhiteSpace(overridePath)
            ? overridePath!
            : Path.Combine(profileRoot, "Browser");

    private IEmbeddedBrowserHost? _viewModel;
    private Task? _initialization;
    private string? _pendingUrl;
    private Window? _window;
    private bool _disposed;
    private bool _focusErrorAnnouncementScheduled;
    private bool _adBlockActive;
    private bool _isSuspended;
    private int _suspensionVersion;
    private string? _fingerprintScriptId;
    private BraveFingerprintMode? _fingerprintScriptMode;
    private string? _documentScriptId;
    private string? _documentScriptUrl;
    private string? _scriptPreparedNavigationUri;
    private readonly SemaphoreSlim _documentScriptGate = new(1, 1);
    private BraveCookieMode? _cookieMode;
    private bool _cookieRestrictionActive;
    private bool _networkEnabled;
    private readonly HashSet<CoreWebView2DownloadOperation> _activeDownloads = new();
    private const int SuspendDelayMilliseconds = 3000;

    // Test seam only; production leaves this null and shows the Save As dialog.
    internal static Func<string, string?>? DownloadPathProvider { get; set; }

    internal bool IsAdBlockFilterActive => _adBlockActive;

    internal int ObservedAdBlockRequests { get; private set; }

    internal int BlockedAdBlockRequests { get; private set; }

    internal bool IsSuspendedForTest => _isSuspended;

    internal bool HasActiveDownloadsForTest => _activeDownloads.Count > 0;

    internal CoreWebView2DownloadOperation? ActiveDownloadForTest
        => _activeDownloads.FirstOrDefault();

    public EmbeddedBrowser()
    {
        InitializeComponent();
        Shields = new BraveShieldsPanelViewModel(BraveShieldsService.Shared);
        Shields.SettingsChanged += OnShieldsSettingsChanged;
        BraveShieldsService.Shared.Changed += OnExternalShieldsChanged;
        ShieldsPopup.DataContext = Shields;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public BraveShieldsPanelViewModel Shields { get; }

    private bool EffectiveBlockAds => AdBlockEnabled && Shields.ShouldBlockTrackersAndAds;

    public bool CanGoBack
    {
        get => (bool)GetValue(CanGoBackProperty);
        private set => SetValue(CanGoBackProperty, value);
    }

    public bool CanGoForward
    {
        get => (bool)GetValue(CanGoForwardProperty);
        private set => SetValue(CanGoForwardProperty, value);
    }

    public bool AdBlockEnabled
    {
        get => (bool)GetValue(AdBlockEnabledProperty);
        set => SetValue(AdBlockEnabledProperty, value);
    }

    private static void OnAdBlockEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
        => ((EmbeddedBrowser)dependencyObject).RefreshAdBlock();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        => UpdateMemoryTargetLevel();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _viewModel = DataContext as IEmbeddedBrowserHost;
        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested += OnBrowserNavigationRequested;
            if (_viewModel is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged += OnViewModelPropertyChanged;
            }
            if (_viewModel is IEmbeddedBrowserAutomationHost automationHost)
            {
                automationHost.Automation = this;
            }

            if (_viewModel.IsBrowserOpen)
            {
                _pendingUrl = _viewModel.BrowserUrl;
            }
        }

        if (_window is null)
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                _window.Closed += OnWindowClosed;
                _window.StateChanged += OnWindowStateChanged;
                _window.Activated += OnWindowActivated;
                _window.Deactivated += OnWindowDeactivated;
            }
        }

        _ = NavigatePendingAsync();
        AnnounceFocusBrowserError();
    }

    /// <summary>
    /// The section host calls this before dropping a cached browser view so a
    /// hidden WebView2 renderer does not stay alive for the rest of the session.
    /// </summary>
    public void ReleaseForMemory()
    {
        // Keep a page alive while it is producing audio or downloading. This is
        // the same protection Discord gets for an active voice call.
        if (ShouldReleaseForMemory(HasActiveDownloads, IsDocumentPlayingAudio))
        {
            DisposeBrowser();
        }
    }

    internal bool HasBackgroundActivity => HasActiveDownloads || IsDocumentPlayingAudio;

    internal static bool ShouldReleaseForMemory(
        bool hasActiveDownloads,
        bool isDocumentPlayingAudio)
        => !hasActiveDownloads && !isDocumentPlayingAudio;

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.StateChanged -= OnWindowStateChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }

        DisposeBrowser();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
        => UpdateMemoryTargetLevel();

    // Alt-tabbing away is not the same as leaving the page: the browser keeps its
    // page, its scroll position and its session, and only drops to the low memory
    // target until the window is activated again. Nothing is disposed here - a
    // disposed WebView comes back blank, because _pendingUrl is already cleared
    // once a page has navigated, and a cached section would reload from scratch.
    private void OnWindowActivated(object? sender, EventArgs e)
        => UpdateMemoryTargetLevel();

    private void OnWindowDeactivated(object? sender, EventArgs e)
        => UpdateMemoryTargetLevel();

    private void OnBrowserNavigationRequested(string url)
    {
        _pendingUrl = url;
        if (IsLoaded && !_disposed)
        {
            _ = NavigatePendingAsync();
        }
    }

    private async Task NavigatePendingAsync()
    {
        if (_pendingUrl is not { } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            await EnsureBrowserAsync();
            ResumeIfSuspended();
            if (_disposed || !IsLoaded ||
                !string.Equals(_pendingUrl, url, StringComparison.Ordinal))
            {
                return;
            }

            Browser.Source = uri;
            _pendingUrl = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _viewModel?.SetBrowserError(
                "The in-app browser could not start. Install the Microsoft Edge WebView2 Runtime, or use Open site instead.");
        }
    }

    internal Task InitializeForTestAsync()
        => EnsureBrowserAsync();

    private Task EnsureBrowserAsync()
        => _initialization ??= InitializeBrowserAsync();

    private async Task<CoreWebView2?> GetCoreAsync()
    {
        if (_disposed)
        {
            return null;
        }

        await EnsureBrowserAsync();
        ResumeIfSuspended();
        return _disposed ? null : Browser.CoreWebView2;
    }

    public async Task SendKeyAsync(
        string key,
        string code,
        int virtualKeyCode,
        bool control = false,
        bool shift = false)
    {
        if (await GetCoreAsync() is not { } core)
        {
            return;
        }

        try
        {
            Browser.Focus();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        var modifiers = (control ? 2 : 0) | (shift ? 8 : 0);
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchKeyEvent",
            BuildKeyEvent("keyDown", key, code, virtualKeyCode, modifiers));
        await core.CallDevToolsProtocolMethodAsync(
            "Input.dispatchKeyEvent",
            BuildKeyEvent("keyUp", key, code, virtualKeyCode, modifiers));
    }

    public async Task InsertTextAsync(string text)
    {
        if (await GetCoreAsync() is not { } core)
        {
            return;
        }

        await core.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text }));
    }

    public async Task<string> ExecuteScriptAsync(string script)
    {
        if (await GetCoreAsync() is not { } core)
        {
            return string.Empty;
        }

        return await core.ExecuteScriptAsync(script);
    }

    private static string BuildKeyEvent(
        string type,
        string key,
        string code,
        int virtualKeyCode,
        int modifiers)
        => JsonSerializer.Serialize(new
        {
            type,
            modifiers,
            key,
            code,
            windowsVirtualKeyCode = virtualKeyCode,
            nativeVirtualKeyCode = virtualKeyCode
        });

    private void DisposeBrowser()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialization = null;
        _suspensionVersion++;
        _isSuspended = false;
        Shields.SettingsChanged -= OnShieldsSettingsChanged;
        BraveShieldsService.Shared.Changed -= OnExternalShieldsChanged;
        ShieldsPopup.IsOpen = false;

        if (_viewModel is not null)
        {
            _viewModel.BrowserNavigationRequested -= OnBrowserNavigationRequested;
            if (_viewModel is INotifyPropertyChanged notifier)
            {
                notifier.PropertyChanged -= OnViewModelPropertyChanged;
            }
            if (_viewModel is IEmbeddedBrowserAutomationHost automationHost)
            {
                automationHost.Automation = null;
            }

            _viewModel = null;
        }

        try
        {
            if (Browser.CoreWebView2 is { } core)
            {
                if (_adBlockActive)
                {
                    try
                    {
                        core.WebResourceRequested -= OnWebResourceRequested;
                        core.RemoveWebResourceRequestedFilter(
                            "*://*/*",
                            CoreWebView2WebResourceContext.All,
                            CoreWebView2WebResourceRequestSourceKinds.All);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }

                    _adBlockActive = false;
                }

                core.NewWindowRequested -= OnNewWindowRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.SourceChanged -= OnSourceChanged;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.DocumentTitleChanged -= OnDocumentTitleChanged;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.IsDocumentPlayingAudioChanged -= OnIsDocumentPlayingAudioChanged;

                foreach (var operation in _activeDownloads)
                {
                    operation.StateChanged -= OnDownloadStateChanged;
                }
                _activeDownloads.Clear();
            }

            Browser.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task InitializeBrowserAsync()
    {
        var environment = await SharedEnvironment.Value;
        await Browser.EnsureCoreWebView2Async(environment);

        if (Browser.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("WebView2 did not initialize.");
        }

        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.SourceChanged += OnSourceChanged;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.WebMessageReceived += OnWebMessageReceived;
        core.IsDocumentPlayingAudioChanged += OnIsDocumentPlayingAudioChanged;
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(FocusShortcutScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        RefreshAdBlock();
        UpdateNavigationState();
        await ApplyShieldsPolicyAsync();
        await SetPageFocusModeAsync(_viewModel?.IsFocusMode == true);
        UpdateMemoryTargetLevel();
    }

    private static async Task<CoreWebView2Environment> CreateSharedEnvironmentAsync()
    {
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments);
        var folder = ResolveUserDataFolder(UserDataFolderOverride, UserDataPaths.Directory);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            // The browser can still fall back to its default folder rather than fail outright.
            Debug.WriteLine(ex);
            folder = null!;
        }

        return await CoreWebView2Environment.CreateAsync(null, folder, options);
    }

    // Collapsed tabs and a minimized window keep their page alive while Chromium
    // releases memory. Non-Discord pages are suspended after a short grace period
    // unless they are playing audio or downloading; Discord stays awake so an
    // active voice call is not interrupted.
    private void UpdateMemoryTargetLevel()
    {
        if (_disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            // An inactive window is not rendered, so Chromium may give its memory
            // back; activation restores the normal target. Suspension stays tied to
            // a page being off screen, so an alt-tab never suspends what you were
            // reading and never has to resume it on the way back.
            var hidden = !IsVisible || _window?.WindowState == WindowState.Minimized;
            core.MemoryUsageTargetLevel = ShouldUseLowMemoryTarget(
                hidden || _window is { IsActive: false },
                HasActiveDownloads,
                IsDocumentPlayingAudio)
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        UpdateSuspensionState();
    }

    private bool ShouldSuspendWhenHidden =>
        !_disposed &&
        (!IsVisible || _window?.WindowState == WindowState.Minimized) &&
        ShouldSuspendHiddenPage(
            _viewModel is DiscordBrowserViewModel,
            HasActiveDownloads,
            IsDocumentPlayingAudio);

    internal static bool ShouldUseLowMemoryTarget(
        bool hidden,
        bool hasActiveDownloads,
        bool isDocumentPlayingAudio)
        => hidden && !hasActiveDownloads && !isDocumentPlayingAudio;

    internal static bool ShouldSuspendHiddenPage(
        bool isDiscord,
        bool hasActiveDownloads,
        bool isDocumentPlayingAudio)
        => !isDiscord && !hasActiveDownloads && !isDocumentPlayingAudio;

    private bool HasActiveDownloads => _activeDownloads.Count > 0;

    private bool IsDocumentPlayingAudio
    {
        get
        {
            try
            {
                return Browser.CoreWebView2?.IsDocumentPlayingAudio == true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return false;
            }
        }
    }

    private void OnIsDocumentPlayingAudioChanged(object? sender, object e)
        => UpdateMemoryTargetLevel();

    private void UpdateSuspensionState()
    {
        if (!ShouldSuspendWhenHidden)
        {
            _suspensionVersion++;
            ResumeIfSuspended();
            return;
        }

        if (_isSuspended)
        {
            return;
        }

        _ = SuspendAfterDelayAsync(++_suspensionVersion);
    }

    private async Task SuspendAfterDelayAsync(int version)
    {
        await Task.Delay(SuspendDelayMilliseconds);
        if (version != _suspensionVersion || !ShouldSuspendWhenHidden)
        {
            return;
        }

        try
        {
            if (Browser.CoreWebView2 is not { } core)
            {
                return;
            }

            _isSuspended = await core.TrySuspendAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void ResumeIfSuspended()
    {
        if (!_isSuspended)
        {
            return;
        }

        _isSuspended = false;
        if (_disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.Resume();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (Browser.CoreWebView2?.Source is { } source &&
            Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            Shields.SetHost(uri.Host);
        }

        RememberCurrentLocation();
        UpdateNavigationState();
    }

    private void OnNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (_disposed ||
            Browser.CoreWebView2 is not { } core ||
            !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        // A CDP script registered after NavigationStarting can lose the race
        // with the new document. For pages that need Brave scriptlets, cancel
        // once, register the script, and then reissue the same navigation.
        if (!string.Equals(e.Uri, _scriptPreparedNavigationUri, StringComparison.Ordinal) &&
            !e.IsRedirected &&
            HasInjectedScript(uri))
        {
            e.Cancel = true;
            _scriptPreparedNavigationUri = e.Uri;
            _ = PrepareDocumentScriptAndNavigateAsync(core, uri);
            return;
        }

        if (string.Equals(e.Uri, _scriptPreparedNavigationUri, StringComparison.Ordinal))
        {
            _scriptPreparedNavigationUri = null;
        }

        var previousHost = Shields.SiteHost;
        if (previousHost.Length > 0 &&
            !string.Equals(previousHost, uri.Host, StringComparison.OrdinalIgnoreCase) &&
            Shields.ShouldForgetOnLeave)
        {
            _ = ClearSiteDataAsync(previousHost);
        }

        Shields.SetHost(uri.Host);

        if (Shields.ShouldBlockAllCookies)
        {
            // WebView2 has no native "block all cookies" switch. Brave's mode
            // therefore clears the site's cookies before and after navigation.
            _ = ClearSiteCookiesAsync(uri.Host);
        }

        // Script settings must be ready before the new document starts.
        try
        {
            core.Settings.IsScriptEnabled = !Shields.ShouldBlockScripts;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        _ = ApplyShieldsPolicyAsync();

        if (Shields.ShouldUpgradeToHttps &&
            uri.Scheme == Uri.UriSchemeHttp &&
            !IsLocalAddress(uri))
        {
            e.Cancel = true;
            var secure = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1
            }.Uri;
            core.Navigate(secure.AbsoluteUri);
        }
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        UpdateNavigationState();
        if (Browser.CoreWebView2?.Source is { } source &&
            Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            Shields.SetHost(uri.Host);
        }
        _ = SetPageFocusModeAsync(_viewModel?.IsFocusMode == true);
        _ = ApplyShieldsPolicyAsync();
        if (Shields.ShouldBlockAllCookies &&
            Browser.CoreWebView2?.Source is { } currentSource &&
            Uri.TryCreate(currentSource, UriKind.Absolute, out var currentUri))
        {
            _ = ClearSiteCookiesAsync(currentUri.Host);
        }
    }

    /// <summary>
    /// Adds or removes the network filter immediately. It takes effect for
    /// requests made after the switch changes; the cosmetic CSS is applied to
    /// the current page as well, so already-rendered ad slots disappear.
    /// </summary>
    private void RefreshAdBlock()
    {
        if (_disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            if (EffectiveBlockAds && !_adBlockActive)
            {
                core.AddWebResourceRequestedFilter(
                    "*://*/*",
                    CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                core.WebResourceRequested += OnWebResourceRequested;
                _adBlockActive = true;
            }
            else if (!EffectiveBlockAds && _adBlockActive)
            {
                core.WebResourceRequested -= OnWebResourceRequested;
                core.RemoveWebResourceRequestedFilter(
                    "*://*/*",
                    CoreWebView2WebResourceContext.All,
                    CoreWebView2WebResourceRequestSourceKinds.All);
                _adBlockActive = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        _ = ApplyCosmeticAdBlockAsync();
    }

    private bool HasInjectedScript(Uri pageUri)
    {
        if (!EffectiveBlockAds || BraveAdBlockEngine.Shared is not { } engine)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(
            engine.GetCosmeticResources(pageUri)?.InjectedScript);
    }

    private async Task PrepareDocumentScriptAndNavigateAsync(CoreWebView2 core, Uri pageUri)
    {
        await RegisterDocumentScriptAsync(core, pageUri);
        if (!_disposed)
        {
            // The cancelled navigation needs to settle before WebView2 accepts
            // the replacement navigation for the same URL.
            await Task.Delay(50);
            core.Navigate(pageUri.AbsoluteUri);
        }
    }

    private async Task RegisterDocumentScriptAsync(CoreWebView2 core, Uri pageUri)
    {
        if (_disposed || !EffectiveBlockAds || BraveAdBlockEngine.Shared is not { } engine)
        {
            return;
        }

        var resources = engine.GetCosmeticResources(pageUri);
        if (string.IsNullOrWhiteSpace(resources?.InjectedScript))
        {
            return;
        }

        await _documentScriptGate.WaitAsync();
        try
        {
            if (_documentScriptId is { } previousId)
            {
                try
                {
                    await core.CallDevToolsProtocolMethodAsync(
                        "Page.removeScriptToEvaluateOnNewDocument",
                        JsonSerializer.Serialize(new { identifier = previousId }));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }

                _documentScriptId = null;
                _documentScriptUrl = null;
            }

            var source = resources.InjectedScript +
                "\n;window.__vrcBraveDocumentScript = document.readyState;";
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            var result = await core.CallDevToolsProtocolMethodAsync(
                "Page.addScriptToEvaluateOnNewDocument",
                JsonSerializer.Serialize(new { source }));
            using var document = JsonDocument.Parse(result);
            if (document.RootElement.TryGetProperty("identifier", out var identifier))
            {
                _documentScriptId = identifier.GetString();
                _documentScriptUrl = pageUri.AbsoluteUri;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _documentScriptGate.Release();
        }
    }

    private async Task ApplyCosmeticAdBlockAsync()
    {
        if (_disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            if (!EffectiveBlockAds)
            {
                await core.ExecuteScriptAsync(RemoveAdBlockCssScript);
                return;
            }

            // Cosmetic filtering is part of both Brave modes. The difference
            // between Standard and Aggressive is the first-party protection
            // level inside the engine, not whether selectors are applied.
            var pageUri = Browser.Source;
            var resources = pageUri is not null
                ? BraveAdBlockEngine.Shared?.GetCosmeticResources(pageUri)
                : null;
            var css = string.IsNullOrEmpty(resources?.Css)
                ? FallbackAdBlockCss
                : resources.Css + "\n" + FallbackAdBlockCss;
            var applyScript = ApplyAdBlockCssTemplate.Replace(
                "{0}",
                JsonSerializer.Serialize(css));
            await core.ExecuteScriptAsync(applyScript);

            if (!string.IsNullOrWhiteSpace(resources?.InjectedScript) &&
                (_documentScriptId is null ||
                 !string.Equals(
                     _documentScriptUrl,
                     pageUri?.AbsoluteUri,
                     StringComparison.Ordinal)))
            {
                await core.ExecuteScriptAsync(resources.InjectedScript);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void OnWebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (!EffectiveBlockAds || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
            {
                return;
            }

            // Shields leaves local addresses alone: a filter rule matching the
            // request path would otherwise treat a loopback server as third
            // party and block its downloads outright.
            if (IsLocalAddress(uri))
            {
                return;
            }

            ObservedAdBlockRequests++;

            // Use Brave's engine for every request so filter-list exceptions,
            // $domain rules, redirects, and removeparam rules behave exactly as
            // they do in Brave. The small host list is only a fallback if the
            // native engine is unavailable.
            var sourceUri = Browser.Source;
            var engine = BraveAdBlockEngine.Shared;
            if (engine is null)
            {
                if (!AdBlockList.ShouldBlock(uri))
                {
                    return;
                }

                BlockedAdBlockRequests++;
                Shields.IncrementBlocked();
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(),
                    403,
                    "Blocked by Brave Shields",
                    "Content-Type: text/plain");
                return;
            }

            var decision = engine.Check(uri, sourceUri, e.ResourceContext);
            if (!string.IsNullOrWhiteSpace(decision.Redirect))
            {
                BlockedAdBlockRequests++;
                Shields.IncrementBlocked();
                var fallbackType = e.ResourceContext switch
                {
                    CoreWebView2WebResourceContext.Script => "application/javascript",
                    CoreWebView2WebResourceContext.Image => "image/gif",
                    CoreWebView2WebResourceContext.Media => "video/mp4",
                    _ => "application/octet-stream"
                };
                var (body, contentType) = DecodeRedirectBody(decision.Redirect, fallbackType);
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(body),
                    200,
                    "OK",
                    $"Content-Type: {contentType}");
                return;
            }

            if (decision.Blocked)
            {
                BlockedAdBlockRequests++;
                Shields.IncrementBlocked();
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(),
                    403,
                    "Blocked by Brave Shields",
                    "Content-Type: text/plain");
                return;
            }

            if (!string.IsNullOrWhiteSpace(decision.RewrittenUrl) &&
                Uri.TryCreate(decision.RewrittenUrl, UriKind.Absolute, out var rewritten))
            {
                e.Request.Uri = rewritten.AbsoluteUri;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static (byte[] Body, string ContentType) DecodeRedirectBody(
        string redirect,
        string fallbackContentType)
    {
        if (!redirect.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return (Encoding.UTF8.GetBytes(redirect), fallbackContentType);
        }

        var comma = redirect.IndexOf(',');
        if (comma < 0)
        {
            return (Encoding.UTF8.GetBytes(redirect), fallbackContentType);
        }

        var metadata = redirect[5..comma];
        var payload = redirect[(comma + 1)..];
        var isBase64 = metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
        var contentType = metadata.Split(';')[0];
        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = fallbackContentType;
        }

        try
        {
            var body = isBase64
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            return (body, contentType);
        }
        catch (FormatException ex)
        {
            Debug.WriteLine(ex);
            return (Encoding.UTF8.GetBytes(redirect), fallbackContentType);
        }
    }

    private async Task ApplyShieldsPolicyAsync()
    {
        if (_disposed || Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.Settings.IsScriptEnabled = !Shields.ShouldBlockScripts;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        RefreshAdBlock();
        await ApplyCookieControlsAsync(core);
        await ApplyFingerprintProtectionAsync(core);
        await ApplyCosmeticAdBlockAsync();
    }

    private async Task ApplyCookieControlsAsync(CoreWebView2 core)
    {
        var enabled = Shields.ShouldBlockThirdPartyCookies;
        if (enabled == _cookieRestrictionActive && Shields.CookieMode == _cookieMode)
        {
            return;
        }

        try
        {
            if (!_networkEnabled)
            {
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                _networkEnabled = true;
            }

            await core.CallDevToolsProtocolMethodAsync(
                "Network.setCookieControls",
                JsonSerializer.Serialize(new { enableThirdPartyCookieRestriction = enabled }));
            _cookieRestrictionActive = enabled;
            _cookieMode = Shields.CookieMode;
        }
        catch (Exception ex)
        {
            // Older WebView2 runtimes do not expose this experimental command.
            Debug.WriteLine(ex);
        }

        if (Shields.ShouldBlockAllCookies && Shields.SiteHost.Length > 0)
        {
            await ClearSiteCookiesAsync(Shields.SiteHost);
        }
    }

    private async Task ApplyFingerprintProtectionAsync(CoreWebView2 core)
    {
        if (!Shields.ShouldBlockFingerprinting)
        {
            await RemoveFingerprintProtectionAsync(core);
            return;
        }

        var mode = Shields.FingerprintMode;
        if (_fingerprintScriptId is not null && _fingerprintScriptMode != mode)
        {
            await RemoveFingerprintProtectionAsync(core);
        }

        var script = mode == BraveFingerprintMode.Strict
            ? StrictFingerprintProtectionScript
            : FingerprintProtectionScript;

        if (_fingerprintScriptId is null)
        {
            try
            {
                var result = await core.CallDevToolsProtocolMethodAsync(
                    "Page.addScriptToEvaluateOnNewDocument",
                    JsonSerializer.Serialize(new { source = script }));
                using var document = JsonDocument.Parse(result);
                if (document.RootElement.TryGetProperty("identifier", out var identifier))
                {
                    _fingerprintScriptId = identifier.GetString();
                    _fingerprintScriptMode = mode;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        try
        {
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task RemoveFingerprintProtectionAsync(CoreWebView2 core)
    {
        if (_fingerprintScriptId is not { } removalId)
        {
            return;
        }

        try
        {
            await core.CallDevToolsProtocolMethodAsync(
                "Page.removeScriptToEvaluateOnNewDocument",
                JsonSerializer.Serialize(new { identifier = removalId }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        _fingerprintScriptId = null;
        _fingerprintScriptMode = null;
    }

    private async Task ClearSiteCookiesAsync(string host)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(host) ||
            Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync($"https://{host}/");
            foreach (var cookie in cookies)
            {
                core.CookieManager.DeleteCookie(cookie);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task ClearSiteDataAsync(string host)
    {
        if (_disposed ||
            string.IsNullOrWhiteSpace(host) ||
            Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            var cookies = await core.CookieManager.GetCookiesAsync($"https://{host}/");
            foreach (var cookie in cookies)
            {
                core.CookieManager.DeleteCookie(cookie);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        foreach (var origin in new[] { $"https://{host}", $"http://{host}" })
        {
            try
            {
                await core.CallDevToolsProtocolMethodAsync(
                    "Storage.clearDataForOrigin",
                    JsonSerializer.Serialize(new { origin, storageTypes = "all" }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }

    private static bool IsLocalAddress(Uri uri)
        => uri.IsLoopback ||
           uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    private void OnShieldsSettingsChanged(object? sender, EventArgs e)
        => _ = ApplyShieldsPolicyAsync();

    private void OnExternalShieldsChanged(object? sender, string host)
    {
        if (_disposed ||
            host.Length == 0 ||
            !string.Equals(host, Shields.SiteHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Shields.ReloadSettings();
        _ = ApplyShieldsPolicyAsync();
    }

    private void ShieldsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement placementTarget)
        {
            ShieldsPopup.PlacementTarget = placementTarget;
        }

        ShieldsPopup.IsOpen = true;
    }

    private void FilterLists_Click(object sender, RoutedEventArgs e)
        => Shields.ShowFilterLists = !Shields.ShowFilterLists;

    private void GlobalSettings_Click(object sender, RoutedEventArgs e)
        => Shields.ShowGlobalSettings = !Shields.ShowGlobalSettings;

    private void ResetSite_Click(object sender, RoutedEventArgs e)
    {
        Shields.ResetSiteToDefaults();
        _ = ApplyShieldsPolicyAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IEmbeddedBrowserHost.IsFocusMode))
        {
            _ = SetPageFocusModeAsync(_viewModel?.IsFocusMode == true);
            AnnounceFocusBrowserError();
        }
        else if (e.PropertyName == nameof(IEmbeddedBrowserHost.BrowserError))
        {
            AnnounceFocusBrowserError();
        }
        else if (e.PropertyName == nameof(IEmbeddedBrowserHost.BrowserUrl))
        {
            SyncFocusAddress();
        }
    }

    private void SyncFocusAddress()
    {
        if (_viewModel is not { } viewModel ||
            FocusAddressBox.IsKeyboardFocusWithin ||
            string.Equals(FocusAddressBox.Text, viewModel.BrowserUrl, StringComparison.Ordinal))
        {
            return;
        }

        // SetCurrentValue keeps the one-way binding intact when automation has
        // replaced the text without going through normal keyboard input.
        FocusAddressBox.SetCurrentValue(TextBox.TextProperty, viewModel.BrowserUrl);
    }

    private async Task SetPageFocusModeAsync(bool focused)
    {
        if (Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(
                $"window.__vrcFocusMode = {focused.ToString().ToLowerInvariant()};");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void UpdateNavigationState()
    {
        var core = Browser.CoreWebView2;
        CanGoBack = core?.CanGoBack == true;
        CanGoForward = core?.CanGoForward == true;
    }

    private void OnDocumentTitleChanged(object? sender, object e)
        => RememberCurrentLocation();

    private void RememberCurrentLocation()
    {
        if (Browser.CoreWebView2 is { Source: { Length: > 0 } source } core)
        {
            _viewModel?.SetBrowserLocation(source, core.DocumentTitle);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Browser.CoreWebView2 is not null &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Browser.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        try
        {
            e.Handled = true;

            string? selectedPath = null;
            if (DownloadPathProvider is { } pathProvider)
            {
                selectedPath = pathProvider(Path.GetFileName(e.ResultFilePath));
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    var suggestedName = string.IsNullOrWhiteSpace(e.ResultFilePath)
                        ? "download"
                        : Path.GetFileName(e.ResultFilePath);

                    var dialog = new SaveFileDialog
                    {
                        Title = "Save download",
                        FileName = suggestedName,
                        Filter = "All files (*.*)|*.*",
                        AddExtension = false,
                        OverwritePrompt = true
                    };

                    var owner = Window.GetWindow(this);
                    var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
                    if (accepted == true)
                    {
                        selectedPath = dialog.FileName;
                    }
                });
            }

            if (selectedPath is null)
            {
                e.Cancel = true;
                SetDownloadStatus("Download cancelled.");
                return;
            }

            e.ResultFilePath = selectedPath;
            _viewModel?.SetBrowserError(string.Empty);
            SetDownloadStatus($"Downloading {Path.GetFileName(selectedPath)}...");

            var operation = e.DownloadOperation;
            TrackDownload(operation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            e.Cancel = true;
            _viewModel?.SetBrowserError("Could not open the Save As dialog. The download was cancelled.");
            SetDownloadStatus("Download failed: could not open Save As.", true);
        }
    }

    private void ReportDownloadState(CoreWebView2DownloadOperation operation)
    {
        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                SetDownloadStatus(
                    $"Downloaded to {operation.ResultFilePath}",
                    downloadedPath: operation.ResultFilePath);
                break;
            case CoreWebView2DownloadState.Interrupted
                when operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled:
                SetDownloadStatus("Download cancelled.");
                break;
            case CoreWebView2DownloadState.Interrupted:
                SetDownloadStatus($"Download failed: {Describe(operation.InterruptReason)}.", true);
                break;
        }
    }

    private void TrackDownload(CoreWebView2DownloadOperation operation)
    {
        if (!_activeDownloads.Add(operation))
        {
            return;
        }

        operation.StateChanged += OnDownloadStateChanged;
        UpdateMemoryTargetLevel();
    }

    private void OnDownloadStateChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation)
        {
            return;
        }

        ReportDownloadState(operation);
        if (operation.State is not (CoreWebView2DownloadState.Completed or CoreWebView2DownloadState.Interrupted))
        {
            return;
        }

        operation.StateChanged -= OnDownloadStateChanged;
        _activeDownloads.Remove(operation);
        UpdateMemoryTargetLevel();
    }

    private void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => _viewModel?.SetDownloadStatus(message, isError, downloadedPath));
            return;
        }

        _viewModel?.SetDownloadStatus(message, isError, downloadedPath);
    }

    private static string Describe(CoreWebView2DownloadInterruptReason reason)
        => reason switch
        {
            CoreWebView2DownloadInterruptReason.FileAccessDenied => "access was denied",
            CoreWebView2DownloadInterruptReason.FileNoSpace => "the disk is full",
            CoreWebView2DownloadInterruptReason.FileNameTooLong => "the file name is too long",
            CoreWebView2DownloadInterruptReason.FileTooLarge => "the file is too large",
            CoreWebView2DownloadInterruptReason.FileMalicious => "security blocked the file",
            _ => reason.ToString()
        };

    private async void FocusAddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (!TryBuildAddressUri(FocusAddressBox.Text, out var uri))
        {
            _viewModel?.SetBrowserError("Enter a valid http or https address.");
            AnnounceFocusBrowserError();
            return;
        }

        try
        {
            if (await GetCoreAsync() is not { } core)
            {
                _viewModel?.SetBrowserError("The in-app browser could not start.");
                return;
            }

            _viewModel?.SetBrowserError(string.Empty);
            NavigateAndFocusPage(core, uri);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _viewModel?.SetBrowserError("The in-app browser could not navigate to that address.");
        }
    }

    private void NavigateAndFocusPage(CoreWebView2 core, Uri uri)
    {
        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
            if (e.IsSuccess)
            {
                FocusPage();
            }
        }

        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            core.Navigate(uri.AbsoluteUri);
        }
        catch
        {
            core.NavigationCompleted -= OnNavigationCompleted;
            throw;
        }
    }

    private void FocusPage()
    {
        Dispatcher.InvokeAsync(
            () =>
            {
                if (_viewModel?.IsFocusMode != true || !IsVisible)
                {
                    return;
                }

                Browser.Focus();
                SyncFocusAddress();
            },
            DispatcherPriority.Input);
    }

    private void AnnounceFocusBrowserError()
    {
        if (_viewModel?.IsFocusMode != true ||
            string.IsNullOrEmpty(_viewModel.BrowserError) ||
            _focusErrorAnnouncementScheduled)
        {
            return;
        }

        _focusErrorAnnouncementScheduled = true;
        Dispatcher.InvokeAsync(
            () =>
            {
                _focusErrorAnnouncementScheduled = false;
                if (_viewModel?.IsFocusMode != true ||
                    FocusAddressError.Text.Length == 0)
                {
                    return;
                }

                var peer = UIElementAutomationPeer.CreatePeerForElement(FocusAddressError);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            },
            DispatcherPriority.Background);
    }

    internal static bool TryBuildAddressUri(string? input, out Uri uri)
    {
        uri = null!;
        var text = input?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var candidate) ||
            (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    public void FocusAddressBar()
    {
        if (!FocusAddressBox.IsVisible)
        {
            return;
        }

        FocusAddressBox.Focus();
        Keyboard.Focus(FocusAddressBox);
        FocusAddressBox.SelectAll();
    }

    public bool TryCancelAddressEdit()
    {
        if (_viewModel?.IsFocusMode != true ||
            !FocusAddressBox.IsKeyboardFocusWithin)
        {
            return false;
        }

        var currentUrl = _viewModel.BrowserUrl;
        if (string.Equals(FocusAddressBox.Text, currentUrl, StringComparison.Ordinal))
        {
            return false;
        }

        FocusAddressBox.Text = currentUrl;
        FocusAddressBox.SelectAll();
        _viewModel.SetBrowserError(string.Empty);
        return true;
    }

    public void GoBack()
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    public void Reload()
    {
        Browser.CoreWebView2?.Reload();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
        => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e)
        => GoForward();

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => Reload();

    private void ExitFocus_Click(object sender, RoutedEventArgs e)
        => ExitFocus();

    private void OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_viewModel?.IsFocusMode != true)
        {
            return;
        }

        try
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "browser-focus-escape":
                    ExitFocus();
                    break;
                case "browser-focus-back":
                    GoBack();
                    break;
                case "browser-focus-forward":
                    GoForward();
                    break;
                case "browser-focus-refresh":
                    Reload();
                    break;
                case "browser-focus-address":
                    FocusAddressBar();
                    break;
            }
        }
        catch (InvalidOperationException)
        {
            // The page posted a non-string message; it is not one of our shortcuts.
        }
    }

    private void ExitFocus()
    {
        if (_viewModel?.IsFocusMode == true)
        {
            _viewModel.IsFocusMode = false;
        }
    }
}
