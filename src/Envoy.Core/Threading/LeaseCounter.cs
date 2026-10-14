namespace Envoy.Core.Threading;

/// <summary>
/// A thread-safe count of outstanding leases. Each <see cref="Acquire"/> returns a lease whose first
/// <see cref="IDisposable.Dispose"/> gives it back; disposing it again does nothing. Backs
/// <c>ChatWatcher.BeginPreview()</c>, so two live views (settings page, setup guide) never switch each other off.
/// </summary>
public sealed class LeaseCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public bool IsHeld => Count > 0;

    public IDisposable Acquire()
    {
        Interlocked.Increment(ref _count);
        return new Lease(this);
    }

    private sealed class Lease(LeaseCounter owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref owner._count);
        }
    }
}
