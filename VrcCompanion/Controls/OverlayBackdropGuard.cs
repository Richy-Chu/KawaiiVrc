using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VrcCompanion.Controls;

/// <summary>
/// Every popup draws a backdrop over the whole window, so the backdrop appears
/// under the pointer that just opened it. The press that finishes that gesture -
/// the second press of a double-click, or a mouse button that chatters - then
/// lands on the backdrop and closes the popup before it can be read. A backdrop
/// protected by this guard ignores a press for the first
/// <see cref="GuardMilliseconds"/> it is visible, so the gesture that opened the
/// popup cannot also dismiss it. A deliberate click after that still closes.
/// </summary>
internal static class OverlayBackdropGuard
{
    /// <summary>How long a fresh backdrop ignores a press, even a slow double-click.</summary>
    internal const int GuardMilliseconds = 400;

    /// <summary>The clock behind the guard; tests move it instead of waiting.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    internal static void ResetClock() => Clock = static () => Environment.TickCount64;

    private static readonly DependencyProperty OpenedAtProperty =
        DependencyProperty.RegisterAttached(
            "OpenedAt",
            typeof(long),
            typeof(OverlayBackdropGuard),
            new PropertyMetadata(0L));

    /// <summary>Set on a popup's backdrop button to arm the guard.</summary>
    public static readonly DependencyProperty ProtectProperty =
        DependencyProperty.RegisterAttached(
            "Protect",
            typeof(bool),
            typeof(OverlayBackdropGuard),
            new PropertyMetadata(false, OnProtectChanged));

    public static void SetProtect(DependencyObject element, bool value)
        => element.SetValue(ProtectProperty, value);

    public static bool GetProtect(DependencyObject element)
        => (bool)element.GetValue(ProtectProperty);

    /// <summary>Whether a press arriving at <paramref name="now"/> belongs to the opening gesture.</summary>
    internal static bool IsOpeningPress(Button backdrop, long now)
    {
        var openedAt = (long)backdrop.GetValue(OpenedAtProperty);
        return openedAt != 0 && now - openedAt < GuardMilliseconds;
    }

    internal static void MarkOpened(Button backdrop)
        => backdrop.SetValue(OpenedAtProperty, Clock());

    private static void OnProtectChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Button backdrop)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            backdrop.IsVisibleChanged += OnVisibleChanged;
            backdrop.PreviewMouseDown += OnPreviewMouseDown;
            if (backdrop.IsVisible)
            {
                MarkOpened(backdrop);
            }

            return;
        }

        backdrop.IsVisibleChanged -= OnVisibleChanged;
        backdrop.PreviewMouseDown -= OnPreviewMouseDown;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Button { IsVisible: true } backdrop)
        {
            MarkOpened(backdrop);
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button backdrop || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (!IsOpeningPress(backdrop, Clock()))
        {
            return;
        }

        // Handled before ButtonBase can see it, so it cannot become a press and
        // the matching mouse-up cannot raise the close command.
        e.Handled = true;
    }
}
