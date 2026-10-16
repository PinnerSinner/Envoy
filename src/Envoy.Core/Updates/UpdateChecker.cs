using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Envoy.Core.Security;

namespace Envoy.Core.Updates;

/// <param name="DownloadUrl">The portable exe: the asset named exactly "Envoy.exe", else the first ".exe" that isn't a Setup.exe.</param>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? DownloadUrl, string? Notes)
{
    /// <summary>The tag as a SemVer version (with any prerelease suffix).</summary>
    public ReleaseVersion? SemVer { get; init; }

    /// <summary>The asset named exactly <see cref="InstallerAssetName"/>; null if missing or untrusted.</summary>
    public string? InstallerUrl { get; init; }

    public bool IsPrerelease { get; init; }

    public const string InstallerAssetName = "Envoy-win-Setup.exe";
    public const string PortableAssetName = "Envoy.exe";
}

/// <summary>
/// Checks the project's GitHub Releases for a newer version (the portable path). Never downloads or installs anything
/// itself. Requests carry only <c>User-Agent: Envoy/&lt;version&gt;</c> and the GitHub <c>Accept</c> header.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string ownerAndRepo)
{
    private const string Api = "https://api.github.com/repos/";

    public string Repository { get; } = ownerAndRepo;

    /// <summary>Returns the latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        using var request = Request($"{Api}{Repository}/releases/latest", current.ToString(3));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null; // private repo, no releases yet, rate limited…
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var latest = Trusted(Parse(json));
        return latest is not null && latest.Version > Normalize(current) ? latest : null;
    }

    /// <summary>
    /// Stable: <c>GET releases/latest</c>. Beta: <c>GET releases?per_page=10</c> and the highest SemVer. Returns the
    /// release when it is newer than <paramref name="current"/> (SemVer precedence), else null. 404 → null.
    /// 403/429 → <see cref="UpdateErrorKind.RateLimited"/>; other non-success or unreadable JSON →
    /// <see cref="UpdateErrorKind.BadResponse"/>; network errors and timeouts → <see cref="UpdateErrorKind.Offline"/>.
    /// A release whose page is not a trusted release URL of this repository is ignored (08a rule).
    /// </summary>
    /// <exception cref="UpdateCheckException">The check failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<UpdateInfo?> CheckAsync(ReleaseVersion current, UpdateChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var url = channel == UpdateChannel.Beta ? $"{Api}{Repository}/releases?per_page=10" : $"{Api}{Repository}/releases/latest";
        string json;
        try
        {
            using var request = Request(url, current.ToString());
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateCheckException(UpdateErrorKind.RateLimited);
            if (!response.IsSuccessStatusCode) throw new UpdateCheckException(UpdateErrorKind.BadResponse);
            json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateCheckException(UpdateErrorKind.Offline, ex); // HttpClient.Timeout
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException(UpdateErrorKind.Offline, ex);
        }

        UpdateInfo? found;
        try
        {
            found = channel == UpdateChannel.Beta ? PickLatest(json, channel) : Parse(json, includePrereleases: false);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UpdateCheckException(UpdateErrorKind.BadResponse, ex);
        }
        var latest = Trusted(found);
        return latest?.SemVer is { } v && v > current ? latest : null;
    }

    /// <summary>
    /// Parses a GitHub "latest release" response. Returns null for drafts, prereleases, unparseable tags and releases
    /// whose page URL is not a trusted GitHub release URL (<see cref="ReleaseUrlPolicy"/>); an untrusted download URL
    /// becomes null.
    /// </summary>
    public static UpdateInfo? Parse(string json) => Parse(json, includePrereleases: false);

    /// <summary>As <see cref="Parse(string)"/>, keeping prereleases when <paramref name="includePrereleases"/>.</summary>
    public static UpdateInfo? Parse(string json, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement, includePrereleases);
    }

    /// <summary>
    /// The highest-SemVer release in a GitHub "list releases" array (not the newest published). Drafts are skipped;
    /// prereleases count only on <see cref="UpdateChannel.Beta"/>; untrusted releases are skipped.
    /// </summary>
    public static UpdateInfo? PickLatest(string releasesArrayJson, UpdateChannel channel)
    {
        using var doc = JsonDocument.Parse(releasesArrayJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a JSON array of releases.");
        UpdateInfo? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            var info = FromElement(release, channel == UpdateChannel.Beta);
            if (info?.SemVer is { } v && (best?.SemVer is null || v > best.SemVer)) best = info;
        }
        return best;
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var text = tag.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        if (Version.TryParse(text, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static HttpRequestMessage Request(string url, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Envoy", version));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    /// <summary>A release must belong to the configured repository; so must its downloads.</summary>
    private UpdateInfo? Trusted(UpdateInfo? info)
    {
        if (info is null || !ReleaseUrlPolicy.IsTrusted(info.PageUrl, Repository)) return null;
        if (!ReleaseUrlPolicy.IsTrusted(info.DownloadUrl, Repository)) info = info with { DownloadUrl = null };
        if (!ReleaseUrlPolicy.IsTrusted(info.InstallerUrl, Repository)) info = info with { InstallerUrl = null };
        return info;
    }

    private static UpdateInfo? FromElement(JsonElement root, bool includePrereleases)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        var prerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
        if (prerelease && !includePrereleases) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;
        _ = ReleaseVersion.TryParse(tag, out var semver);
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        if (!ReleaseUrlPolicy.IsTrusted(page)) return null;

        string? portable = null;
        string? fallback = null;
        string? installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var trusted = ReleaseUrlPolicy.IsTrusted(url) ? url : null;
                if (string.Equals(name, UpdateInfo.InstallerAssetName, StringComparison.OrdinalIgnoreCase)) installer ??= trusted;
                else if (string.Equals(name, UpdateInfo.PortableAssetName, StringComparison.OrdinalIgnoreCase)) portable ??= trusted;
                else if (fallback is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                         && !name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                    fallback = trusted ?? "";
            }
        }
        var download = portable ?? (string.IsNullOrEmpty(fallback) ? null : fallback);

        var body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        return new UpdateInfo(version, tag, page!, download, body)
        {
            SemVer = semver,
            InstallerUrl = installer,
            IsPrerelease = prerelease,
        };
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
