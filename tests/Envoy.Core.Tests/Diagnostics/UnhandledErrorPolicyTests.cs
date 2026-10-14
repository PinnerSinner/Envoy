using Envoy.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

public sealed class UnhandledErrorPolicyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-policy-" + Guid.NewGuid().ToString("N"));
    private readonly LogHub _hub = new();
    private readonly ListSink _sink = new();

    public UnhandledErrorPolicyTests() => _hub.AddSink(_sink);

    private string MarkerPath => Path.Combine(_folder, "last-crash.json");

    public void Dispose()
    {
        _hub.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private UnhandledErrorPolicy Policy(bool smoke = false, CrashLoopGuard? guard = null) =>
        new(_hub, () => smoke, () => MarkerPath, "1.2.3", guard);

    private static Exception Thrown(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    [Theory]
    [InlineData(UnhandledSource.Dispatcher)]
    [InlineData(UnhandledSource.WinForms)]
    public void Ui_error_logged_and_reported(UnhandledSource source)
    {
        var outcome = Policy().OnUiException(Thrown("secret detail sk-ant-abcdefghijk"), source);

        Assert.Equal(UiErrorAction.ShowProblem, outcome.Action);
        Assert.NotNull(outcome.Problem);
        Assert.Equal(ProblemText.UnexpectedTitle, outcome.Problem.Title);
        Assert.Equal(ProblemText.UnexpectedDetail, outcome.Problem.Detail);
        Assert.DoesNotContain("secret detail", outcome.Problem.Title + outcome.Problem.Detail, StringComparison.Ordinal);
        Assert.StartsWith("System.InvalidOperationException@", outcome.Problem.Key, StringComparison.Ordinal);

        var entry = Assert.Single(_sink.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(UnhandledErrorPolicy.Category, entry.Category);
        Assert.Contains("source=" + source, entry.Fields, StringComparison.Ordinal);
        Assert.Contains("secret detail", entry.Exception, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-abcdefghijk", entry.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public void Smoke_leaves_ui_error_unhandled()
    {
        var outcome = Policy(smoke: true).OnUiException(Thrown("x"), UnhandledSource.Dispatcher);
        Assert.Equal(UiErrorAction.LeaveUnhandled, outcome.Action);
        Assert.Null(outcome.Problem);
        Assert.Equal(LogLevel.Error, Assert.Single(_sink.Entries).Level);
    }

    [Fact]
    public void Reentrant_call_does_not_recurse()
    {
        var policy = Policy();
        UiErrorOutcome? inner = null;
        var reentering = new ReenteringSink(() => inner = policy.OnUiException(Thrown("inner"), UnhandledSource.Dispatcher));
        _hub.AddSink(reentering);

        var outer = policy.OnUiException(Thrown("outer"), UnhandledSource.Dispatcher);

        Assert.Equal(UiErrorAction.ShowProblem, outer.Action);
        Assert.NotNull(inner);
        Assert.Equal(UiErrorAction.LeaveUnhandled, inner.Action);
        Assert.Contains(_sink.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("while handling", StringComparison.Ordinal));
        Assert.Equal(1, reentering.Calls);

        // Not stuck: the next exception is handled normally.
        Assert.Equal(UiErrorAction.ShowProblem, policy.OnUiException(Thrown("later"), UnhandledSource.WinForms).Action);
    }

    [Fact]
    public void Unobserved_is_logged()
    {
        Policy().OnUnobserved(new AggregateException(Thrown("lost in a task")));
        var entry = Assert.Single(_sink.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Unobserved task exception", entry.Message, StringComparison.Ordinal);
        Assert.Contains("lost in a task", entry.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_crash_writes_marker_and_flushes()
    {
        _hub.Redactor.SetSecrets(["registered-secret-value"]);
        Policy().OnDomainException(Thrown("died with registered-secret-value"), isTerminating: true);

        var entry = Assert.Single(_sink.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Contains("isTerminating=True", entry.Fields, StringComparison.Ordinal);
        Assert.True(_sink.Flushes >= 1);

        var marker = CrashMarker.TryRead(MarkerPath);
        Assert.NotNull(marker);
        Assert.Equal("1.2.3", marker.Version);
        Assert.Equal("AppDomain", marker.Source);
        Assert.Equal("System.InvalidOperationException", marker.ExceptionType);
        Assert.Equal("died with [redacted]", marker.Message);
        Assert.DoesNotContain("registered-secret-value", File.ReadAllText(MarkerPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_crash_with_a_non_exception_object_never_throws()
    {
        var policy = new UnhandledErrorPolicy(_hub, () => false, () => null, "1.0.0");
        policy.OnDomainException("a string was thrown", isTerminating: false);
        policy.OnDomainException(null, isTerminating: true);
        Assert.Equal(2, _sink.Entries.Count);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public void Crash_loop_exits_after_threshold()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var policy = Policy(guard: new CrashLoopGuard(5, TimeSpan.FromSeconds(30), clock));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(UiErrorAction.ShowProblem, policy.OnUiException(Thrown("again"), UnhandledSource.Dispatcher).Action);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        var outcome = policy.OnUiException(Thrown("again"), UnhandledSource.Dispatcher);
        Assert.Equal(UiErrorAction.ExitCrashLoop, outcome.Action);
        Assert.NotNull(CrashMarker.TryRead(MarkerPath));
        Assert.Contains(_sink.Entries, e => e.Level == LogLevel.Critical);
    }

    private sealed class ReenteringSink(Action onFirstError) : ILogSink
    {
        public int Calls { get; private set; }

        public void Write(LogEntry entry)
        {
            if (entry.Level != LogLevel.Error || Calls > 0) return;
            Calls++;
            onFirstError();
        }

        public void Flush()
        {
        }
    }
}
