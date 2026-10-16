namespace Envoy.Core.Chat;

/// <summary>What one entry of a capture is (spec 04).</summary>
public enum ChatLineKind
{
    /// <summary>A player talking: a line with a sender, plus any wrapped continuation lines.</summary>
    Chat,

    /// <summary>A game notice matched by a notice pattern (or the player-name heuristic). Never translated.</summary>
    System,

    /// <summary>Sender-less lines with no message above them, usually the tail of a message that scrolled away.</summary>
    Fragment,
}

/// <summary>A chat message read from the game, split into its parts. One message can span several screen lines.</summary>
/// <param name="Raw">The (cleaned) source lines, joined with <c>\n</c>.</param>
/// <param name="Channel">"All", "Team", ... when the game prefixed one, otherwise null.</param>
/// <param name="Sender">Player name before the colon, when one could be found.</param>
/// <param name="Text">The message body; wrapped lines are joined (REQ-CHAT-7).</param>
public sealed record ChatMessage(string Raw, string? Channel, string? Sender, string Text)
{
    public ChatLineKind Kind { get; init; } = ChatLineKind.Chat;

    /// <summary>How many screen lines the message spans (REQ-CHAT-8).</summary>
    public int LineCount { get; init; } = 1;

    /// <summary>Top of the first line in the captured image, or 0 when unknown.</summary>
    public double Top { get; init; }

    /// <summary>Bottom of the last line in the captured image, or 0 when unknown.</summary>
    public double Bottom { get; init; }
}
