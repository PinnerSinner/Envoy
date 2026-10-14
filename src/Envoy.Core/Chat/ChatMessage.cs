namespace Envoy.Core.Chat;

/// <summary>A single chat line read from the game, split into its parts.</summary>
/// <param name="Raw">The line exactly as OCR read it.</param>
/// <param name="Channel">"All", "Team", ... when the game prefixed one, otherwise null.</param>
/// <param name="Sender">Player name before the colon, when one could be found.</param>
/// <param name="Text">The message body.</param>
public sealed record ChatMessage(string Raw, string? Channel, string? Sender, string Text);
