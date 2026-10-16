using System.Globalization;
using System.Text;
using Envoy.Core.Chat;

namespace Envoy.Core.Ocr;

/// <summary>One frame of a recorded capture: the OCR passes, the settings changed from this frame on, and the expected rows.</summary>
/// <param name="Expect">Rows <c>kind | channel | sender | text</c> (<c>-</c> for null), in emission order.</param>
public sealed record CaptureFrameFixture(IReadOnlyList<OcrPass> Passes, IReadOnlyList<string> Expect)
{
    /// <summary><c>@set</c> keys given inside this frame; they apply from this frame on.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A text format for recorded captures (spec 04): the golden tests use it, and so does the Live view's "Copy as test
/// capture". Fixtures hold chat text and player names; contributed ones must be anonymised (docs/chat-formats.md).
/// </summary>
/// <remarks>
/// <code>
/// # comment
/// @me Hoplite                 options.MyPlayerName
/// @language en                options.MyLanguage (default en)
/// @muted Spammer, Toxic99
/// @players 天下第一, Kim       pre-seeded player names
/// @set hideSystem=false stitch=true clean=true byName=true profile=generic
/// @pattern *resigned*         repeatable notice pattern
/// @frame                      starts a capture
/// @pass zh-CN                 engine language for the rows below (default: @language)
///  10.0  28.0 | text          top bottom [left right] | text exactly as the engine returned it
/// @expect                     rows emitted by this frame; "(none)" for none
/// chat | All | 天下第一 | text
/// </code>
/// There are no inline comments: everything after a directive's name is its value.
/// </remarks>
public sealed record CaptureFixture(
    IReadOnlyDictionary<string, string> Directives,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<CaptureFrameFixture> Frames)
{
    /// <summary>The <c>@set</c> keys every built feature understands; any other key fails parsing.</summary>
    public static IReadOnlyList<string> SettingKeys { get; } = ["hideSystem", "stitch", "clean", "byName", "profile"];

    /// <summary>The directives that take a value (besides <c>@set</c>, <c>@pattern</c>, <c>@frame</c>, <c>@pass</c>, <c>@expect</c>).</summary>
    public static IReadOnlyList<string> DirectiveNames { get; } = ["me", "language", "muted", "players"];

    public static IReadOnlyList<string> Kinds { get; } = ["chat", "system", "fragment"];

    private const string None = "(none)";

    /// <summary><c>@set</c> keys given before the first frame.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    /// <summary>The <c>@language</c> directive, or <c>en</c>.</summary>
    public string Language => Directives.TryGetValue("language", out var l) && l.Length > 0 ? l : "en";

    /// <exception cref="FormatException">"line N: …" for the first problem.</exception>
    public static CaptureFixture Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var directives = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var patterns = new List<string>();
        var frames = new List<CaptureFrameFixture>();

        FrameBuilder? frame = null;
        var section = Section.Header;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var n = 1; n <= lines.Length; n++)
        {
            var line = lines[n - 1];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            if (trimmed.StartsWith('@'))
            {
                var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
                var name = space < 0 ? trimmed[1..] : trimmed[1..space];
                var value = space < 0 ? "" : trimmed[(space + 1)..].Trim();
                switch (name)
                {
                    case "frame":
                        if (frame is not null) frames.Add(frame.Build());
                        frame = new FrameBuilder();
                        section = Section.Lines;
                        break;
                    case "pass":
                        if (frame is null) throw Error(n, "@pass comes before the first @frame.");
                        if (value.Length == 0) throw Error(n, "@pass needs a language.");
                        frame.StartPass(value);
                        section = Section.Lines;
                        break;
                    case "expect":
                        if (frame is null) throw Error(n, "@expect comes before the first @frame.");
                        section = Section.Expect;
                        break;
                    case "set":
                        ParseSettings(value, frame?.Settings ?? settings, n);
                        break;
                    case "pattern":
                        if (frame is not null) throw Error(n, "@pattern must come before the first @frame.");
                        patterns.Add(value);
                        break;
                    default:
                        if (!DirectiveNames.Contains(name)) throw Error(n, $"unknown directive @{name}.");
                        if (frame is not null) throw Error(n, $"@{name} must come before the first @frame.");
                        directives[name] = value;
                        break;
                }
                continue;
            }

            if (frame is null || section == Section.Header) throw Error(n, "a row comes before the first @frame.");
            if (section == Section.Expect) frame.Expect.Add(ParseExpect(trimmed, n));
            else frame.AddLine(ParseLine(line, n), directives.GetValueOrDefault("language") is { Length: > 0 } l ? l : "en");
        }
        if (frame is not null) frames.Add(frame.Build());
        return new CaptureFixture(directives, patterns, frames) { Settings = settings };
    }

    /// <summary>One frame of merged lines with an empty <c>@expect</c>, for "Copy as test capture" (REQ-CHAT-42).</summary>
    public static string Write(IReadOnlyList<OcrLine> lines, string passLanguage)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var language = string.IsNullOrWhiteSpace(passLanguage) ? "en" : passLanguage.Trim();
        var fixture = new CaptureFixture(
            new Dictionary<string, string> { ["language"] = language },
            [],
            [new CaptureFrameFixture([new OcrPass(language, lines)], [])]);
        return "# Envoy test capture. It contains chat and player names: anonymise it before sharing. Fill in @expect."
            + "\n" + fixture.Write();
    }

    /// <summary>The whole fixture in the text format; <see cref="Parse"/> reads it back unchanged.</summary>
    public string Write()
    {
        var sb = new StringBuilder();
        foreach (var name in DirectiveNames)
        {
            if (Directives.TryGetValue(name, out var value)) sb.Append('@').Append(name).Append(' ').Append(value).Append('\n');
        }
        AppendSettings(sb, Settings);
        foreach (var pattern in Patterns) sb.Append("@pattern ").Append(pattern).Append('\n');
        foreach (var frame in Frames)
        {
            sb.Append("@frame\n");
            AppendSettings(sb, frame.Settings);
            foreach (var pass in frame.Passes)
            {
                sb.Append("@pass ").Append(pass.LanguageCode).Append('\n');
                foreach (var line in pass.Lines)
                {
                    sb.Append(Number(line.Top)).Append(' ').Append(Number(line.Bottom));
                    if (line.Right > line.Left) sb.Append(' ').Append(Number(line.Left)).Append(' ').Append(Number(line.Right));
                    sb.Append(" | ").Append(OneLine(line.Text)).Append('\n');
                }
            }
            sb.Append("@expect\n");
            if (frame.Expect.Count == 0) sb.Append(None).Append('\n');
            foreach (var row in frame.Expect) sb.Append(row).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The canonical expect row for a message: <c>kind | channel | sender | text</c>.</summary>
    public static string Row(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var kind = message.Kind.ToString().ToLowerInvariant();
        return $"{kind} | {message.Channel ?? "-"} | {message.Sender ?? "-"} | {message.Text}";
    }

    private static void AppendSettings(StringBuilder sb, IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count == 0) return;
        sb.Append("@set");
        foreach (var key in SettingKeys)
        {
            if (settings.TryGetValue(key, out var value)) sb.Append(' ').Append(key).Append('=').Append(value);
        }
        sb.Append('\n');
    }

    private static void ParseSettings(string value, Dictionary<string, string> into, int n)
    {
        foreach (var pair in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) throw Error(n, $"@set needs key=value, got \"{pair}\".");
            var key = pair[..eq];
            if (!SettingKeys.Contains(key)) throw Error(n, $"unknown @set key \"{key}\".");
            into[key] = pair[(eq + 1)..];
        }
    }

    private static OcrLine ParseLine(string line, int n)
    {
        var bar = line.IndexOf('|', StringComparison.Ordinal);
        if (bar < 0) throw Error(n, "a line row needs \"top bottom | text\".");
        var numbers = line[..bar].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (numbers.Length is not (2 or 4)) throw Error(n, "a line row needs 2 or 4 numbers before '|'.");
        var values = new double[numbers.Length];
        for (var i = 0; i < numbers.Length; i++)
        {
            if (!double.TryParse(numbers[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                throw Error(n, $"\"{numbers[i]}\" is not a number.");
        }
        var text = line[(bar + 1)..];
        if (text.StartsWith(' ')) text = text[1..];
        return numbers.Length == 4 ? new OcrLine(text, values[0], values[1], values[2], values[3]) : new OcrLine(text, values[0], values[1]);
    }

    private static string ParseExpect(string row, int n)
    {
        if (row == None) return None;
        var parts = row.Split('|', 4);
        if (parts.Length < 4) throw Error(n, "an expect row needs \"kind | channel | sender | text\".");
        var kind = parts[0].Trim();
        if (!Kinds.Contains(kind)) throw Error(n, $"unknown kind \"{kind}\" (use chat, system or fragment).");
        return $"{kind} | {parts[1].Trim()} | {parts[2].Trim()} | {parts[3].Trim()}";
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string OneLine(string text) => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static FormatException Error(int line, string message) =>
        new(string.Create(CultureInfo.InvariantCulture, $"line {line}: {message}"));

    private enum Section { Header, Lines, Expect }

    private sealed class FrameBuilder
    {
        private readonly List<(string Language, List<OcrLine> Lines)> _passes = [];

        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
        public List<string> Expect { get; } = [];

        public void StartPass(string language) => _passes.Add((language, []));

        public void AddLine(OcrLine line, string defaultLanguage)
        {
            if (_passes.Count == 0) StartPass(defaultLanguage);
            _passes[^1].Lines.Add(line);
        }

        public CaptureFrameFixture Build() =>
            new(_passes.Select(p => new OcrPass(p.Language, p.Lines)).ToList(), Expect.Where(e => e != None).ToList()) { Settings = Settings };
    }
}
