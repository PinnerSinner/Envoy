using System.Windows.Threading;

namespace Envoy.App.Services;

/// <summary>Debounces settings edits: the apply action runs once, <c>delay</c> after the last edit.</summary>
internal sealed class SettingsAutoApplier
{
    private readonly DispatcherTimer _timer;
    private readonly Action _apply;

    /// <param name="apply">AppHost.ApplySettings() plus the main view model's refreshes.</param>
    public SettingsAutoApplier(Dispatcher dispatcher, TimeSpan delay, Action apply)
    {
        _apply = apply;
        _timer = new DispatcherTimer(delay, DispatcherPriority.Background, (_, _) => ApplyNow(), dispatcher) { IsEnabled = false };
    }

    public bool IsPending => _timer.IsEnabled;

    /// <summary>(Re)starts the delay.</summary>
    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Stops the timer and applies unconditionally (before a test translation or test voice).</summary>
    public void ApplyNow()
    {
        _timer.Stop();
        _apply();
    }

    /// <summary>Applies only if an edit is waiting (window close).</summary>
    public void FlushIfPending()
    {
        if (IsPending) ApplyNow();
    }
}
