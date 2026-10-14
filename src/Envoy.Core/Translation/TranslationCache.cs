using System.Security.Cryptography;
using System.Text;

namespace Envoy.Core.Translation;

/// <summary>A saved translation (REQ-TRR-20). Only a one-way fingerprint of the original line is kept.</summary>
/// <param name="KeyHash">SHA-256 (hex) of <see cref="TranslationCache.Key"/>.</param>
public sealed record CacheEntry(string KeyHash, string Text, string? DetectedSourceLanguage, string ProviderId, DateTimeOffset At);

/// <summary>
/// The translation memory (REQ-TRR-19): an LRU of results, owned by <see cref="TranslationRuntime"/> so it survives
/// translator rebuilds. Entries are indexed by the SHA-256 of their key, so entries loaded from disk (which hold only the
/// hash) and live entries are found the same way. Thread-safe.
/// </summary>
public sealed class TranslationCache
{
    /// <summary>Saved entries older than this are dropped when loaded.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Requests longer than this are kept in memory only.</summary>
    public const int MaxPersistableLength = 200;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, LinkedListNode<Item>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Item> _lru = new();
    private int _capacity;
    private long _version;
    private long _savedVersion;
    private int _persistable;

    public TranslationCache(int capacity, TimeProvider? clock = null)
    {
        _capacity = Math.Max(1, capacity);
        _clock = clock ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            lock (_gate) return _map.Count;
        }
    }

    /// <summary>Entries that would be saved to disk.</summary>
    public int PersistableCount
    {
        get
        {
            lock (_gate) return _persistable;
        }
    }

    /// <summary>True when persistable entries changed since the last <see cref="MarkSaved"/>.</summary>
    public bool IsDirty
    {
        get
        {
            lock (_gate) return _version != _savedVersion;
        }
    }

    /// <summary>A change counter; pass the value read before <see cref="Export"/> to <see cref="MarkSaved"/>.</summary>
    public long Version
    {
        get
        {
            lock (_gate) return _version;
        }
    }

    /// <summary>Shrinking evicts the least recently used entries.</summary>
    public int Capacity
    {
        get
        {
            lock (_gate) return _capacity;
        }
        set
        {
            lock (_gate)
            {
                _capacity = Math.Max(1, value);
                Trim();
            }
        }
    }

    /// <summary><c>ns␟source␟target␟trimmed text</c>. The namespace keeps providers' output apart.</summary>
    public static string Key(string ns, TranslationRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"{ns}\u001f{r.SourceLanguage}\u001f{r.TargetLanguage}\u001f{r.Text.Trim()}";
    }

    /// <summary>The SHA-256 of <paramref name="key"/> as upper-case hex: the only form of the original saved to disk.</summary>
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key ?? "")));

    public bool TryGet(string key, out TranslationResult result)
    {
        var hash = Hash(key);
        lock (_gate)
        {
            if (_map.TryGetValue(hash, out var node))
            {
                if (_clock.GetUtcNow() - node.Value.At <= MaxAge)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    result = node.Value.Result;
                    return true;
                }
                Remove(node);
            }
        }
        result = null!;
        return false;
    }

    /// <param name="persistable">False for glossary results and long requests: they never reach the disk.</param>
    public void Set(string key, TranslationResult result, bool persistable)
    {
        ArgumentNullException.ThrowIfNull(result);
        var hash = Hash(key);
        var stored = result with { FromCache = false };
        lock (_gate)
        {
            if (_map.TryGetValue(hash, out var existing)) Remove(existing);
            Add(new Item(hash, stored, persistable, _clock.GetUtcNow()), first: true);
            if (persistable) _version++;
            Trim();
        }
    }

    /// <summary>Empties the cache. Nothing is left to save, so it is not dirty afterwards.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            _persistable = 0;
            _version++;
            _savedVersion = _version;
        }
    }

    /// <summary>Persistable entries, most recently used first.</summary>
    public IReadOnlyList<CacheEntry> Export()
    {
        lock (_gate)
        {
            return [.. _lru.Where(i => i.Persistable)
                .Select(i => new CacheEntry(i.KeyHash, i.Result.Text, i.Result.DetectedSourceLanguage, i.Result.ProviderId, i.At))];
        }
    }

    /// <summary>Adds saved entries (most recent first), keeping their order and dropping those older than <see cref="MaxAge"/>.</summary>
    public void Import(IEnumerable<CacheEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var e in entries.Reverse())
            {
                if (string.IsNullOrEmpty(e.KeyHash) || now - e.At > MaxAge || _map.ContainsKey(e.KeyHash)) continue;
                Add(new Item(e.KeyHash, new TranslationResult(e.Text, e.DetectedSourceLanguage, e.ProviderId), true, e.At), first: true);
            }
            Trim();
        }
    }

    /// <summary>Marks the state at <paramref name="version"/> as saved.</summary>
    public void MarkSaved(long version)
    {
        lock (_gate)
        {
            if (version > _savedVersion) _savedVersion = Math.Min(version, _version);
        }
    }

    private void Add(Item item, bool first)
    {
        var node = first ? _lru.AddFirst(item) : _lru.AddLast(item);
        _map[item.KeyHash] = node;
        if (item.Persistable) _persistable++;
    }

    private void Remove(LinkedListNode<Item> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.KeyHash);
        if (node.Value.Persistable)
        {
            _persistable--;
            _version++;
        }
    }

    private void Trim()
    {
        while (_lru.Count > _capacity) Remove(_lru.Last!);
    }

    private sealed record Item(string KeyHash, TranslationResult Result, bool Persistable, DateTimeOffset At);
}
