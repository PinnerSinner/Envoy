using Envoy.Core.Languages;
using Envoy.Core.Translation.Resilience;
using Envoy.Core.Updates;

namespace Envoy.Core.Notifications;

/// <summary>What a provider-health state means for the provider notice (REQ-OUXA-17, contract §2.2).</summary>
public enum ProviderNoticeRule
{
    /// <summary>Leave the notice as it is.</summary>
    None,

    /// <summary>Submit (or replace) the provider notice.</summary>
    Submit,

    /// <summary>Resolve the provider notice, which re-arms it.</summary>
    Resolve,
}

/// <summary>Maps a <see cref="ProviderState"/> edge to the provider notice. Pure.</summary>
public static class ProviderNoticeRules
{
    /// <summary>AuthFailed, QuotaExceeded, CoolingDown → Submit; Healthy → Resolve; anything else → None.</summary>
    public static ProviderNoticeRule For(ProviderState state) => state switch
    {
        ProviderState.AuthFailed or ProviderState.QuotaExceeded or ProviderState.CoolingDown => ProviderNoticeRule.Submit,
        ProviderState.Healthy => ProviderNoticeRule.Resolve,
        _ => ProviderNoticeRule.None,
    };
}

/// <summary>Builds the three advisories (REQ-OUXA-16, 17). Text only from <see cref="NoticeText"/> and <see cref="UpdateView.Headline"/>.</summary>
public static class NoticeFactory
{
    public const string OcrKeyPrefix = "ocr-missing:";
    public const string ProviderKeyPrefix = "provider:";
    public const string UpdateKeyPrefix = "update:";

    /// <summary>null when nothing is missing. Key = prefix + codes sorted ordinal, comma-joined. Text uses language names.</summary>
    public static Notice? OcrMissing(IReadOnlyList<string> missingCodes)
    {
        ArgumentNullException.ThrowIfNull(missingCodes);
        var codes = missingCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (codes.Count == 0) return null;
        return new Notice(NoticeKind.OcrLanguageMissing, OcrKey(codes), NoticeText.OcrMissingTitle,
            NoticeText.OcrMissingFor(codes.Select(LanguageCatalog.NameOf)), NoticeActions.OpenCapture, IsWarning: true);
    }

    /// <summary>The OCR notice key for these codes (sorted ordinal, comma-joined).</summary>
    public static string OcrKey(IEnumerable<string> codes) =>
        OcrKeyPrefix + string.Join(",", codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    public static string ProviderKey(string providerId) => ProviderKeyPrefix + providerId;

    /// <summary>null unless <paramref name="state"/> is AuthFailed, QuotaExceeded or CoolingDown. Never includes the provider's last error.</summary>
    public static Notice? ProviderFailing(string providerId, string providerName, ProviderState state)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(providerName);
        var template = state switch
        {
            ProviderState.AuthFailed => NoticeText.ProviderAuthFailed,
            ProviderState.QuotaExceeded => NoticeText.ProviderQuota,
            ProviderState.CoolingDown => NoticeText.ProviderCoolingDown,
            _ => null,
        };
        return template is null
            ? null
            : new Notice(NoticeKind.ProviderFailing, ProviderKey(providerId), NoticeText.ProviderTitle,
                NoticeText.ProviderFor(template, providerName), NoticeActions.OpenTranslation, IsWarning: true);
    }

    /// <summary>null unless the state is Available or ReadyToRestart with a version. Key = prefix + version; Text = the banner headline.</summary>
    public static Notice? UpdateAvailable(UpdateSnapshot snapshot, UpdateView view)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(view);
        if (snapshot.State is not (UpdateState.Available or UpdateState.ReadyToRestart) || snapshot.Available is not { } available) return null;
        if (string.IsNullOrWhiteSpace(view.Headline)) return null;
        return new Notice(NoticeKind.UpdateAvailable, UpdateKeyPrefix + available.Version, NoticeText.UpdateTitle, view.Headline,
            NoticeActions.UpdatePrimary, IsWarning: false);
    }
}
