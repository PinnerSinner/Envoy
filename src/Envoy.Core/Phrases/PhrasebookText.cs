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
}
