namespace Envoy.Core.Translation.Resilience;

/// <summary>
/// The overall deadline of the current translation call (REQ-TRR-6). <see cref="FallbackTranslator"/> sets it for the
/// length of a call so <see cref="ResilientTranslator"/> knows the time left without changing <see cref="ITranslator"/>.
/// It also carries the real caller's token: the token a provider receives is linked to the deadline, so only this one
/// tells "the caller gave up" (cancelled, nothing recorded) from "the deadline ran out" (a recorded Timeout).
/// It flows with the async call (spec Risk 7: invisible to work started on a fresh execution context).
/// </summary>
internal static class TranslationDeadline
{
    private static readonly AsyncLocal<State?> Value = new();

    public static DateTimeOffset? Current => Value.Value?.At;

    /// <summary>Sets the deadline (and the caller's token) until the returned scope is disposed.</summary>
    /// <param name="caller">The caller's own token, before it was linked to the deadline.</param>
    public static Scope Begin(DateTimeOffset at, CancellationToken caller = default)
    {
        var previous = Value.Value;
        // A nested call keeps the outer caller (the real one) and the earlier deadline.
        Value.Value = previous is { } outer
            ? new State(outer.At < at ? outer.At : at, outer.Caller)
            : new State(at, caller);
        return new Scope(previous);
    }

    /// <summary>Time left before the deadline (never negative), or null when no deadline is set.</summary>
    public static TimeSpan? Remaining(TimeProvider clock)
    {
        if (Value.Value is not { } state) return null;
        var left = state.At - clock.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// True when the real caller cancelled: the token given to <see cref="Begin"/> inside a deadline scope, else
    /// <paramref name="token"/> itself (no deadline, so the token is the caller's).
    /// </summary>
    public static bool CallerCancelled(CancellationToken token) =>
        Value.Value is { } state ? state.Caller.IsCancellationRequested : token.IsCancellationRequested;

    internal sealed record State(DateTimeOffset At, CancellationToken Caller);

    public readonly struct Scope : IDisposable
    {
        private readonly State? _previous;

        internal Scope(State? previous) => _previous = previous;

        public void Dispose() => Value.Value = _previous;
    }
}
