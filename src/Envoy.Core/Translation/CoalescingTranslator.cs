namespace Envoy.Core.Translation;

/// <summary>
/// Concurrent requests with the same key share one underlying call and all receive its result or exception
/// (REQ-TRR-14). A waiter that cancels ends only its own wait; the shared call is cancelled once every waiter has
/// cancelled (REQ-TRR-15).
/// </summary>
public sealed class CoalescingTranslator(ITranslator inner, Func<TranslationRequest, string> key) : ITranslator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);

    public string Id => inner.Id;
    public bool IsMetered => inner.IsMetered;

    /// <summary>Calls in flight (for tests).</summary>
    public int InFlight
    {
        get
        {
            lock (_gate) return _flights.Count;
        }
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var k = key(request);
        Flight flight;
        var owner = false;
        lock (_gate)
        {
            if (!_flights.TryGetValue(k, out flight!))
            {
                flight = new Flight();
                _flights[k] = flight;
                owner = true;
            }
            flight.Waiters++;
        }

        if (owner) _ = RunAsync(k, flight, request); // never throws: the outcome goes to the flight

        try
        {
            return await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (--flight.Waiters == 0 && !flight.Done)
                {
                    // Nobody is waiting any more: cancel the shared call and let a new request start afresh.
                    if (_flights.TryGetValue(k, out var current) && ReferenceEquals(current, flight)) _flights.Remove(k);
                    flight.Cts.Cancel();
                }
            }
            throw;
        }
    }

    private async Task RunAsync(string k, Flight flight, TranslationRequest request)
    {
        try
        {
            var result = await inner.TranslateAsync(request, flight.Cts.Token).ConfigureAwait(false);
            Finish(k, flight);
            flight.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            Finish(k, flight);
            flight.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            Finish(k, flight);
            flight.Completion.TrySetException(ex);
        }
    }

    private void Finish(string k, Flight flight)
    {
        lock (_gate)
        {
            flight.Done = true;
            if (_flights.TryGetValue(k, out var current) && ReferenceEquals(current, flight)) _flights.Remove(k);
            flight.Cts.Dispose();
        }
    }

    private sealed class Flight
    {
        public TaskCompletionSource<TranslationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cts { get; } = new();
        public int Waiters { get; set; }
        public bool Done { get; set; }
    }
}
