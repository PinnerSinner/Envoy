using System.Net.Http.Headers;
using System.Text.Json;

namespace Envoy.Core.Updates;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? DownloadUrl, string? Notes);

/// <summary>Checks the project's GitHub Releases for a newer version. Never downloads or installs anything itself.</summary>
public sealed class UpdateChecker(HttpClient http, string ownerAndRepo)
{
    public string Repository { get; } = ownerAndRepo;

    /// <summary>Returns the latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Envoy", current.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null; // private repo, no releases yet, rate limited…
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var latest = Parse(json);
        return latest is not null && latest.Version > Normalize(current) ? latest : null;
    }

    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version)) return null;

        string? download = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    download = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        return new UpdateInfo(version, tag, root.GetProperty("html_url").GetString() ?? "", download,
            root.TryGetProperty("body", out var body) ? body.GetString() : null);
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
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

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
