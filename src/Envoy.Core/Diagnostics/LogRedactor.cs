using System.Text;
using System.Text.RegularExpressions;

namespace Envoy.Core.Diagnostics;

/// <summary>
/// The only redaction implementation in Envoy (REQ-REL-4, contract §2). Every log entry, crash marker and diagnostics
/// report passes through it. Order: (1) registered secret values, longest first; (2) known key patterns; (3) URL
/// user-info and query strings; (4) path aliases such as the user profile. Thread-safe: the secret and alias sets are
/// immutable arrays swapped with <see cref="Volatile"/>.
/// </summary>
public sealed partial class LogRedactor
{
    public const string Mask = "[redacted]";

    /// <summary>Shorter values are not registered: they would mask ordinary words.</summary>
    public const int MinimumSecretLength = 6;

    private readonly object _gate = new();
    private string[] _secrets = [];
    private (string Path, string Alias)[] _aliases = [];

    /// <summary>
    /// Replaces the registered secrets with a copy of <paramref name="values"/> (blank and short values ignored). Later
    /// edits to the settings never change the set without another call (contract §7 snapshot rule).
    /// </summary>
    public void SetSecrets(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length >= MinimumSecretLength)
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToArray();
        Volatile.Write(ref _secrets, copy);
    }

    /// <summary>The currently registered secrets (a copy), for tests.</summary>
    public IReadOnlyList<string> Secrets => [.. Volatile.Read(ref _secrets)];

    /// <summary>Replaces <paramref name="path"/> (case-insensitive) with <paramref name="alias"/> in every entry (REQ-REL-6).</summary>
    public void AddPathAlias(string path, string alias)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3) return;
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length < 3) return;
        lock (_gate)
        {
            var current = Volatile.Read(ref _aliases);
            if (current.Any(a => string.Equals(a.Path, trimmed, StringComparison.OrdinalIgnoreCase))) return;
            (string Path, string Alias)[] next = [.. current, (trimmed, alias)];
            Volatile.Write(ref _aliases, [.. next.OrderByDescending(a => a.Path.Length)]);
        }
    }

    /// <summary>The redacted text; "" for null.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = text;

        foreach (var secret in Volatile.Read(ref _secrets))
            result = result.Replace(secret, Mask, StringComparison.Ordinal);

        result = AnthropicKey().Replace(result, Mask);
        result = AwsAccessKey().Replace(result, Mask);
        result = DeepLKey().Replace(result, Mask);
        result = HeaderAssignment().Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + Mask);
        result = Url().Replace(result, m => RedactUrl(m.Value));

        foreach (var (path, alias) in Volatile.Read(ref _aliases))
            result = result.Replace(path, alias, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    /// <summary><c>https://user:pass@host/x?q=1#f</c> → <c>https://[redacted]@host/x?[redacted]</c>.</summary>
    private static string RedactUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return url;
        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0) authorityEnd = url.Length;

        var sb = new StringBuilder(url.Length);
        sb.Append(url, 0, authorityStart);
        var authority = url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        sb.Append(at >= 0 ? Mask + authority[at..] : authority);

        var rest = url[authorityEnd..];
        var query = rest.IndexOf('?', StringComparison.Ordinal);
        if (query < 0) query = rest.IndexOf('#', StringComparison.Ordinal);
        sb.Append(query < 0 ? rest : rest[..query] + "?" + Mask);
        return sb.ToString();
    }

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}:fx")]
    private static partial Regex DeepLKey();

    [GeneratedRegex(@"(?i)(?<name>\b(?:x-api-key|authorization|auth_key))(?<sep>\s*[:=]\s*)(?:(?:Bearer|Basic|DeepL-Auth-Key)\s+)?[^\s,;&""']+")]
    private static partial Regex HeaderAssignment();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
