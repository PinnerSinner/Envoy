using System.Windows;

namespace Envoy.App.Controls;

/// <summary>
/// Marks controls that are not settings, so editing them doesn't trigger MainWindow's debounced
/// <c>AppHost.ApplySettings()</c>. The value is inherited: set it on a container to exclude everything inside it.
/// </summary>
public static class AutoApply
{
    public static readonly DependencyProperty IgnoreProperty = DependencyProperty.RegisterAttached(
        "Ignore", typeof(bool), typeof(AutoApply), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIgnore(DependencyObject d) => (bool)d.GetValue(IgnoreProperty);

    public static void SetIgnore(DependencyObject d, bool value) => d.SetValue(IgnoreProperty, value);
}
