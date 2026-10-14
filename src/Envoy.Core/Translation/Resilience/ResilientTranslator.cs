using Envoy.Core.Translation.Providers;

namespace Envoy.Core.Translation.Resilience;

/// <summary>
/// Wraps one provider for one logical call (spec §3): breaker → token bucket → lane queue → attempts with per-attempt
/// timeouts, retries with full-jitter backoff (or a short Retry-After), all inside the overall deadline. Records one
/// outcome per call in the breaker and health, and raises a <see cref="TranslationDiagnostic"/> per attempt.
/// </summary>
public sealed class ResilientTranslator : ITranslator
{
    private readonly ITranslator _inner;
    private readonly ProviderGuard _guard;
    private readonly TranslationLane _lane;
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;

    internal ResilientTranslator(ITranslator inner, ProviderGuard guard, TranslationLane lane, TimeProvider clock, Func<double>? jitter = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _lane = lane;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _jitter = jitter ?? Random.Shared.NextDouble;
    }

    public string Id => _inner.Id;
    public bool IsMetered => _inner.IsMetered;
    public TranslationLane Lane => _lane;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lane = _guard.For(_lane);
        var o = lane.Options;
        var callStart = _clock.GetTimestamp();

        // 1. Breaker: reject in well under a millisecond with no I/O.
        if (!_guard.Breaker.TryEnter(out var isProbe))
        {
            var reason = _guard.Breaker.OpenReason;
            throw Reject(reason == ProviderState.Unknown ? ProviderState.CoolingDown : reason, _guard.Breaker.OpenUntil, callStart);
        }

