using System.Threading.Tasks;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Low-level input/navigation hooks used by app tabs that need to drive a site's
/// own interface. Implemented by the embedded browser control.
/// </summary>
public interface IEmbeddedBrowserAutomation
{
    Task SendKeyAsync(string key, string code, int virtualKeyCode, bool control = false, bool shift = false);

    Task InsertTextAsync(string text);

    Task<string> ExecuteScriptAsync(string script);
}

/// <summary>
/// A browser host that accepts a control-owned automation bridge while its
/// WebView2 is alive.
/// </summary>
public interface IEmbeddedBrowserAutomationHost
{
    IEmbeddedBrowserAutomation? Automation { get; set; }
}
