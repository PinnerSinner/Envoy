namespace Envoy.Core.Compose;

/// <summary>
/// Replies you sent or copied this session, newest first, recalled with Up/Down in the reply box (REQ-OUXC-39, 40).
/// Memory only: it is never saved or logged, so no chat text reaches disk unless transcripts are on. UI thread only.
/// </summary>
public sealed class ReplyHistory(int capacity = 50)
{
    private readonly List<string> _entries = [];
    private readonly int _capacity = Math.Max(1, capacity);
    private int _cursor = -1;
    private string _draft = "";

    /// <summary>Newest first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>True between the first Up and the moment the user types, sends or moves past the newest entry.</summary>
    public bool IsBrowsing => _cursor >= 0;

    /// <summary>Records a reply: trimmed, blank ignored, an exact duplicate moves to the front, the oldest beyond capacity drops. Ends browsing.</summary>
    public void Add(string? text)
    {
        EndBrowsing();
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return;
        _entries.RemoveAll(e => string.Equals(e, trimmed, StringComparison.Ordinal));
        _entries.Insert(0, trimmed);
        if (_entries.Count > _capacity) _entries.RemoveRange(_capacity, _entries.Count - _capacity);
    }

    /// <summary>
    /// The next older entry, or null when there is none (empty history, or already at the oldest). The first call saves
    /// <paramref name="currentText"/> as the draft that <see cref="Newer"/> restores.
    /// </summary>
    public string? Older(string? currentText)
    {
        if (_cursor + 1 >= _entries.Count) return null;
        if (_cursor < 0) _draft = currentText ?? "";
        return _entries[++_cursor];
    }

    /// <summary>The next newer entry; past the newest, the saved draft (and browsing ends). Null when not browsing.</summary>
    public string? Newer()
    {
        if (_cursor < 0) return null;
        if (--_cursor >= 0) return _entries[_cursor];
        var draft = _draft;
        EndBrowsing();
        return draft;
    }

    /// <summary>Called when the user types: the text stays, the next Up starts from the newest entry again.</summary>
    public void EndBrowsing()
    {
        _cursor = -1;
        _draft = "";
    }
}
