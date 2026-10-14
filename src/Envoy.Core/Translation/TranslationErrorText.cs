using System.Globalization;
using Envoy.Core.Text;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Translation;

/// <summary>
/// Every user-facing translation error (contract §8.7, REQ-TRR-1): whole sentences with named placeholders, no "Error:"
/// prefix, never a response body or raw exception text. <c>{provider}</c> is always the catalog name.
/// </summary>
public static class TranslationErrorText
{
    public const string Transient = "{provider} is having trouble right now.";
    public const string NetworkFailed = "Envoy couldn't reach {provider}. Check your internet connection.";
    public const string AttemptTimeout = "{provider} didn't answer in time.";
    public const string Timeout = "No translation within {seconds} s.";
    public const string RateLimited = "{provider} is limiting requests right now.";
    public const string AuthRejected = "{provider} rejected your key. Check it on the Translation page.";
    public const string QuotaExceeded = "{provider} says your quota is used up.";
    public const string BudgetExceeded = "Daily character limit of {limit:N0} reached for {provider}. Raise it in Settings → Translation, or switch provider.";
    public const string BadResponse = "{provider} sent a reply Envoy could not read.";
    public const string Unsupported = "{provider} couldn't translate this message.";
    public const string Refused = "{provider} declined to translate this message.";
    public const string Unknown = "{provider} failed unexpectedly.";
    public const string MissingKey = "{provider} needs an API key. Add it on the Translation page.";
    public const string MissingUrl = "{provider} needs a server URL. Add it on the Translation page.";
    public const string MissingAwsKeys = "{provider} needs an access key and secret. Add them on the Translation page.";
    public const string NoToken = "{provider} didn't issue a sign-in token.";

    public const string SkippedCoolingDown = "{provider} is cooling down after repeated failures.";
    public const string SkippedThrottled = "{provider} is limiting requests right now.";
    public const string SkippedAuth = "{provider} rejected your key. Check it on the Translation page.";
    public const string SkippedQuota = "{provider} has no quota left for now.";
    public const string SkippedBusy = "{provider} is busy with other messages.";

    /// <summary>The reply box's own request pacing (compose lane), not a limit set by the provider.</summary>
    public const string SkippedComposePaced = "Envoy is pacing reply-box requests to {provider}. Try again in a moment.";

    public const string NoProviders = "No translation provider is configured.";

    /// <summary>{reasons} = the whole-sentence message of each provider that failed, in chain order.</summary>
    public const string AllFailed = "Translation unavailable. {reasons}";

    public const string CacheClearFailed = "Envoy couldn't delete the saved translations. Close any program using the file and try again.";

    /// <summary>Fills <paramref name="template"/> with <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public static string Format(string template, params (string Name, object? Value)[] args) =>
        TextTemplate.Format(template, CultureInfo.CurrentCulture, args);

    /// <summary>The sentence for <paramref name="kind"/> about the provider <paramref name="providerId"/>.</summary>
    public static string ForKind(TranslationErrorKind kind, string providerId)
    {
        var template = kind switch
        {
            TranslationErrorKind.Transient => Transient,
            TranslationErrorKind.Timeout => AttemptTimeout,
            TranslationErrorKind.RateLimited => RateLimited,
            TranslationErrorKind.Auth => AuthRejected,
            TranslationErrorKind.QuotaExceeded => QuotaExceeded,
            TranslationErrorKind.BadResponse => BadResponse,
            TranslationErrorKind.Unsupported => Unsupported,
            TranslationErrorKind.Configuration => MissingKey,
            TranslationErrorKind.Unavailable => SkippedBusy,
            _ => Unknown,
        };
        return Format(template, ("provider", ProviderCatalog.NameOf(providerId)));
    }

    /// <summary>The sentence for a provider skipped locally while in <paramref name="state"/>.</summary>
    public static string ForSkip(ProviderState state, string providerId)
    {
        var template = state switch
        {
            ProviderState.CoolingDown => SkippedCoolingDown,
            ProviderState.Throttled => SkippedThrottled,
            ProviderState.AuthFailed => SkippedAuth,
            ProviderState.QuotaExceeded => SkippedQuota,
            _ => SkippedBusy,
        };
        return Format(template, ("provider", ProviderCatalog.NameOf(providerId)));
    }

    /// <summary>The overall deadline message.</summary>
    public static string ForTimeout(TimeSpan deadline) =>
        Format(Timeout, ("seconds", (int)Math.Round(deadline.TotalSeconds)));

    /// <summary>A classified exception whose message is <see cref="ForKind"/>.</summary>
    public static TranslationException Error(TranslationErrorKind kind, string providerId, Exception? inner = null,
                                             int? statusCode = null, TimeSpan? retryAfter = null) =>
        new(ForKind(kind, providerId), inner, kind, statusCode, retryAfter);

    /// <summary>A <see cref="TranslationErrorKind.Configuration"/> error built from <paramref name="template"/>.</summary>
    public static TranslationException NotConfigured(string template, string providerId) =>
        new(Format(template, ("provider", ProviderCatalog.NameOf(providerId))), kind: TranslationErrorKind.Configuration);
}
