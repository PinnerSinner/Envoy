using Envoy.Core.Translation;

namespace Envoy.Core.Tests.Translation.Fakes;

/// <summary>
/// An <see cref="ITranslator"/> with a programmable sequence of results or exceptions, an optional gate, and in-flight
/// counters. When the script runs out it succeeds with "[target] text".
/// </summary>
internal sealed class ScriptedTranslator(string id = "scripted", bool metered = false) : ITranslator
{
    private readonly object _gate = new();
    private readonly Queue<Func<TranslationRequest, CancellationToken, Task<TranslationResult>>> _script = new();
    private int _calls;
    private int _inFlight;
    private int _peak;

    public string Id => id;
    public bool IsMetered => metered;

    public int Calls => Volatile.Read(ref _calls);
    public int InFlight => Volatile.Read(ref _inFlight);
    public int PeakInFlight => Volatile.Read(ref _peak);

    /// <summary>Applies to every call once the script is empty; null = succeed.</summary>
    public Func<TranslationRequest, CancellationToken, Task<TranslationResult>>? Default { get; set; }

    public ScriptedTranslator Then(Func<TranslationRequest, CancellationToken, Task<TranslationResult>> step)
    {
        lock (_gate) _script.Enqueue(step);
        return this;
    }

    public ScriptedTranslator Succeed(string? text = null) =>
        Then((r, _) => Task.FromResult(new TranslationResult(text ?? $"[{r.TargetLanguage}] {r.Text}", "zh-CN", id)));

    public ScriptedTranslator Fail(Exception ex) => Then((_, _) => Task.FromException<TranslationResult>(ex));

    public ScriptedTranslator Fail(TranslationErrorKind kind, int? status = null, TimeSpan? retryAfter = null) =>
        Fail(new TranslationException($"{id} failed ({kind}).", null, kind, status, retryAfter));

    /// <summary>Waits until <paramref name="gate"/> completes (or the call is cancelled), then succeeds.</summary>
    public ScriptedTranslator Wait(Task gate) => Then(async (r, ct) =>
    {
        await gate.WaitAsync(ct);
        return new TranslationResult($"[{r.TargetLanguage}] {r.Text}", "zh-CN", id);
    });

    /// <summary>Never answers; ends only when cancelled.</summary>
    public ScriptedTranslator Hang() => Then(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    });

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _inFlight);
        int peak;
        do
        {
            peak = Volatile.Read(ref _peak);
        }
        while (now > peak && Interlocked.CompareExchange(ref _peak, now, peak) != peak);
        try
        {
            Func<TranslationRequest, CancellationToken, Task<TranslationResult>>? step;
            lock (_gate) step = _script.Count > 0 ? _script.Dequeue() : Default;
            return step is null
                ? new TranslationResult($"[{request.TargetLanguage}] {request.Text}", "zh-CN", id)
                : await step(request, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
