using System.Globalization;
using Envoy.Core.Text;

namespace Envoy.Core.Ux;

/// <summary>
/// One chapter of the Tale (spec 09b, REQ-MOT-9): a playful heading and one plain sentence that says what Envoy does.
/// <paramref name="Index"/> is 0-based; <paramref name="Numeral"/> is the Roman numeral on its seal.
/// </summary>
public sealed record TaleChapter(int Index, string Numeral, string Heading, string Sentence)
{
    /// <summary>The seal's accessible name: "Chapter III: The interpreter answers".</summary>
    public string SealName => TextTemplate.Format(TaleText.SealName, CultureInfo.CurrentCulture, ("numeral", Numeral), ("heading", Heading));
}

/// <summary>Every word of the Tale window (spec 09b, REQ-MOT-9). No em dashes; medieval flavour in the headings only.</summary>
public static class TaleText
{
    public const string WindowTitle = "Envoy: the tale";
    public const string Title = "The Tale of Envoy";
    public const string Subtitle = "or, how to parley with any foe";

    /// <summary>{numeral} = the chapter's Roman numeral.</summary>
    public const string ChapterOf = "Chapter {numeral} of VI";

    /// <summary>{numeral} = the chapter's Roman numeral, {heading} = its heading.</summary>
    public const string SealName = "Chapter {numeral}: {heading}";

    public const string Play = "Play";
    public const string Pause = "Pause";
    public const string Next = "Next";
    public const string Back = "Back";
    public const string Skip = "Skip the tale";
    public const string SetUp = "Set up Envoy";
    public const string WatchAgain = "Watch again";
    public const string Offer = "Watch a one-minute tour";

    /// <summary>Under reduced motion or none, the Tale does not play by itself.</summary>
    public const string StillHint = "Animations are off, so each chapter shows its finished picture. Press Next to go on.";

    public const string Heading1 = "A babel upon the field";
    public const string Sentence1 = "Players in your matches write in Chinese, Korean, Russian and many more languages.";
    public const string Heading2 = "The scout keeps watch";
    public const string Sentence2 = "Envoy reads the chat on your screen with Windows text recognition, the way OBS sees your screen.";
    public const string Heading3 = "The interpreter answers";
    public const string Sentence3 = "Each new message is translated and shown over the game as a scroll, sealed with its language.";
    public const string Heading4 = "The scribe takes your words";

    /// <summary>{hotkey} = the reply hotkey, such as "Ctrl+Shift+T".</summary>
    public const string Sentence4 = "Press {hotkey} and write in your own language, and Envoy writes the reply in theirs as you type.";

    /// <summary>Chapter IV when no reply hotkey is set.</summary>
    public const string Sentence4NoHotkey = "Open the reply box from the tray and write in your own language; Envoy writes the reply in theirs.";

    public const string Heading5 = "The courier rides out";
    public const string Sentence5 = "Seal it, and Envoy types the translation into game chat for you, the way a keyboard would.";
    public const string Heading6 = "Yours to command";
    public const string Sentence6 = "Envoy is free to use, wears a livery for every Age and is private by default, with no telemetry.";

    /// <summary>The three promises sealed in chapter VI.</summary>
    public static IReadOnlyList<string> Promises { get; } =
    [
        "Free translators need no account. A paid one uses your own key.",
        "A livery for every Age: II, III, IV and Mythology.",
        "No telemetry. What Envoy keeps stays on your PC.",
    ];

    /// <summary>Labels drawn inside the chapter pictures.</summary>
    public const string ScreenLabel = "the game, as you see it";
    public const string ScoutReads = "the scout reads these lines";
    public const string ProviderNote = "by Google Translate, free, in under a breath";
    public const string ReplyTo = "eply to 전략가, in Korean";
    public const string YourWords = "Your words";
    public const string ScribeWrites = "The scribe writes";
    public const string GameChat = "game chat";
    public const string Delivered = "The courier has delivered it.";

    /// <summary>The Roman numerals of the six seals.</summary>
    public static IReadOnlyList<string> Numerals { get; } = ["I", "II", "III", "IV", "V", "VI"];

    /// <summary>The six chapters; <paramref name="replyHotkey"/> is the reply combination ("" or null when none is set).</summary>
    public static IReadOnlyList<TaleChapter> Chapters(string? replyHotkey)
    {
        var sentence4 = string.IsNullOrWhiteSpace(replyHotkey)
            ? Sentence4NoHotkey
            : TextTemplate.Format(Sentence4, CultureInfo.CurrentCulture, ("hotkey", replyHotkey.Trim()));
        return
        [
            new(0, Numerals[0], Heading1, Sentence1),
            new(1, Numerals[1], Heading2, Sentence2),
            new(2, Numerals[2], Heading3, Sentence3),
            new(3, Numerals[3], Heading4, sentence4),
            new(4, Numerals[4], Heading5, Sentence5),
            new(5, Numerals[5], Heading6, Sentence6),
        ];
    }

    /// <summary>"Chapter III of VI".</summary>
    public static string ChapterOfText(int index) =>
        TextTemplate.Format(ChapterOf, CultureInfo.CurrentCulture, ("numeral", Numerals[Math.Clamp(index, 0, Numerals.Count - 1)]));
}
