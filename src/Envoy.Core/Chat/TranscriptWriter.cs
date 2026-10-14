using System.Globalization;
using System.Text;
using Envoy.Core.Pipeline;

namespace Envoy.Core.Chat;

/// <summary>
/// Optional plain-text log of every translated message, one file per session, so you can review a match later
/// or report a player. Off by default; stored only on your PC.
/// </summary>
public sealed class TranscriptWriter(string folder, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private string? _path;

    public string Folder { get; } = folder;
    public string? CurrentFile => _path;

    /// <summary>Formats a message as a single transcript line.</summary>
    public static string Format(IncomingMessage m, DateTimeOffset localTime)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ");
        if (m.Chat.Channel is { } channel) sb.Append('[').Append(channel).Append("] ");
        if (m.Chat.Sender is { } sender) sb.Append(sender);
        if (m.SourceLanguage is { } lang) sb.Append(" (").Append(lang).Append(')');
        if (m.Chat.Sender is not null || m.SourceLanguage is not null) sb.Append(": ");
        sb.Append(m.Chat.Text);
        if (m.Translation is { } t) sb.Append("  =>  ").Append(t);
        if (m.Status == IncomingStatus.Failed) sb.Append("  [translation failed: ").Append(m.Error).Append(']');
        return sb.ToString();
    }

    public void Append(IncomingMessage message)
    {
        if (message.Status == IncomingStatus.Pending) return;
        var now = _clock.GetLocalNow();
        var line = Format(message, now);
        lock (_gate)
        {
            if (_path is null)
            {
                Directory.CreateDirectory(Folder);
                _path = Path.Combine(Folder, $"chat-{now:yyyy-MM-dd_HHmmss}.txt");
                File.AppendAllText(_path, $"# Envoy chat transcript — {now:yyyy-MM-dd HH:mm}{Environment.NewLine}", Encoding.UTF8);
            }
            File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    /// <summary>Starts a new file on the next message (e.g. when a new match starts).</summary>
    public void Rotate()
    {
        lock (_gate) _path = null;
    }
}
