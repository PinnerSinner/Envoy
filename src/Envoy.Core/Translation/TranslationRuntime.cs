using Envoy.Core.Diagnostics;
using Envoy.Core.Translation.Resilience;

namespace Envoy.Core.Translation;

/// <summary>One attempt (or local skip) of one provider (REQ-TRR-26). Never carries chat text.</summary>
/// <param name="Outcome">"success", "failure", "skipped", "cancelled" or "not-set-up".</param>
/// <param name="Attempt">1-based; 0 for a call rejected before any attempt.</param>
public sealed record TranslationDiagnostic(string ProviderId, string Outcome, TranslationErrorKind? Kind,
                                           int? StatusCode, int Attempt, TimeSpan Elapsed, TranslationLane Lane);

/// <summary>
/// Long-lived translation state owned by <c>AppHost</c> (REQ-TRR-11, 16, 19, 20): per-provider breakers, throttles and
/// health, the memory cache and its opt-in disk copy. Survives <see cref="TranslatorFactory.Build"/> rebuilds; a
/// provider's state resets only when its configuration fingerprint changes or <see cref="ResetProvider"/> is called.
/// </summary>
public sealed class TranslationRuntime : IDisposable
{
    /// <summary>How often a dirty cache is saved while "Remember translations" is on.</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderGuard> _guards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Fingerprint, ITranslator Provider)> _shared = new(StringComparer.Ordinal);
    private readonly TranslationCacheStore? _store;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly ILog _log;
    private TranslationRuntimeOptions _options = TranslationRuntimeOptions.Default;
    private ITimer? _timer;
    private bool _persisting;
    private volatile bool _suppressed;
    private volatile bool _disposed;

    /// <param name="cachePath">Where saved translations go (<c>AppHost.TranslationCachePath</c>); null = never saved.</param>
    public TranslationRuntime(string? cachePath = null, TimeProvider? clock = null, LogRedactor? redactor = null, ILog? log = null)
    {
        Clock = clock ?? TimeProvider.System;
        _log = log ?? NullLog.Instance;
        Health = new ProviderHealthRegistry(Clock, redactor);
        Cache = new TranslationCache(_options.CacheMaxEntries, Clock);
        if (cachePath is not null) _store = new TranslationCacheStore(cachePath, _log);
    }

    public TimeProvider Clock { get; }

    /// <summary>The current snapshot (volatile read); any thread.</summary>
    public TranslationRuntimeOptions Options => Volatile.Read(ref _options);

    /// <summary>The single provider-health source (contract §2.1).</summary>
    public ProviderHealthRegistry Health { get; }

    public TranslationCache Cache { get; }

    /// <summary>The disk store, or null when this runtime never saves.</summary>
    public TranslationCacheStore? Store => _store;

    /// <summary>Raised on every provider state transition, on the thread that caused it (= <see cref="ProviderHealthRegistry.StateChanged"/>).</summary>
    public event Action<ProviderHealthSnapshot>? ProviderHealthChanged
    {
        add => Health.StateChanged += value;
        remove => Health.StateChanged -= value;
    }

    /// <summary>One per attempt or local skip; any thread (REQ-TRR-26).</summary>
    public event Action<TranslationDiagnostic>? Diagnostic;

    /// <summary>
    /// Publishes <paramref name="options"/> (UI thread, inside <c>ApplySettings</c>), resizes the cache, and loads the
    /// saved translations when remembering turns on, or deletes the file (and, on turning off, clears memory) when off.
    /// </summary>
    public void Configure(TranslationRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_disposed) return;
        Volatile.Write(ref _options, options);
        Cache.Capacity = options.CacheMaxEntries;
        if (_store is null) return;

        if (options.RememberTranslations)
        {
            if (_persisting || _suppressed) return;
            _persisting = true;
            _saveLock.Wait();
            try
            {
                var version = Cache.Version;
                Cache.Import(_store.Load());
                Cache.MarkSaved(version);
            }
            finally
            {
                _saveLock.Release();
            }
            _timer ??= Clock.CreateTimer(_ => SaveIfDirty(), null, SaveInterval, SaveInterval);
            return;
        }

