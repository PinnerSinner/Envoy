namespace Envoy.Core.Translation;

/// <summary>Remembers recent translations: "gg", "gl hf" and spam repeat a lot, and every repeat is free.</summary>
public sealed class CachingTranslator(ITranslator inner, int capacity = 500) : ITranslator
{
    private readonly Dictionary<string, LinkedListNode<(string Key, TranslationResult Value)>> _map = new();
    private readonly LinkedList<(string Key, TranslationResult Value)> _lru = new();
    private readonly object _gate = new();

    public string Id => inner.Id;
    public bool IsMetered => inner.IsMetered;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var key = $"{request.SourceLanguage}\u001f{request.TargetLanguage}\u001f{request.Text.Trim()}";
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var hit))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
                return hit.Value.Value with { FromCache = true };
            }
        }

        var result = await inner.TranslateAsync(request, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_map.ContainsKey(key))
            {
                _map[key] = _lru.AddFirst((key, result));
                while (_lru.Count > capacity)
                {
                    _map.Remove(_lru.Last!.Value.Key);
                    _lru.RemoveLast();
                }
            }
        }
        return result;
    }
}
