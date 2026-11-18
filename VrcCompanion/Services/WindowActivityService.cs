using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VrcCompanion.Services;

public sealed record WindowActivityInfo(string App, string Title)
{
    public string DisplayText => string.IsNullOrWhiteSpace(Title)
        ? App
        : $"{App} — {Title}";
}

public interface IWindowActivityService
{
    WindowActivityInfo? GetCurrent();
}

/// <summary>
/// Reads the active foreground window locally. It never sends window titles
/// anywhere; callers decide which parts to include in a chatbox line.
/// </summary>
public sealed class WindowActivityService : IWindowActivityService
{
    public WindowActivityInfo? GetCurrent()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var app = process.ProcessName;
            if (string.IsNullOrWhiteSpace(app))
            {
                return null;
            }

            var length = GetWindowTextLength(handle);
            var title = new StringBuilder(Math.Max(length + 1, 1));
            GetWindowText(handle, title, title.Capacity);
            return new WindowActivityInfo(app, title.ToString().Trim());
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
