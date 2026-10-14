using System.Windows.Input;

namespace Envoy.Core.Mvvm;

/// <summary>
/// A synchronous command. It does not hook WPF's CommandManager (WPF-only), so callers raise
/// <see cref="RaiseCanExecuteChanged"/> themselves when the answer to <see cref="CanExecute"/> changes.
/// An exception from the action propagates to the caller, exactly as a click handler's would.
/// </summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A synchronous command with a typed parameter. A parameter that is neither a <typeparamref name="T"/> nor null makes
/// <see cref="CanExecute"/> false and <see cref="Execute"/> a no-op (never an <see cref="InvalidCastException"/>).
/// </summary>
public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private readonly Action<T?> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => TryConvert(parameter, out var value) && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (TryConvert(parameter, out var value) && (canExecute?.Invoke(value) ?? true)) _execute(value);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static bool TryConvert(object? parameter, out T? value)
    {
        switch (parameter)
        {
            case T typed:
                value = typed;
                return true;
            case null:
                value = default;
                return true;
            default:
                value = default;
                return false;
        }
    }
}
