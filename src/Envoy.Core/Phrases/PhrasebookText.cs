namespace Envoy.Core.Phrases;

/// <summary>User-facing text for the Phrasebook page (contract §8.7).</summary>
public static class PhrasebookText
{
    /// <summary>Shown in the preview when a phrase has no translation in the chosen language.</summary>
    public const string Missing = "—";

    /// <summary>Caption of the box shown while the user edits their phrases file.</summary>
    public const string EditTitle = "Envoy";

    /// <summary>Body of the box shown while the user edits their phrases file.</summary>
    public const string EditInstructions =
        "Your phrases file is open. Save it, then press OK to reload.\n\nUse the same \"id\" as a built-in phrase to replace it.";

    /// <summary>Why phrases.json was not loaded (completes <c>PageHintText.PhrasesNotLoaded</c>). {line} is 1-based.</summary>
    public const string LoadInvalidJsonAt = "it isn't valid JSON (line {line})";

    /// <summary>Why phrases.json was not loaded, when the parser gives no line.</summary>
    public const string LoadInvalidJson = "it isn't valid JSON";

    /// <summary>Why phrases.json was not loaded: over <c>PhraseLimits.MaxFileBytes</c> (08a).</summary>
    public const string LoadTooLarge = "it is larger than 1 MB";

    /// <summary>Why phrases.json was not loaded: locked or not allowed.</summary>
    public const string LoadUnreadable = "it can't be read";
}
