namespace Envoy.Core.Translation;

/// <summary>Counts characters sent to each provider per (local) day so paid providers can be capped.</summary>
public sealed class UsageTracker(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateOnly _day;
    private readonly Dictionary<string, int> _today = new();

    public UsageSnapshot Snapshot()
    {
        lock (_gate)
        {
            RollOver();
            return new UsageSnapshot(_day, new Dictionary<string, int>(_today));
        }
    }

    public void Restore(UsageSnapshot? snapshot)
    {
        if (snapshot is null) return;
        lock (_gate)
        {
            _day = snapshot.Day;
            _today.Clear();
            foreach (var (k, v) in snapshot.Characters) _today[k] = v;
            RollOver();
        }
    }

    public int Today(string providerId)
    {
        lock (_gate)
        {
            RollOver();
            return _today.GetValueOrDefault(providerId);
        }
    }

    /// <summary>Reserves <paramref name="characters"/> for the provider or returns false if it would exceed the limit.</summary>
    public bool TryConsume(string providerId, int characters, int dailyLimit)
    {
        lock (_gate)
        {
            RollOver();
            var used = _today.GetValueOrDefault(providerId);
            if (dailyLimit > 0 && used + characters > dailyLimit) return false;
            _today[providerId] = used + characters;
            return true;
        }
    }

    /// <summary>Gives back <paramref name="characters"/> reserved for a call that never left the PC (REQ-TRR-22). Never below 0.</summary>
    public void Refund(string providerId, int characters)
    {
        if (characters <= 0) return;
        lock (_gate)
        {
            RollOver();
            if (!_today.TryGetValue(providerId, out var used)) return;
            _today[providerId] = Math.Max(0, used - characters);
        }
    }

    private void RollOver()
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        if (today == _day) return;
        _day = today;
        _today.Clear();
    }
}

public sealed record UsageSnapshot(DateOnly Day, Dictionary<string, int> Characters);

/// <summary>
/// Refuses to send more than the daily character limit to a metered provider. Characters are reserved once per logical
/// call (retries happen inside) and given back when the call never left the PC (REQ-TRR-22).
/// </summary>
/// <param name="onExceeded">Called with the next local midnight when the cap stops a call (health shows QuotaExceeded).</param>
public sealed class BudgetedTranslator(ITranslator inner, UsageTracker usage, Func<int> dailyLimit,
                                       Action<BudgetExceededException, DateTimeOffset>? onExceeded = null, TimeProvider? clock = null) : ITranslator
{
    public string Id => inner.Id;
    public bool IsMetered => inner.IsMetered;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!inner.IsMetered) return await inner.TranslateAsync(request, cancellationToken).ConfigureAwait(false);

        var limit = dailyLimit();
        var characters = request.Text.Length;
        if (!usage.TryConsume(inner.Id, characters, limit))
        {
            var exceeded = new BudgetExceededException(inner.Id, limit);
            onExceeded?.Invoke(exceeded, NextLocalMidnight(clock ?? TimeProvider.System));
            throw exceeded;
        }
        try
        {
            return await inner.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslationException ex) when (NeverLeftPc(ex))
        {
            usage.Refund(inner.Id, characters);
            throw;
        }
    }

    /// <summary>
    /// True when the request certainly never reached the provider: a local rejection, missing configuration, or a
    /// connection/name-resolution failure before sending. Timeouts and HTTP errors are not (the provider may bill them).
    /// </summary>
    public static bool NeverLeftPc(TranslationException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Kind switch
        {
            TranslationErrorKind.Unavailable or TranslationErrorKind.Configuration => true,
            TranslationErrorKind.Transient => error.InnerException is HttpRequestException
            {
                HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError,
            },
            _ => false,
        };
    }

    /// <summary>The start of the next local day, when the daily cap resets.</summary>
    public static DateTimeOffset NextLocalMidnight(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var local = clock.GetLocalNow();
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, clock.LocalTimeZone.GetUtcOffset(midnight));
    }
}
