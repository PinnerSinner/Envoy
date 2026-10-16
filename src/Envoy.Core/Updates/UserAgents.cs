namespace Envoy.Core.Updates;

/// <summary>The User-Agent strings Envoy sends (contract §3).</summary>
public static class UserAgents
{
    /// <summary>
    /// The shared client's default agent. It stays browser-like because the keyless Google and Edge providers depend on
    /// it; only the Envoy token carries the version (major.minor.patch, no prerelease).
    /// </summary>
    public static string Default(ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Envoy/" + version.CoreString;
    }
}
