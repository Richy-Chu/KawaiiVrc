using System.Windows;
using System.Windows.Controls;

namespace VrcCompanion.Controls;

/// <summary>
/// Gives the native PasswordBox a bindable value without exposing a plain
/// TextBox or breaking the control's built-in masking behavior.
/// </summary>
public static class PasswordBoxBinding
{
    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxBinding),
            new PropertyMetadata(false, OnBindPasswordChanged));

    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxBinding),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBinding),
            new PropertyMetadata(false));

    public static bool GetBindPassword(DependencyObject element)
        => (bool)element.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject element, bool value)
        => element.SetValue(BindPasswordProperty, value);

    public static string GetBoundPassword(DependencyObject element)
        => (string)element.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject element, string value)
        => element.SetValue(BoundPasswordProperty, value ?? string.Empty);

    private static void OnBindPasswordChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not PasswordBox box)
        {
            return;
        }

        if (e.OldValue is true)
        {
            box.PasswordChanged -= OnPasswordChanged;
        }

        if (e.NewValue is true)
        {
            box.PasswordChanged += OnPasswordChanged;
        }
    }

    private static void OnBoundPasswordChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not PasswordBox box ||
            box.GetValue(IsUpdatingProperty) is true)
        {
            return;
        }

        var value = e.NewValue as string ?? string.Empty;
        if (string.Equals(box.Password, value, StringComparison.Ordinal))
        {
            return;
        }

        box.SetValue(IsUpdatingProperty, true);
        box.Password = value;
        box.SetValue(IsUpdatingProperty, false);
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box ||
            box.GetValue(IsUpdatingProperty) is true)
        {
            return;
        }

        box.SetValue(IsUpdatingProperty, true);
        box.SetCurrentValue(BoundPasswordProperty, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}