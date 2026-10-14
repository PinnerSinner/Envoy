using System.ComponentModel;
using Envoy.Core.Mvvm;
using Xunit;

namespace Envoy.Core.Tests;

public class MvvmTests
{
    [Fact]
    public void RelayCommand_Execute_InvokesAction()
    {
        var calls = 0;
        var command = new RelayCommand(() => calls++);
        command.Execute(null);
        Assert.Equal(1, calls);
        command.Execute(null);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RelayCommand_CanExecuteFalse_ExecuteIsNoOp()
    {
        var calls = 0;
        var command = new RelayCommand(() => calls++, () => false);
        Assert.False(command.CanExecute(null));
        command.Execute(null);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RelayCommand_RaiseCanExecuteChanged_RaisesEvent()
    {
        var command = new RelayCommand(() => { });
        var senders = new List<object?>();
        command.CanExecuteChanged += (s, _) => senders.Add(s);
        command.RaiseCanExecuteChanged();
        Assert.Same(command, Assert.Single(senders));
    }

    [Fact]
    public void RelayCommandT_PassesTypedParameter()
    {
        string? received = null;
        var command = new RelayCommand<string>(p => received = p);
        Assert.True(command.CanExecute("ko"));
        command.Execute("ko");
        Assert.Equal("ko", received);
    }

    [Fact]
    public void RelayCommandT_WrongParameterType_CanExecuteFalseAndNoThrow()
    {
        var calls = 0;
        var command = new RelayCommand<string>(_ => calls++);
        Assert.False(command.CanExecute(42));
        command.Execute(42);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void RelayCommandT_NullParameter_PassesDefault()
    {
        var received = "unset";
        var command = new RelayCommand<string>(p => received = p);
        command.Execute(null);
        Assert.Null(received);
    }

    [Fact]
    public void AsyncRelayCommand_NullOnError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AsyncRelayCommand(() => Task.CompletedTask, null!));
        Assert.Throws<ArgumentNullException>(() => new AsyncRelayCommand(null!, _ => { }));
    }

    [Fact]
    public async Task AsyncRelayCommand_WhileRunning_CanExecuteFalseAndIsRunningTrue()
    {
        var gate = new TaskCompletionSource();
        var command = new AsyncRelayCommand(() => gate.Task, _ => { });
        Assert.True(command.CanExecute(null));
        Assert.False(command.IsRunning);

        var running = command.ExecuteAsync();
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));

        gate.SetResult();
        await running;
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AsyncRelayCommand_ReentrantExecute_Ignored()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var command = new AsyncRelayCommand(() => { calls++; return gate.Task; }, _ => { });

        var first = command.ExecuteAsync();
        command.Execute(null);
        await command.ExecuteAsync();
        Assert.Equal(1, calls);

        gate.SetResult();
        await first;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AsyncRelayCommand_RaisesCanExecuteChanged_OnStartAndFinish()
    {
        var command = new AsyncRelayCommand(() => Task.Delay(1), _ => { });
        var raised = 0;
        var isRunningChanges = 0;
        command.CanExecuteChanged += (_, _) => raised++;
        ((INotifyPropertyChanged)command).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning)) isRunningChanges++;
        };
        await command.ExecuteAsync();
        Assert.True(raised >= 2);
        Assert.Equal(2, isRunningChanges);
    }

    [Fact]
    public async Task AsyncRelayCommand_Exception_RoutedToOnError_NotThrown()
    {
        var boom = new InvalidOperationException("boom");
        Exception? reported = null;
        var command = new AsyncRelayCommand(async () => { await Task.Yield(); throw boom; }, ex => reported = ex);

        await command.ExecuteAsync();

        Assert.Same(boom, reported);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AsyncRelayCommand_SynchronousThrow_RoutedToOnError()
    {
        Exception? reported = null;
        var command = new AsyncRelayCommand(() => throw new InvalidOperationException("sync"), ex => reported = ex);
        await command.ExecuteAsync();
        Assert.IsType<InvalidOperationException>(reported);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task AsyncRelayCommand_Cancellation_RoutedToOnError()
    {
        Exception? reported = null;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var command = new AsyncRelayCommand(() => Task.Delay(1000, cts.Token), ex => reported = ex);

        await command.ExecuteAsync();

        Assert.IsAssignableFrom<OperationCanceledException>(reported);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task AsyncRelayCommand_ThrowingOnError_Propagates()
    {
        var command = new AsyncRelayCommand(() => throw new InvalidOperationException(), _ => throw new NotSupportedException());
        await Assert.ThrowsAsync<NotSupportedException>(command.ExecuteAsync);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public void ObservableObject_SetProperty_RaisesOnlyOnChange()
    {
        var model = new Sample();
        var names = new List<string?>();
        model.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        Assert.True(model.Set("a"));
        Assert.False(model.Set("a"));
        Assert.True(model.Set("b"));

        Assert.Equal([nameof(Sample.Value), nameof(Sample.Value)], names);
        Assert.Equal("b", model.Value);
    }

    private sealed class Sample : ObservableObject
    {
        private string? _value;
        public string? Value => _value;
        public bool Set(string? value) => SetProperty(ref _value, value, nameof(Value));
    }
}
