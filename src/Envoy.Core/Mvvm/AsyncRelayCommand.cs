using System.ComponentModel;
using System.Windows.Input;

namespace Envoy.Core.Mvvm;

/// <summary>
/// A command whose body is asynchronous. While the task runs, <see cref="IsRunning"/> is true, <see cref="CanExecute"/>
/// is false and further <see cref="Execute"/> calls are ignored. Every exception from the task, including
/// <see cref="OperationCanceledException"/>, goes to the required <c>onError</c> handler and is never rethrown, so a
/// button can never crash the app through an <c>async void</c> path.
/// </summary>
/// <remarks>
/// <see cref="ExecuteAsync"/> awaits without <c>ConfigureAwait(false)</c>: started on the UI thread, the state changes
/// and <c>onError</c> run back on the UI thread. If <c>onError</c> itself throws, that exception propagates from
/// <see cref="ExecuteAsync"/>; it is a programming error and is not caught.
/// </remarks>
public sealed class AsyncRelayCommand : ICommand, INotifyPropertyChanged
{
    private readonly Func<Task> _execute;
    private readonly Action<Exception> _onError;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    /// <exception cref="ArgumentNullException">execute or onError is null.</exception>
    public AsyncRelayCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _canExecute = canExecute;
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke() ?? true);

    /// <summary>Fire-and-forget wrapper over <see cref="ExecuteAsync"/>.</summary>
    public void Execute(object? parameter) => _ = ExecuteAsync();

    /// <summary>Runs the command unless it is already running or disabled. Never throws (see the class remarks).</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        IsRunning = true;
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
