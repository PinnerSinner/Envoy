using Envoy.Core.Settings;

namespace Envoy.Core.Translation;

/// <summary>
/// The immutable translation settings snapshot (contract §7), built on the UI thread in <c>AppHost.ApplySettings</c>
/// and read by background code through <see cref="TranslationRuntime.Options"/>.
/// </summary>
/// <param name="RequestTimeout">Overall deadline per translation call, shared across retries and fallbacks (REQ-TRR-6).</param>
/// <param name="RememberTranslations">Save the cache between sessions (REQ-TRR-20).</param>
/// <param name="CacheMaxEntries">Memory and disk cap of the cache.</param>
public sealed record TranslationRuntimeOptions(TimeSpan RequestTimeout, bool RememberTranslations, int CacheMaxEntries)
{
    public const int MinTimeoutSeconds = 3;
    public const int MaxTimeoutSeconds = 60;
    public const int MinCacheEntries = 100;
    public const int MaxCacheEntries = 20_000;

    public static TranslationRuntimeOptions Default { get; } = From(new TranslationSettings());

    /// <summary>Copies (and clamps: 3–60 s, 100–20000 entries) the values out of <paramref name="s"/>.</summary>
    public static TranslationRuntimeOptions From(TranslationSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new TranslationRuntimeOptions(
            TimeSpan.FromSeconds(Math.Clamp(s.RequestTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds)),
            s.RememberTranslations,
            Math.Clamp(s.CacheMaxEntries, MinCacheEntries, MaxCacheEntries));
    }
}
