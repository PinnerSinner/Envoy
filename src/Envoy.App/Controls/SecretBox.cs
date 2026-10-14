using System.Windows;
using System.Windows.Controls;

namespace Envoy.App.Controls;

/// <summary>Makes PasswordBox.Password bindable (it isn't a dependency property, deliberately).</summary>
public static class SecretBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(SecretBox),
        // Default null so the first bound value (even "") raises a change and hooks the event.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(SecretBox));

    public static string GetValue(DependencyObject d) => (string)d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, string value) => d.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(HookedProperty))
        {
            box.SetValue(HookedProperty, true);
            box.PasswordChanged += (_, _) => SetValue(box, box.Password);
        }
        var value = e.NewValue as string ?? "";
        if (box.Password != value) box.Password = value;
    }
}
