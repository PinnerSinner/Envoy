using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Envoy.Core.Diagnostics;

namespace Envoy.Core.Chat;

public enum ChatFormatErrorKind
{
    /// <summary>The user file could not be read or is not valid JSON; the built-in formats stay in use.</summary>
    FileUnreadable,

    /// <summary>One field of one profile is invalid; the inherited value is used instead.</summary>
    FieldInvalid,
}

/// <summary>A problem in <c>chat-formats.json</c>. Holds names only, never file content or exception text.</summary>
public sealed record ChatFormatError(string? Profile, string? Field, ChatFormatErrorKind Kind);

/// <summary>
/// The chat format profiles: the embedded <c>chat-formats.json</c> (only <c>generic</c>), plus an optional user file in
/// the data folder that adds or replaces profiles by id and maps game ids to profiles (REQ-CHAT-30 to 33). Envoy only
/// reads the user file. Immutable once built.
/// </summary>
public sealed class ChatFormatCatalog
{
    public const string Auto = "auto";

    /// <summary>The largest user file Envoy reads; anything bigger counts as unreadable.</summary>
    public const long MaxUserFileBytes = 262_144;

    private const string ResourceName = "Envoy.Core.Chat.chat-formats.json";

    private static readonly Lazy<ChatFormatCatalog> LazyBuiltIn = new(() => Parse(ReadBuiltInJson(), null));

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    private readonly Dictionary<string, ChatFormatProfile> _byId;

    private ChatFormatCatalog(List<ChatFormatProfile> profiles, Dictionary<string, string> games, List<ChatFormatError> errors)
    {
        Profiles = profiles;
        _byId = profiles.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Generic = _byId[ChatFormatProfile.GenericId];
        GameProfiles = games;
        Errors = errors;
    }

    /// <summary>The embedded profiles alone.</summary>
    public static ChatFormatCatalog BuiltIn => LazyBuiltIn.Value;

    /// <summary>Every profile, <c>generic</c> first.</summary>
    public IReadOnlyList<ChatFormatProfile> Profiles { get; }

    public ChatFormatProfile Generic { get; }

    /// <summary>Game id → profile id, used by <see cref="Auto"/>. Empty in the built-in file.</summary>
    public IReadOnlyDictionary<string, string> GameProfiles { get; }

    public IReadOnlyList<ChatFormatError> Errors { get; }

    /// <summary>
    /// The built-in profiles plus the user file at <paramref name="path"/> when it exists. Never throws and never writes;
    /// problems are in <see cref="Errors"/> and logged at Warning (names only).
    /// </summary>
    public static ChatFormatCatalog LoadWithUserFile(string path, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string? user = null;
        var unreadable = false;
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > MaxUserFileBytes) unreadable = true;
                else user = File.ReadAllText(path, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            unreadable = true;
        }

