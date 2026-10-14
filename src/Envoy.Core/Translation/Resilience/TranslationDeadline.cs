namespace Envoy.Core.Translation.Resilience;

/// <summary>
/// The overall deadline of the current translation call (REQ-TRR-6). <see cref="FallbackTranslator"/> sets it for the
/// length of a call so <see cref="ResilientTranslator"/> knows the time left without changing <see cref="ITranslator"/>.
/// It flows with the async call (spec Risk 7: invisible to work started on a fresh execution context).
/// </summary>
internal static class TranslationDeadline
{
    private static readonly AsyncLocal<DateTimeOffset?> Value = new();

    public static DateTimeOffset? Current => Value.Value;

    /// <summary>Sets the deadline until the returned scope is disposed.</summary>
    public static Scope Begin(DateTimeOffset at)
    {
        var previous = Value.Value;
        Value.Value = previous is { } outer && outer < at ? outer : at;
        return new Scope(previous);
    }

    /// <summary>Time left before the deadline (never negative), or null when no deadline is set.</summary>
    public static TimeSpan? Remaining(TimeProvider clock)
    {
        if (Value.Value is not { } at) return null;
        var left = at - clock.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public readonly struct Scope(DateTimeOffset? previous) : IDisposable
    {
        public void Dispose() => Value.Value = previous;
    }
}
