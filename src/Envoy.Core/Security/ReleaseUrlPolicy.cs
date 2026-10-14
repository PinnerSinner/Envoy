namespace Envoy.Core.Security;

/// <summary>
/// Which URLs from a GitHub release response Envoy will offer or open (REQ-QSC-31). A spoofed response must not be
/// able to point "Download" at a local file, a network share or another site.
/// </summary>
public static class ReleaseUrlPolicy
{
    private const string Prefix = "https://github.com/";

    /// <summary>
    /// True for <c>https://github.com/{owner}/{repo}/releases/…</c>: https, host exactly github.com, default port, no
    /// user-info. When <paramref name="repository"/> ("owner/repo") is given, the URL's owner and repo must equal it,
    /// ignoring case.
    /// </summary>
    public static bool IsTrusted(string? url, string? repository = null)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0)
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !string.Equals(segments[2], "releases", StringComparison.Ordinal)) return false;
        if (segments[0] is "." or ".." || segments[1] is "." or "..") return false;
        return repository is null
            || string.Equals($"{segments[0]}/{segments[1]}", repository.Trim('/'), StringComparison.OrdinalIgnoreCase);
    }
}
