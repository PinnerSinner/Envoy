using System.Globalization;
using Envoy.Core.Diagnostics;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Translation;

/// <summary>
/// The <c>Translation</c> log subscription (contract §2.1, REQ-TRR-16, 26): every provider state transition and every
/// attempt at Info. Fields carry provider ids, states, kinds, status codes and timings, never chat text or error text.
/// The provider and the state (or outcome) are part of the message, so 02's repeat suppressor collapses only true
/// repeats (a run of successes from one provider), never two different transitions.
/// </summary>
public static class TranslationLogging
{
    public const string TransitionMessage = "Provider state";
    public const string AttemptMessage = "Translation attempt";

    /// <summary>Subscribes <paramref name="log"/> to <paramref name="runtime"/>; dispose the result to unsubscribe.</summary>
    public static IDisposable Attach(TranslationRuntime runtime, ILog log)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        return new Subscription(runtime, log);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly TranslationRuntime _runtime;
        private readonly ILog _log;
        private int _disposed;

        public Subscription(TranslationRuntime runtime, ILog log)
        {
            _runtime = runtime;
            _log = log;
            runtime.ProviderHealthChanged += OnTransition;
            runtime.Diagnostic += OnDiagnostic;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _runtime.ProviderHealthChanged -= OnTransition;
            _runtime.Diagnostic -= OnDiagnostic;
        }

        private void OnTransition(ProviderHealthSnapshot s) =>
            _log.Info(TransitionMessage + " " + s.ProviderId + " " + s.State,
                LogField.Of("provider", s.ProviderId),
                LogField.Of("state", s.State),
                LogField.Of("kind", s.LastErrorKind?.ToString() ?? "none"),
                LogField.Of("retry_at", s.RetryAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "none"));

        private void OnDiagnostic(TranslationDiagnostic d) =>
            _log.Info(AttemptMessage + " " + d.ProviderId + " " + d.Outcome,
                LogField.Of("provider", d.ProviderId),
                LogField.Of("outcome", d.Outcome),
                LogField.Of("kind", d.Kind?.ToString() ?? "none"),
                LogField.Of("status", d.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "none"),
                LogField.Of("attempt", d.Attempt),
                LogField.Of("elapsed_ms", (long)d.Elapsed.TotalMilliseconds),
                LogField.Of("lane", d.Lane));
    }
}