        var catalog = unreadable ? WithFileError(BuiltIn) : Parse(ReadBuiltInJson(), user);
        foreach (var error in catalog.Errors)
        {
            log.Warn("Chat formats problem", null, LogField.Of("kind", error.Kind), LogField.Of("profile", error.Profile),
                LogField.Of("field", error.Field));
        }
        return catalog;
    }

    /// <summary>Builds a catalog from the built-in JSON and an optional user JSON (for tests). Never throws for user input.</summary>
    public static ChatFormatCatalog Parse(string builtInJson, string? userJson)
    {
        ArgumentNullException.ThrowIfNull(builtInJson);
        var errors = new List<ChatFormatError>();
        var profiles = new List<ChatFormatProfile>();
        var games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The built-in file is trusted: a problem in it is a build bug, so it throws.
        using (var builtIn = JsonDocument.Parse(builtInJson, JsonOptions))
        {
            var builtInErrors = new List<ChatFormatError>();
            Merge(builtIn.RootElement, profiles, games, builtInErrors);
            if (builtInErrors.Count > 0 || !profiles.Any(p => p.Id == ChatFormatProfile.GenericId))
                throw new InvalidOperationException("The built-in chat-formats.json is invalid.");
        }

        if (userJson is not null)
        {
            try
            {
                using var user = JsonDocument.Parse(userJson, JsonOptions);
                if (user.RootElement.ValueKind != JsonValueKind.Object) errors.Add(new ChatFormatError(null, null, ChatFormatErrorKind.FileUnreadable));
                else Merge(user.RootElement, profiles, games, errors);
            }
            catch (JsonException)
            {
                errors.Add(new ChatFormatError(null, null, ChatFormatErrorKind.FileUnreadable));
            }
        }

        var generic = profiles.First(p => p.Id.Equals(ChatFormatProfile.GenericId, StringComparison.OrdinalIgnoreCase));
        var ordered = new List<ChatFormatProfile> { generic };
        ordered.AddRange(profiles.Where(p => !ReferenceEquals(p, generic)));
        return new ChatFormatCatalog(ordered, games, errors);
    }

    /// <summary>
    /// The profile the setting names. <see cref="Auto"/> (or empty) uses the game mapping; an unknown id falls back to
    /// <c>generic</c> (the setting is kept, so it works again once the file is back).
    /// </summary>
    public ChatFormatProfile Resolve(string? setting, string? gameId)
    {
        var id = string.IsNullOrWhiteSpace(setting) || setting.Equals(Auto, StringComparison.OrdinalIgnoreCase)
            ? gameId is not null && GameProfiles.TryGetValue(gameId, out var mapped) ? mapped : null
            : setting;
        return id is not null && _byId.TryGetValue(id, out var profile) ? profile : Generic;
    }

    public ChatFormatProfile? Find(string id) => _byId.GetValueOrDefault(id);

    private static ChatFormatCatalog WithFileError(ChatFormatCatalog builtIn) =>
        new([.. builtIn.Profiles], new Dictionary<string, string>(builtIn.GameProfiles, StringComparer.OrdinalIgnoreCase),
            [new ChatFormatError(null, null, ChatFormatErrorKind.FileUnreadable)]);

    internal static string ReadBuiltInJson()
    {
        using var stream = typeof(ChatFormatCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Built-in chat formats resource missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Merge(JsonElement root, List<ChatFormatProfile> profiles, Dictionary<string, string> games, List<ChatFormatError> errors)
    {
        if (Property(root, "profiles") is { } list)
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new ChatFormatError(null, "profiles", ChatFormatErrorKind.FieldInvalid));
            }
            else
            {
                foreach (var element in list.EnumerateArray())
                {
                    if (BuildProfile(element, profiles, errors) is not { } profile) continue;
                    var existing = profiles.FindIndex(p => p.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0) profiles[existing] = profile;
                    else profiles.Add(profile);
                }
            }
        }

        if (Property(root, "games") is { } map)
        {
            if (map.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ChatFormatError(null, "games", ChatFormatErrorKind.FieldInvalid));
                return;
            }
            foreach (var entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } target)
                    games[entry.Name] = target.Trim();
                else
                    errors.Add(new ChatFormatError(null, "games", ChatFormatErrorKind.FieldInvalid));
            }
        }
    }

    private static ChatFormatProfile? BuildProfile(JsonElement element, List<ChatFormatProfile> known, List<ChatFormatError> errors)
    {
        if (element.ValueKind != JsonValueKind.Object || Property(element, "id") is not { ValueKind: JsonValueKind.String } idElement
            || idElement.GetString()?.Trim() is not { Length: > 0 and <= 64 } id)
        {
            errors.Add(new ChatFormatError(null, "id", ChatFormatErrorKind.FieldInvalid));
            return null;
        }

        // Missing fields come from the inherited profile, or from generic.
        var parent = known.FirstOrDefault(p => p.Id.Equals(ChatFormatProfile.GenericId, StringComparison.OrdinalIgnoreCase));
        if (Property(element, "inherits") is { } inheritsElement)
        {
            var inherits = inheritsElement.ValueKind == JsonValueKind.String ? inheritsElement.GetString() : null;
            var found = inherits is null ? null : known.FirstOrDefault(p => p.Id.Equals(inherits, StringComparison.OrdinalIgnoreCase));
            if (found is null) errors.Add(new ChatFormatError(id, "inherits", ChatFormatErrorKind.FieldInvalid));
            else parent = found;
        }

        var field = new FieldReader(id, errors);
        var name = field.String(element, "name", parent?.Name ?? id, s => s.Length is > 0 and <= 80);
        var channels = field.Channels(element, parent?.Channels);
        var brackets = field.StringList(element, "brackets", parent?.Brackets, b => b.Length == 2);
        var separators = field.StringList(element, "separators", parent?.Separators, s => s.Length is > 0 and <= 8);
        var timestamp = field.String(element, "timestamp", parent?.TimestampPattern, IsValidRegex);
        var maxSender = field.Int(element, "maxSenderLength", parent?.MaxSenderLength, 1, 64);
        var (stitchEnabled, stitching) = field.Stitching(element, parent);
        var system = field.SystemPatterns(element, parent?.SystemPatterns);

        if (channels is null || brackets is null || separators is null || timestamp is null || maxSender is null)
        {
            // Only the built-in generic profile can be missing a parent; it must be complete.
            errors.Add(new ChatFormatError(id, null, ChatFormatErrorKind.FieldInvalid));
            return null;
        }
        return new ChatFormatProfile(id, name ?? id, channels, brackets, separators, timestamp, maxSender.Value, stitchEnabled, stitching, system);
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, ChatFormatProfile.MatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Case-insensitive property lookup; unknown properties are simply never asked for.</summary>
    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in element.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    /// <summary>Reads one profile's fields; an invalid field records an error and yields the inherited value.</summary>
    private sealed class FieldReader(string profile, List<ChatFormatError> errors)
    {
        private void Report(string field) => errors.Add(new ChatFormatError(profile, field, ChatFormatErrorKind.FieldInvalid));

        private T Invalid<T>(string field, T fallback)
        {
            Report(field);
            return fallback;
        }

        public string? String(JsonElement element, string field, string? fallback, Func<string, bool> valid)
        {
            if (Property(element, field) is not { } value) return fallback;
            return value.ValueKind == JsonValueKind.String && value.GetString() is { } s && valid(s) ? s : Invalid(field, fallback);
        }

        public int? Int(JsonElement element, string field, int? fallback, int min, int max)
        {
            if (Property(element, field) is not { } value) return fallback;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max ? n : Invalid(field, fallback);
        }

        public IReadOnlyList<string>? StringList(JsonElement element, string field, IReadOnlyList<string>? fallback, Func<string, bool> valid)
        {
            if (Property(element, field) is not { } value) return fallback;
            if (value.ValueKind != JsonValueKind.Array) return Invalid(field, fallback);
            var list = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } s || !valid(s)) return Invalid(field, fallback);
                list.Add(s);
            }
            return list.Count == 0 ? Invalid(field, fallback) : list;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Channels(JsonElement element, IReadOnlyDictionary<string, IReadOnlyList<string>>? fallback)
        {
            if (Property(element, "channels") is not { } value) return fallback;
            if (value.ValueKind != JsonValueKind.Object) return Invalid("channels", fallback);
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var entry in value.EnumerateObject())
            {
                var canonical = ChatFormatProfile.CanonicalChannels.FirstOrDefault(c => c.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (canonical is null || entry.Value.ValueKind != JsonValueKind.Array) return Invalid("channels", fallback);
                var words = new List<string>();
                foreach (var item in entry.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString()?.Trim() is not { Length: > 0 and <= 16 } word)
                        return Invalid("channels", fallback);
                    words.Add(word);
                }
                result[canonical] = words;
            }
            return result;
        }

        public (bool Enabled, StitchOptions Options) Stitching(JsonElement element, ChatFormatProfile? parent)
        {
            var enabled = parent?.StitchEnabled ?? true;
            var options = parent?.Stitching ?? new StitchOptions();
            if (Property(element, "stitching") is not { } value) return (enabled, options);
            if (value.ValueKind != JsonValueKind.Object) return Invalid("stitching", (enabled, options));

            if (Property(value, "enabled") is { } e)
            {
                if (e.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = e.GetBoolean();
                else Report("stitching.enabled");
            }
            var gap = Double(value, "maxGapFactor", options.MaxGapFactor, 0.2, 5);
            var height = Double(value, "heightTolerance", options.HeightTolerance, 0, 5);
            var lines = Int(value, "maxContinuationLines", options.MaxContinuationLines, 0, 10) ?? options.MaxContinuationLines;
            return (enabled, new StitchOptions(gap, height, lines));
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> SystemPatterns(JsonElement element, IReadOnlyDictionary<string, IReadOnlyList<string>>? fallback)
        {
            var empty = (IReadOnlyDictionary<string, IReadOnlyList<string>>)new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            fallback ??= empty;
            if (Property(element, "systemPatterns") is not { } value) return fallback;
            if (value.ValueKind != JsonValueKind.Object) return Invalid("systemPatterns", fallback);
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in value.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Array) return Invalid("systemPatterns", fallback);
                var patterns = entry.Value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString() ?? "").ToList();
                SystemLinePatterns.Compile(patterns, out var patternErrors);
                if (patternErrors.Count > 0)
                {
                    // Keep the patterns that work; report the field once.
                    Report("systemPatterns");
                    var bad = patternErrors.Select(p => p.Index).ToHashSet();
                    patterns = patterns.Where((_, i) => !bad.Contains(i)).ToList();
                }
                result[entry.Name] = patterns;
            }
            return result;
        }

        private double Double(JsonElement element, string field, double fallback, double min, double max)
        {
            if (Property(element, field) is not { } value) return fallback;
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && d >= min && d <= max ? d : Invalid(field, fallback);
        }
    }
}
