using System.Globalization;
using Envoy.Core.Diagnostics;
using Envoy.Core.Text;

namespace Envoy.Core.Translation.Resilience;

/// <summary>
/// Health text for the Translation page and the War Room status line (contract §8.7): whole-sentence templates with
/// named placeholders. Built only from provider names, states and times, never from error text.
/// </summary>
public static class ProviderHealthText
{
    /// <summary>Longest <see cref="ProviderHealthSnapshot.LastError"/> (REQ-TRR-17).</summary>
    public const int MaxErrorLength = 160;

    public const string NotUsedYet = "Not used yet.";
    public const string NotSetUp = "Not set up.";
    public const string Working = "Working.";
    public const string WorkingWithLatency = "Working · {latency} ms";
    public const string Degraded = "Working, but some requests failed.";
    public const string DegradedWithLatency = "Working, but some requests failed · {latency} ms";
    public const string ThrottledFor = "Skipping for {seconds} s: it is limiting requests.";
    public const string Throttled = "It was limiting requests. Envoy tries it again with the next message.";
    public const string CoolingDownFor = "Skipping for {seconds} s after repeated failures.";
    public const string CoolingDown = "It failed repeatedly. Envoy tries it again with the next message.";
    public const string AuthFailed = "Your key was rejected. Check it above.";
    public const string QuotaFor = "Its quota is used up. Envoy tries it again in {minutes} min.";
    public const string QuotaHoursFor = "Its quota is used up. Envoy tries it again in {hours} h.";
    public const string Quota = "Its quota is used up.";

    public const string Using = "Using {provider}.";
    public const string UsingFallback = "{primary} is unavailable, so Envoy is using {active}.";
    public const string NoneAvailable = "No translation provider is available right now. Envoy keeps trying.";

    public const string RetryButton = "Retry {provider} now";

    public const string LastSuccessNever = "No translation yet.";
    public const string LastSuccessSeconds = "Last translation {seconds} s ago.";
    public const string LastSuccessMinutes = "Last translation {minutes} min ago.";
    public const string LastSuccessHours = "Last translation {hours} h ago.";

    /// <summary>"Working · 340 ms", "Skipping for 18 s: it is limiting requests.", "Your key was rejected. Check it above."</summary>
    public static string Describe(ProviderHealthSnapshot s, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        var waiting = s.RetryAt is { } at && at > now ? at - now : (TimeSpan?)null;
        return s.State switch
        {
            ProviderState.Healthy => s.MedianLatency is { } ok ? Format(WorkingWithLatency, ("latency", Ms(ok))) : Working,
            ProviderState.Degraded => s.MedianLatency is { } slow ? Format(DegradedWithLatency, ("latency", Ms(slow))) : Degraded,
            ProviderState.Throttled => waiting is { } t ? Format(ThrottledFor, ("seconds", Seconds(t))) : Throttled,
            ProviderState.CoolingDown => waiting is { } c ? Format(CoolingDownFor, ("seconds", Seconds(c))) : CoolingDown,
            ProviderState.AuthFailed => AuthFailed,
            ProviderState.QuotaExceeded => waiting switch
            {
                null => Quota,
                { TotalHours: >= 2 } q => Format(QuotaHoursFor, ("hours", (int)Math.Ceiling(q.TotalHours))),
                { } q => Format(QuotaFor, ("minutes", (int)Math.Ceiling(q.TotalMinutes))),
            },
            _ => s.LastErrorKind == TranslationErrorKind.Configuration ? NotSetUp : NotUsedYet,
        };
    }

    /// <summary>The War Room status line: which provider is serving chat now (REQ-TRR-18).</summary>
    /// <param name="chain">Snapshots in chain order.</param>
    /// <param name="names">Display names, same order as <paramref name="chain"/>.</param>
    public static string Summary(IReadOnlyList<ProviderHealthSnapshot> chain, IReadOnlyList<string> names, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(names);
        if (chain.Count == 0) return NoneAvailable;
        for (var i = 0; i < chain.Count; i++)
        {
            if (!IsUsable(chain[i], now)) continue;
            return i == 0
                ? Format(Using, ("provider", NameAt(names, chain, 0)))
                : Format(UsingFallback, ("primary", NameAt(names, chain, 0)), ("active", NameAt(names, chain, i)));
        }
        return NoneAvailable;
    }

    /// <summary>True when the next chat line would be sent to this provider.</summary>
    public static bool IsUsable(ProviderHealthSnapshot s, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.State switch
        {
            ProviderState.AuthFailed => false,
            ProviderState.QuotaExceeded or ProviderState.CoolingDown or ProviderState.Throttled => s.RetryAt is not { } at || at <= now,
            ProviderState.Unknown => s.LastErrorKind != TranslationErrorKind.Configuration,
            _ => true,
        };
    }

    /// <summary>"Retry {provider} now" (the button's <c>AutomationProperties.Name</c>).</summary>
    public static string RetryButtonName(string providerName) => Format(RetryButton, ("provider", providerName));

    /// <summary>"Last translation 12 s ago." / "No translation yet."</summary>
    public static string LastSuccess(ProviderHealthSnapshot s, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.LastSuccess is not { } at) return LastSuccessNever;
        var ago = now - at;
        if (ago < TimeSpan.Zero) ago = TimeSpan.Zero;
        if (ago.TotalMinutes < 1) return Format(LastSuccessSeconds, ("seconds", (int)ago.TotalSeconds));
        if (ago.TotalHours < 1) return Format(LastSuccessMinutes, ("minutes", (int)ago.TotalMinutes));
        return Format(LastSuccessHours, ("hours", (int)ago.TotalHours));
    }

    /// <summary>
    /// The only sanitiser for health errors (REQ-TRR-17): delegates masking to 02's <see cref="LogRedactor"/> (mask
    /// <c>[redacted]</c>) and cuts the result to <see cref="MaxErrorLength"/> characters. Redaction runs first so a
    /// secret is never cut in half where the redactor can no longer recognise it.
    /// </summary>
    public static string Sanitize(string message, LogRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        var redacted = redactor.Redact(message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return redacted.Length <= MaxErrorLength ? redacted : redacted[..MaxErrorLength];
    }

    private static string NameAt(IReadOnlyList<string> names, IReadOnlyList<ProviderHealthSnapshot> chain, int i) =>
        i < names.Count ? names[i] : ProviderCatalog.NameOf(chain[i].ProviderId);

    private static int Ms(TimeSpan t) => (int)Math.Round(t.TotalMilliseconds);

    private static int Seconds(TimeSpan t) => Math.Max(1, (int)Math.Ceiling(t.TotalSeconds));

    private static string Format(string template, params (string Name, object? Value)[] values) =>
        TextTemplate.Format(template, CultureInfo.CurrentCulture, values);
}
