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

    private void RollOver()
    {
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
        if (today == _day) return;
        _day = today;
        _today.Clear();
    }
}

public sealed record UsageSnapshot(DateOnly Day, Dictionary<string, int> Characters);

/// <summary>Refuses to send more than the daily character limit to a metered provider.</summary>
public sealed class BudgetedTranslator(ITranslator inner, UsageTracker usage, Func<int> dailyLimit) : ITranslator
{
    public string Id => inner.Id;
    public bool IsMetered => inner.IsMetered;

    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var limit = dailyLimit();
        if (inner.IsMetered && !usage.TryConsume(inner.Id, request.Text.Length, limit))
            throw new BudgetExceededException(inner.Id, limit);
        return inner.TranslateAsync(request, cancellationToken);
    }
}
