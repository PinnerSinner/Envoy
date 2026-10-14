using Envoy.Core.Diagnostics;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Translation;

/// <summary>
/// Tries each provider in order until one succeeds, so a rate-limited free endpoint never leaves you blind. Every call
/// ends within the overall deadline with a result or a classified <see cref="TranslationException"/> (REQ-TRR-6, 7):
/// a timeout moves on to the next provider, and <see cref="OperationCanceledException"/> escapes only when the
/// caller's own token was cancelled.
/// </summary>
public sealed class FallbackTranslator : ITranslator
{
    private readonly IReadOnlyList<ITranslator> _chain;
    private readonly Func<TimeSpan>? _deadline;
    private readonly TimeProvider _clock;
    private readonly LogRedactor _redactor;

    /// <param name="deadline">The overall deadline, read per call (from the runtime's options snapshot); null = none.</param>
    /// <param name="redactor">Masks the combined failure message (REQ-TRR-17); null = a redactor with no registered secrets.</param>
    public FallbackTranslator(IReadOnlyList<ITranslator> chain, Func<TimeSpan>? deadline = null, TimeProvider? clock = null,
                              LogRedactor? redactor = null)
    {
        _chain = chain ?? throw new ArgumentNullException(nameof(chain));
        _deadline = deadline;
        _clock = clock ?? TimeProvider.System;
        _redactor = redactor ?? new LogRedactor();
    }

    public string Id => _chain.Count > 0 ? _chain[0].Id : "none";
    public bool IsMetered => _chain.Any(t => t.IsMetered);

    public IReadOnlyList<ITranslator> Chain => _chain;

    /// <summary>Raised when a provider really fails and the next one is tried. Not raised for local skips (already in health).</summary>
    public event Action<string, Exception>? ProviderFailed;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (_chain.Count == 0) throw new TranslationException(TranslationErrorText.NoProviders, kind: TranslationErrorKind.Configuration);
        cancellationToken.ThrowIfCancellationRequested();

        var limit = _deadline?.Invoke();
        using var deadlineCts = limit is { } d ? new CancellationTokenSource(d, _clock) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts?.Token ?? CancellationToken.None);
        using var scope = limit is { } span ? TranslationDeadline.Begin(_clock.GetUtcNow() + span) : (TranslationDeadline.Scope?)null;

        var errors = new List<(string Id, Exception Error)>();
        foreach (var translator in _chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineCts?.IsCancellationRequested == true) throw OverallTimeout(limit!.Value, errors);
            try
            {
                return await translator.TranslateAsync(request, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // The deadline, or an HttpClient.Timeout inside the provider: a timeout, never a silent drop.
                var timeout = TranslationErrorText.Error(TranslationErrorKind.Timeout, translator.Id, ex);
                errors.Add((translator.Id, timeout));
                ProviderFailed?.Invoke(translator.Id, timeout);
                if (deadlineCts?.IsCancellationRequested == true) throw OverallTimeout(limit!.Value, errors);
            }
            catch (ProviderUnavailableException ex)
            {
                errors.Add((translator.Id, ex));
            }
            catch (Exception ex)
            {
                errors.Add((translator.Id, ex));
                ProviderFailed?.Invoke(translator.Id, ex);
                if (deadlineCts?.IsCancellationRequested == true) throw OverallTimeout(limit!.Value, errors);
            }
        }

        throw new TranslationException(CombinedMessage(errors), new AggregateException(errors.Select(e => e.Error)),
            CombinedKind(errors));
    }

    private static TranslationException OverallTimeout(TimeSpan limit, List<(string Id, Exception Error)> errors) =>
        new(TranslationErrorText.ForTimeout(limit), errors.Count == 0 ? null : new AggregateException(errors.Select(e => e.Error)),
            TranslationErrorKind.Timeout);

    /// <summary>
    /// "Translation unavailable. {each provider's whole-sentence reason}" — names, kinds and states only, never chat text,
    /// keys or bodies (REQ-TRR-17). Whole sentences that do not fit in <see cref="ProviderHealthText.MaxErrorLength"/>
    /// are left out rather than cut.
    /// </summary>
    private string CombinedMessage(List<(string Id, Exception Error)> errors)
    {
        var reasons = errors
            .Select(e => e.Error is TranslationException te ? te.Message : TranslationErrorText.ForKind(TranslationErrorKind.Unknown, e.Id))
            .Select(r => ProviderHealthText.Sanitize(r, _redactor))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var kept = new List<string>();
        foreach (var reason in reasons)
        {
            var candidate = Combined([.. kept, reason]);
            if (kept.Count > 0 && candidate.Length > ProviderHealthText.MaxErrorLength) break;
            kept.Add(reason);
        }
        return ProviderHealthText.Sanitize(Combined(kept), _redactor);

        static string Combined(List<string> parts) =>
            TranslationErrorText.Format(TranslationErrorText.AllFailed, ("reasons", string.Join(" ", parts)));
    }

    /// <summary>The kind every provider agreed on (Unavailable when all were skipped), else Unknown.</summary>
    private static TranslationErrorKind CombinedKind(List<(string Id, Exception Error)> errors)
    {
        var kinds = errors.Select(e => e.Error is TranslationException te ? te.Kind : TranslationErrorKind.Unknown).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : TranslationErrorKind.Unknown;
    }
}
