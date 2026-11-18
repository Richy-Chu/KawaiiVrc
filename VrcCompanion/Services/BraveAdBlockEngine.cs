using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace VrcCompanion.Services;

/// <summary>
/// Managed wrapper around Brave's <c>adblock-rust</c> engine. The filter rules
/// are compiled into the bundled serialized engine, so the app ships the same
/// EasyList/EasyPrivacy/uBlock/Brave rule set Brave uses without requiring a
/// network download at startup.
/// </summary>
internal sealed class BraveAdBlockEngine : IDisposable
{
    private const string NativeLibrary = "vrc_companion_adblock";
    private static readonly Lazy<BraveAdBlockEngine?> SharedEngine = new(CreateBundled);
    private readonly object _gate = new();
    private IntPtr _handle;

    internal static string? LastCreationError { get; private set; }

    internal static bool ResourcesLoaded { get; private set; }

    private BraveAdBlockEngine(IntPtr handle)
        => _handle = handle;

    public static BraveAdBlockEngine? Shared => SharedEngine.Value;

    public static BraveAdBlockEngine? CreateBundled()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Resources",
            "adblock",
            "brave-engine.bin");
        if (!File.Exists(path))
        {
            LastCreationError = $"Brave Shields engine not found at {path}.";
            Debug.WriteLine(LastCreationError);
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var handle = NativeMethods.vrc_adblock_create_from_serialized(
                bytes,
                (nuint)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                LastCreationError = "Brave Shields engine could not deserialize its filter lists.";
                Debug.WriteLine(LastCreationError);
                return null;
            }

            var resourcesPath = Path.Combine(
                AppContext.BaseDirectory,
                "Resources",
                "adblock",
                "brave-resources.json");
            ResourcesLoaded = File.Exists(resourcesPath) &&
                NativeMethods.vrc_adblock_use_resources(
                    handle,
                    File.ReadAllText(resourcesPath)) == 1;
            if (!ResourcesLoaded)
            {
                Debug.WriteLine("Brave Shields resources could not be loaded; continuing without scriptlets.");
            }

            LastCreationError = null;
            return new BraveAdBlockEngine(handle);
        }
        catch (Exception ex)
        {
            LastCreationError = ex.ToString();
            Debug.WriteLine(ex);
            return null;
        }
    }

    public bool ShouldBlock(
        Uri requestUri,
        Uri? sourceUri,
        CoreWebView2WebResourceContext context)
        => ShouldBlock(requestUri, sourceUri, GetRequestType(context));

    internal BraveAdBlockResult Check(
        Uri requestUri,
        Uri? sourceUri,
        CoreWebView2WebResourceContext context)
        => Check(requestUri, sourceUri, GetRequestType(context));

    private static string GetRequestType(CoreWebView2WebResourceContext context)
        => context switch
        {
            CoreWebView2WebResourceContext.Document => "document",
            CoreWebView2WebResourceContext.Stylesheet => "stylesheet",
            CoreWebView2WebResourceContext.Image => "image",
            CoreWebView2WebResourceContext.Media => "media",
            CoreWebView2WebResourceContext.Font => "font",
            CoreWebView2WebResourceContext.Script => "script",
            CoreWebView2WebResourceContext.XmlHttpRequest => "xmlhttprequest",
            CoreWebView2WebResourceContext.Fetch => "xmlhttprequest",
            _ => "other"
        };

    internal bool ShouldBlock(Uri requestUri, Uri? sourceUri, string requestType)
        => Check(requestUri, sourceUri, requestType).Blocked;

    internal BraveAdBlockResult Check(Uri requestUri, Uri? sourceUri, string requestType)
    {
        if (requestUri is null ||
            (requestUri.Scheme != Uri.UriSchemeHttp &&
             requestUri.Scheme != Uri.UriSchemeHttps))
        {
            return BraveAdBlockResult.Empty;
        }

        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                return BraveAdBlockResult.Empty;
            }

            var pointer = NativeMethods.vrc_adblock_check_json(
                _handle,
                requestUri.AbsoluteUri,
                sourceUri?.AbsoluteUri ?? string.Empty,
                requestType);
            if (pointer == IntPtr.Zero)
            {
                return BraveAdBlockResult.Empty;
            }

            try
            {
                var json = Marshal.PtrToStringUTF8(pointer);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return BraveAdBlockResult.Empty;
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                return new BraveAdBlockResult(
                    root.TryGetProperty("blocked", out var blocked) && blocked.GetBoolean(),
                    GetString(root, "redirect"),
                    GetString(root, "rewrittenUrl"),
                    GetString(root, "csp"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return BraveAdBlockResult.Empty;
            }
            finally
            {
                NativeMethods.vrc_adblock_free_string(pointer);
            }
        }
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value)
            ? value.GetString()
            : null;

    internal BraveCosmeticResources? GetCosmeticResources(Uri pageUri)
    {
        if (pageUri is null ||
            (pageUri.Scheme != Uri.UriSchemeHttp &&
             pageUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = NativeMethods.vrc_adblock_cosmetic(
                _handle,
                pageUri.AbsoluteUri);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var json = Marshal.PtrToStringUTF8(pointer);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var selectors = new System.Collections.Generic.List<string>();
                if (root.TryGetProperty("hideSelectors", out var hideSelectors))
                {
                    foreach (var selector in hideSelectors.EnumerateArray())
                    {
                        if (selector.GetString() is { Length: > 0 } value)
                        {
                            selectors.Add(value);
                        }
                    }
                }

                var css = selectors.Count == 0
                    ? string.Empty
                    : string.Join(",\n", selectors) +
                      " { display: none !important; }";
                var script = root.TryGetProperty("injectedScript", out var injectedScript)
                    ? injectedScript.GetString() ?? string.Empty
                    : string.Empty;

                return new BraveCosmeticResources(css, script);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }
            finally
            {
                NativeMethods.vrc_adblock_free_string(pointer);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.vrc_adblock_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private static class NativeMethods
    {
        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vrc_adblock_create_from_serialized(
            byte[] data,
            nuint length);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vrc_adblock_use_resources(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string resourcesJson);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int vrc_adblock_check(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceUrl,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestType);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vrc_adblock_check_json(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceUrl,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string requestType);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr vrc_adblock_cosmetic(
            IntPtr handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string pageUrl);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void vrc_adblock_free_string(IntPtr value);

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void vrc_adblock_destroy(IntPtr handle);
    }
}

internal sealed record BraveCosmeticResources(string Css, string InjectedScript);

internal sealed record BraveAdBlockResult(
    bool Blocked,
    string? Redirect,
    string? RewrittenUrl,
    string? Csp)
{
    public static BraveAdBlockResult Empty { get; } = new(false, null, null, null);
}