        var probeSettled = !isProbe;
        var holdsSlot = false;
        // The token we get is linked to the overall deadline: only the real caller's cancellation is "cancelled"
        // (nothing recorded); the deadline running out is a Timeout failure like any other (REQ-TRR-6, 16).
        bool CallerCancelled() => TranslationDeadline.CallerCancelled(cancellationToken);
        using var deadlineCts = TranslationDeadline.Remaining(_clock) is { } left ? new CancellationTokenSource(left, _clock) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts?.Token ?? CancellationToken.None);
        try
        {
            // 2. Token bucket (keyless providers): never wait longer than MaxQueueWait or the time left. Taken before the
            //    lane gate, so a request that cannot get a token in time falls through at once and a request waiting for a
            //    token holds no concurrency slot.
            var maxWait = Min(o.MaxQueueWait, TranslationDeadline.Remaining(_clock));
            bool gotToken;
            try
            {
                gotToken = await lane.Bucket.TakeAsync(maxWait, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!CallerCancelled())
            {
                throw DeadlineTimeout();
            }
            if (!gotToken) throw Reject(ProviderState.Throttled, null, callStart, local: true);

            // 3. Lane gate, bounded by the queue limit, the deadline and the caller's token.
            if (!lane.Gate.Wait(0, CancellationToken.None))
            {
                if (!lane.TryEnqueue()) throw Reject(ProviderState.Throttled, null, callStart, local: true, busy: true);
                try
                {
                    await lane.Gate.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!CallerCancelled())
                {
                    throw DeadlineTimeout();
                }
                finally
                {
                    lane.Dequeue();
                }
            }
            holdsSlot = true;

            // 4. Attempts.
            var maybeSent = false;
            TranslationException error;
            for (var attempt = 1; ; attempt++)
            {
                var attemptStart = _clock.GetTimestamp();
                var timeout = Min(o.AttemptTimeout, TranslationDeadline.Remaining(_clock));
                if (timeout <= TimeSpan.Zero)
                {
                    error = DeadlineTimeout();
                    break;
                }

                using (var attemptCts = new CancellationTokenSource(timeout, _clock))
                using (var attemptLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, attemptCts.Token))
                {
                    try
                    {
                        var result = await _inner.TranslateAsync(request, attemptLinked.Token).ConfigureAwait(false);
                        var latency = _clock.GetElapsedTime(attemptStart);
                        _guard.Breaker.OnSuccess();
                        _guard.Health.RecordSuccess(latency, firstAttempt: attempt == 1);
                        probeSettled = true;
                        Report("success", null, null, attempt, attemptStart);
                        return result;
                    }
                    catch (OperationCanceledException) when (CallerCancelled())
                    {
                        Report("cancelled", null, null, attempt, attemptStart);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        error = Classify(ex, deadlineCts?.IsCancellationRequested == true || TranslationDeadline.Remaining(_clock) == TimeSpan.Zero);
                    }
                }

                Report("failure", error.Kind, error.StatusCode, attempt, attemptStart);
                maybeSent |= !BudgetedTranslator.NeverLeftPc(error);
                var delay = RetryPolicy.NextDelay(error, attempt, o, TranslationDeadline.Remaining(_clock), _jitter());
                if (delay is not { } wait) break;
                try
                {
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!CallerCancelled())
                {
                    error = DeadlineTimeout();
                    break;
                }
            }

            RecordFailure(error);
            probeSettled = true;
            // An earlier attempt may have reached the provider (and been billed): never let a later pre-send failure
            // make the whole call look refundable (REQ-TRR-22).
            if (maybeSent && BudgetedTranslator.NeverLeftPc(error))
                throw new TranslationException(error.Message, error, error.Kind, error.StatusCode, error.RetryAfter);
            throw error;
        }
        finally
        {
            if (holdsSlot) lane.Gate.Release();
            if (!probeSettled) _guard.Breaker.OnProbeCancelled();
        }
    }

    private void RecordFailure(TranslationException error)
    {
        var breaker = _guard.Breaker;
        breaker.OnFailure(error);
        ProviderState? state = error.Kind switch
        {
            TranslationErrorKind.Configuration => ProviderState.Unknown,
            TranslationErrorKind.Unsupported or TranslationErrorKind.Unavailable => null,
            _ => breaker.State == BreakerState.Closed ? ProviderState.Degraded : breaker.OpenReason,
        };
        var counted = error.Kind is not (TranslationErrorKind.Unsupported or TranslationErrorKind.Configuration or TranslationErrorKind.Unavailable);
        _guard.Health.RecordFailure(error, state, breaker.OpenUntil, counted);
    }

    /// <summary>
    /// A rejection without a network call. Health records the state (raising StateChanged on a transition), except for
    /// the reply box's own pacing (<paramref name="local"/> in the compose lane): that is Envoy's limit, not the
    /// provider's, so it leaves the shared health alone and says so (REQ-TRR-29).
    /// </summary>
    private ProviderUnavailableException Reject(ProviderState state, DateTimeOffset? retryAt, long callStart, bool local = false,
                                                bool busy = false)
    {
        var paced = local && _lane == TranslationLane.Compose;
        if (!paced) _guard.Health.RecordRejection(state, retryAt);
        Report("skipped", TranslationErrorKind.Unavailable, null, 0, callStart);
        var provider = ("provider", ProviderCatalog.NameOf(Id));
        var message = paced ? TranslationErrorText.Format(TranslationErrorText.SkippedComposePaced, provider)
            : busy ? TranslationErrorText.Format(TranslationErrorText.SkippedBusy, provider)
            : TranslationErrorText.ForSkip(state, Id);
        return new ProviderUnavailableException(Id, state, retryAt, message);
    }

    /// <summary>The overall deadline ran out while this provider was queued, waiting or answering.</summary>
    private TranslationException DeadlineTimeout() => TranslationErrorText.Error(TranslationErrorKind.Timeout, Id);

    private TranslationException Classify(Exception ex, bool deadlinePassed) => ex switch
    {
        TranslationException te => te,
        OperationCanceledException when deadlinePassed => DeadlineTimeout(),
        OperationCanceledException => TranslationErrorText.Error(TranslationErrorKind.Timeout, Id, ex),
        HttpRequestException or IOException => ProviderHttp.Wrap(ex, Id, CancellationToken.None)!,
        _ => TranslationErrorText.Error(TranslationErrorKind.Unknown, Id, ex),
    };

    private void Report(string outcome, TranslationErrorKind? kind, int? status, int attempt, long start) =>
        _guard.Report(new TranslationDiagnostic(Id, outcome, kind, status, attempt, _clock.GetElapsedTime(start), _lane));

    private static TimeSpan Min(TimeSpan a, TimeSpan? b) => b is { } x && x < a ? x : a;
}
