using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Envoy.App.Services;

/// <summary>
/// The smoke test's runtime accessibility audit (spec 07b, REQ-OUXB-27 and 31): every visible, enabled, focusable
/// interactive control has a UI Automation name, and Tab reaches each of them. check_a11y.py checks the XAML; this
/// checks what is actually on screen, including templated items.
/// </summary>
internal static class AccessibilityAudit
{
    private const int MaxTabs = 200;

    /// <summary>The audited controls under <paramref name="root"/>: visible, enabled, focusable, not part of a control's template.</summary>
    public static IReadOnlyList<Control> Audited(DependencyObject root) =>
        [.. Descendants(root).OfType<Control>().Where(IsAudited)];

    /// <summary>"{Type} '{x:Name or nearest named ancestor}'" for each audited control whose automation name is empty.</summary>
    public static IReadOnlyList<string> FindUnnamed(DependencyObject root) =>
        [.. Audited(root).Where(c => string.IsNullOrWhiteSpace(UIElementAutomationPeer.CreatePeerForElement(c)?.GetName())).Select(Describe)];

    /// <summary>
    /// Focuses the first tab stop of <paramref name="window"/>, presses Tab (MoveFocus Next) up to 200 times, and returns
    /// the audited tab stops never reached; null when the window could not take keyboard focus (not active).
    /// </summary>
    public static IReadOnlyList<string>? FindUnreachable(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var eligible = Audited(window).Where(KeyboardNavigation.GetIsTabStop).ToList();
        window.Activate();
        if (!window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) || !IsIn(window, Keyboard.FocusedElement)) return null;

        var visited = new HashSet<DependencyObject>();
        var current = Keyboard.FocusedElement as DependencyObject;
        for (var i = 0; i < MaxTabs && current is not null && IsIn(window, current); i++)
        {
            if (!visited.Add(current)) break; // back at a visited control: Tab has wrapped around (Cycle)
            var next = new TraversalRequest(FocusNavigationDirection.Next);
            var moved = current switch
            {
                UIElement element => element.MoveFocus(next),
                ContentElement element => element.MoveFocus(next),
                _ => false,
            };
            if (!moved) break;
            current = Keyboard.FocusedElement as DependencyObject;
        }
        return [.. eligible.Where(c => !visited.Contains(c) && !GroupVisited(c, visited)).Select(Describe)];
    }

    private static bool IsIn(Window window, object? element) =>
        element is DependencyObject d && ReferenceEquals(Window.GetWindow(d), window);

    private static bool IsAudited(Control c) =>
        c is ButtonBase or TextBoxBase or PasswordBox or ComboBox or Slider
        && c.IsVisible && c.IsEnabled && c.Focusable
        && c.TemplatedParent is not Control; // a ComboBox's toggle or a Slider's repeat buttons belong to their owner

    /// <summary>Tab enters a radio group once; any visited member of the same group counts for the rest.</summary>
    private static bool GroupVisited(Control c, HashSet<DependencyObject> visited) =>
        c is RadioButton radio && visited.OfType<RadioButton>().Any(r => r.GroupName == radio.GroupName && ReferenceEquals(r.Parent, radio.Parent));

    private static string Describe(Control c)
    {
        DependencyObject? node = c;
        while (node is not null && (node is not FrameworkElement fe || string.IsNullOrEmpty(fe.Name))) node = VisualTreeHelper.GetParent(node);
        return $"{c.GetType().Name} '{(node as FrameworkElement)?.Name ?? "?"}'";
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node is not Visual and not System.Windows.Media.Media3D.Visual3D) continue;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}
