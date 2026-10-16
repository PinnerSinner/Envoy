using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Envoy.Core.Updates;

/// <summary>
/// A SemVer 2.0 version (REQ-DIST-19). Accepts "v1.2", "1.2.3" and "1.2.3-beta.1+sha"; build metadata is dropped, so two
/// versions that differ only in metadata are equal. Immutable and thread-safe.
/// </summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private readonly string[] _prerelease;

    public ReleaseVersion(int major, int minor, int patch, params string[] prerelease)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        ArgumentNullException.ThrowIfNull(prerelease);
        foreach (var id in prerelease)
        {
            if (!IsIdentifier(id)) throw new ArgumentException("Prerelease identifiers are non-empty [0-9A-Za-z-] strings.", nameof(prerelease));
        }
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = [.. prerelease];
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>The dot-separated prerelease identifiers; empty for a release.</summary>
    public IReadOnlyList<string> Prerelease => _prerelease;

    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>"1.2.3", without any prerelease suffix.</summary>
    public string CoreString => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>
    /// Parses "1.2", "1.2.3", "v1.2.3-beta.1" or "1.2.3-rc.1+0abc" (metadata dropped). Two or three numeric parts; a
    /// leading "v"/"V" is allowed. False for null, blank and anything else.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            var metadata = s[(plus + 1)..];
            if (metadata.Split('.').Any(m => !IsIdentifier(m))) return false;
            s = s[..plus];
        }
        string[] pre = [];
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..].Split('.');
            if (pre.Any(p => !IsIdentifier(p))) return false;
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }
        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    /// <exception cref="FormatException"><paramref name="text"/> is not a version.</exception>
    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException("Not a release version.");

    /// <summary>
    /// "1.2.0-beta.1+0abc…" → 1.2.0-beta.1. Falls back to the numeric <paramref name="assemblyVersion"/> (major, minor,
    /// build), then 0.0.0.
    /// </summary>
    public static ReleaseVersion FromInformational(string? informationalVersion, Version? assemblyVersion)
    {
        if (TryParse(informationalVersion, out var v)) return v;
        if (assemblyVersion is not null)
            return new ReleaseVersion(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(0, assemblyVersion.Build));
        return new ReleaseVersion(0, 0, 0);
    }

    /// <summary>SemVer §11 precedence; null sorts first.</summary>
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A release has higher precedence than any of its prereleases.
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            c = CompareIdentifiers(_prerelease[i], other._prerelease[i]);
            if (c != 0) return c;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var id in _prerelease) hash.Add(IsNumeric(id) ? id.TrimStart('0') : id, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>"1.2.3" or "1.2.3-beta.1": no "v", no build metadata.</summary>
    public override string ToString() => IsPrerelease ? CoreString + "-" + string.Join('.', _prerelease) : CoreString;

    public static bool operator ==(ReleaseVersion? a, ReleaseVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(ReleaseVersion? a, ReleaseVersion? b) => !(a == b);
    public static bool operator <(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) < 0;
    public static bool operator >(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) > 0;
    public static bool operator <=(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) <= 0;
    public static bool operator >=(ReleaseVersion? a, ReleaseVersion? b) => Compare(a, b) >= 0;

    private static int Compare(ReleaseVersion? a, ReleaseVersion? b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b);

    /// <summary>Numeric identifiers compare numerically and sort before alphanumeric ones; others compare ordinally.</summary>
    private static int CompareIdentifiers(string a, string b)
    {
        var an = IsNumeric(a);
        var bn = IsNumeric(b);
        if (an && bn)
        {
            var ta = a.TrimStart('0');
            var tb = b.TrimStart('0');
            return ta.Length != tb.Length ? ta.Length.CompareTo(tb.Length) : string.CompareOrdinal(ta, tb);
        }
        if (an) return -1;
        if (bn) return 1;
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool IsNumeric(string id) => id.All(char.IsAsciiDigit);

    private static bool IsIdentifier(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
