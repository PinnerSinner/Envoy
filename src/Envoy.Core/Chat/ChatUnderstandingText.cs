using System.Globalization;
using Envoy.Core.Text;

namespace Envoy.Core.Chat;

/// <summary>
/// Text for the Reading Chat page's "Understanding chat" card and the Live view's "Understood as" (contract §8.7).
/// Whole sentences with named placeholders, filled only by <see cref="TextTemplate.Format"/>. Raw regex and JSON errors
/// are never shown; they go to the log as sensitive fields.
/// </summary>
public static class ChatUnderstandingText
{
    public const string UnderstoodHeader = "Understood as:";

    /// <summary>The first entry of the "Chat format" list.</summary>
    public const string MatchGameFormat = "Match the game";

    public const string Nothing = "(nothing understood yet)";

    /// <summary>{channel}, {sender}, {text}.</summary>
    public const string ChatEntry = "[{channel}] {sender}: {text}";

    /// <summary>{sender}, {text}: a chat entry without a channel.</summary>
    public const string ChatEntryNoChannel = "{sender}: {text}";

    /// <summary>{text}: a line read one per message, without a sender (analysis fallback).</summary>
    public const string ChatEntryNoSender = "(no sender) {text}";

    /// <summary>{text}.</summary>
    public const string NoticeEntry = "(notice) {text}";

    /// <summary>{text}.</summary>
    public const string FragmentEntry = "(part of an earlier message) {text}";

    /// <summary>{entry} = one of the entry lines above; {count} = how many screen lines it joins (shown when 2 or more).</summary>
    public const string LineCount = "{entry} ({count} lines)";

    /// <summary>{line} = the 1-based line in the notice box.</summary>
    public const string PatternInvalid = "Line {line} isn't a pattern Envoy understands, so it's ignored. Check its brackets and symbols, or remove \"re:\".";

    public const string FormatsFileUnreadable = "Your chat-formats.json couldn't be read, so Envoy is using the built-in formats. Check the file for typos.";

    /// <summary>{field}, {profile}.</summary>
    public const string FormatsFieldInvalid = "In chat-formats.json, the \"{field}\" setting of profile \"{profile}\" isn't valid, so Envoy uses the inherited value.";

    /// <summary>{field}: a top-level setting such as "games".</summary>
    public const string FormatsSettingInvalid = "In chat-formats.json, the \"{field}\" setting isn't valid, so Envoy ignores it.";

    public const string FormatsProfileWithoutId = "In chat-formats.json, a profile has no valid \"id\", so Envoy skips it.";

    /// <summary>{profile}: a profile that can't be used.</summary>
    public const string FormatsProfileInvalid = "In chat-formats.json, a profile (\"{profile}\") isn't valid, so Envoy skips it.";

    public const string CopiedCaption = "Copied. It contains chat and player names, so only share it if you're happy to.";

    public const string CopyFailedCaption = "Envoy couldn't copy to the clipboard. Another app may be using it, so try again.";

    /// <summary><see cref="UnderstoodHeader"/>, then one line per entry (or <see cref="Nothing"/>).</summary>
    public static string Describe(IReadOnlyList<ChatMessage> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var lines = new List<string> { UnderstoodHeader };
        if (entries.Count == 0) lines.Add(Nothing);
        lines.AddRange(entries.Select(Entry));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>One entry as shown under "Understood as".</summary>
    public static string Entry(ChatMessage entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var c = CultureInfo.CurrentCulture;
        var line = entry.Kind switch
        {
            ChatLineKind.System => TextTemplate.Format(NoticeEntry, c, ("text", entry.Text)),
            ChatLineKind.Fragment => TextTemplate.Format(FragmentEntry, c, ("text", entry.Text)),
            _ when entry.Sender is null => TextTemplate.Format(ChatEntryNoSender, c, ("text", entry.Text)),
            _ when entry.Channel is null => TextTemplate.Format(ChatEntryNoChannel, c, ("sender", entry.Sender), ("text", entry.Text)),
            _ => TextTemplate.Format(ChatEntry, c, ("channel", entry.Channel), ("sender", entry.Sender), ("text", entry.Text)),
        };
        return entry.LineCount > 1 ? TextTemplate.Format(LineCount, c, ("entry", line), ("count", entry.LineCount)) : line;
    }

    /// <summary>The card's error text: one sentence per problem, or "" when there are none.</summary>
    public static string Errors(IReadOnlyList<PatternError>? patternErrors, IReadOnlyList<ChatFormatError>? formatErrors)
    {
        var c = CultureInfo.CurrentCulture;
        var lines = new List<string>();
        foreach (var error in patternErrors ?? [])
            lines.Add(TextTemplate.Format(PatternInvalid, c, ("line", error.Index + 1)));
        foreach (var error in formatErrors ?? [])
        {
            lines.Add(error switch
            {
                { Kind: ChatFormatErrorKind.FileUnreadable } => FormatsFileUnreadable,
                { Profile: { } p, Field: { } f } => TextTemplate.Format(FormatsFieldInvalid, c, ("field", f), ("profile", p)),
                { Profile: null, Field: "id" } => FormatsProfileWithoutId,
                { Profile: { } p } => TextTemplate.Format(FormatsProfileInvalid, c, ("profile", p)),
                { Field: { } f } => TextTemplate.Format(FormatsSettingInvalid, c, ("field", f)),
                _ => FormatsFileUnreadable,
            });
        }
        return string.Join(Environment.NewLine, lines.Distinct(StringComparer.Ordinal));
    }
}