        var wasPersisting = _persisting;
        _persisting = false;
        StopTimer();
        _saveLock.Wait();
        try
        {
            _store.Delete();
        }
        finally
        {
            _saveLock.Release();
        }
        if (wasPersisting) Cache.Clear();
    }

    /// <summary>A provider could not be built (missing key or URL): health shows it as "Not set up".</summary>
    public void RecordBuildFailure(string providerId, Exception error)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ArgumentNullException.ThrowIfNull(error);
        var classified = error as TranslationException
                         ?? new TranslationException(TranslationErrorText.ForKind(TranslationErrorKind.Configuration, providerId),
                             error, TranslationErrorKind.Configuration);
        lock (_gate) _guards.Remove(providerId); // a fixed key later starts from a fresh record
        Health.Entry(providerId).RecordBuildFailure(classified);
        Diagnostic?.Invoke(new TranslationDiagnostic(providerId, "not-set-up", TranslationErrorKind.Configuration, null, 0,
            TimeSpan.Zero, TranslationLane.Chat));
    }

    /// <summary>"Retry now": closes the breaker, refills the buckets and clears the provider's health.</summary>
    public void ResetProvider(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        ProviderGuard? guard;
        lock (_gate) _guards.TryGetValue(providerId, out guard);
        if (guard is not null) guard.Reset();
        else Health.Entry(providerId).Reset();
    }

    /// <summary>Empties memory and deletes the file. False when the file could not be deleted (logged).</summary>
    public bool ClearCache()
    {
        _saveLock.Wait();
        try
        {
            Cache.Clear();
            return _store?.Delete() ?? true;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Waits for an in-flight save, then never writes the file again (called by <c>AppHost.SuppressSave</c>), so a reset
    /// or "Delete all Envoy data" is not undone at exit.
    /// </summary>
    public void SuppressPersistence()
    {
        _saveLock.Wait();
        try
        {
            _suppressed = true;
        }
        finally
        {
            _saveLock.Release();
        }
        StopTimer();
    }

    public bool PersistenceSuppressed => _suppressed;

    /// <summary>Saves now if remembering is on, the cache is dirty and persistence is not suppressed.</summary>
    public void Flush() => SaveIfDirty();

    /// <summary>Flushes (unless suppressed), stops the save timer and disposes the shared providers.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        StopTimer();
        SaveIfDirty();
        _disposed = true;
        List<ITranslator> providers;
        lock (_gate)
        {
            providers = [.. _shared.Values.Select(v => v.Provider)];
            _shared.Clear();
        }
        foreach (var provider in providers) (provider as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The provider for <paramref name="providerId"/>, shared by every stack built with the same
    /// <paramref name="configFingerprint"/> (both lanes, every rebuild). Only providers that own resources
    /// (<see cref="IDisposable"/>, e.g. the AWS client) are kept; the one they replace on a fingerprint change, and all of
    /// them at <see cref="Dispose"/>, are disposed. Others are cheap and made fresh by <paramref name="create"/>.
    /// </summary>
    internal ITranslator? SharedProvider(string providerId, string configFingerprint, Func<ITranslator?> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        lock (_gate)
        {
            if (_shared.TryGetValue(providerId, out var kept) && kept.Fingerprint == configFingerprint) return kept.Provider;
        }

        ITranslator? created;
        try
        {
            created = create();
        }
        catch
        {
            ReleaseShared(providerId, null); // e.g. its key was removed: the old client is no longer used
            throw;
        }
        if (created is not IDisposable) return created;
        return ReleaseShared(providerId, (configFingerprint, created));
    }

    /// <summary>Stores <paramref name="next"/> (or removes the entry) and disposes the provider it replaces.</summary>
    private ITranslator? ReleaseShared(string providerId, (string Fingerprint, ITranslator Provider)? next)
    {
        (string Fingerprint, ITranslator Provider) old;
        bool hadOld;
        lock (_gate)
        {
            hadOld = _shared.TryGetValue(providerId, out old);
            if (hadOld && next is { } n && old.Fingerprint == n.Fingerprint)
            {
                // Another build raced us with the same configuration: keep the stored one.
                (n.Provider as IDisposable)?.Dispose();
                return old.Provider;
            }
            if (next is { } value && !_disposed) _shared[providerId] = value;
            else _shared.Remove(providerId);
        }
        if (hadOld) (old.Provider as IDisposable)?.Dispose();
        return next?.Provider;
    }

    /// <summary>
    /// The guard for <paramref name="providerId"/>. A different <paramref name="configFingerprint"/> (key, URL, model,
    /// region) replaces it and resets the provider's health.
    /// </summary>
    internal ProviderGuard Guard(string providerId, string configFingerprint)
    {
        ProviderGuard guard;
        bool reset;
        lock (_gate)
        {
            if (_guards.TryGetValue(providerId, out var existing) && existing.Fingerprint == configFingerprint) return existing;
            var entry = Health.Entry(providerId);
            guard = new ProviderGuard(providerId, configFingerprint, Clock, entry, d => Diagnostic?.Invoke(d));
            _guards[providerId] = guard;
            reset = existing is not null || entry.Snapshot().LastErrorKind == TranslationErrorKind.Configuration;
        }
        if (reset) guard.Health.Reset(); // outside the lock: it may raise StateChanged
        return guard;
    }

    /// <summary>The health entry of <paramref name="providerId"/> (budget marks and tests).</summary>
    internal ProviderHealthEntry HealthEntry(string providerId) => Health.Entry(providerId);

    private void SaveIfDirty()
    {
        if (_store is null || _disposed) return;
        _saveLock.Wait();
        try
        {
            if (_suppressed || _disposed || !_persisting || !Cache.IsDirty) return;
            var version = Cache.Version;
            if (_store.Save(Cache.Export())) Cache.MarkSaved(version);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private void StopTimer()
    {
        var timer = Interlocked.Exchange(ref _timer, null);
        timer?.Dispose();
    }
}
