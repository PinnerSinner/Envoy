using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace Envoy.Core.Capture;

/// <summary>
/// Pixel buffers for captured frames (REQ-CAP-20). A dedicated <see cref="ArrayPool{T}"/> (at most 2 arrays per size
/// bucket, up to 64 MiB each) gives deterministic reuse and bounded retention. Thread-safe.
/// </summary>
public sealed class FramePool
{
    private const int MaxArrayLength = 64 * 1024 * 1024;
    private const int ArraysPerBucket = 2;

    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Create(MaxArrayLength, ArraysPerBucket);
    private readonly object _gate = new();
    private int _outstanding;
    private long _outstandingBytes;
    private long _highWater;

    /// <summary>Buffers rented and not yet returned (the soak run and tests watch it).</summary>
    public int Outstanding
    {
        get
        {
            lock (_gate) return _outstanding;
        }
    }

    /// <summary>The most bytes of pooled arrays out at once since this pool was created.</summary>
    public long RetainedBytesHighWater
    {
        get
        {
            lock (_gate) return _highWater;
        }
    }

    /// <summary>A BGRA32 buffer for a <paramref name="width"/>×<paramref name="height"/> frame. Dispose it to return it.</summary>
    public FrameBuffer Rent(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var length = checked(width * 4 * height);
        var array = _pool.Rent(length);
        lock (_gate)
        {
            _outstanding++;
            _outstandingBytes += array.Length;
            if (_outstandingBytes > _highWater) _highWater = _outstandingBytes;
        }
        return new FrameBuffer(this, array, width, height);
    }

    internal void Return(byte[] array)
    {
        lock (_gate)
        {
            _outstanding--;
            _outstandingBytes -= array.Length;
        }
        _pool.Return(array);
    }
}

/// <summary>A rented BGRA32, top-down frame with stride = width × 4. <see cref="Array"/> may be longer than <see cref="Length"/>.</summary>
public sealed class FrameBuffer : IDisposable
{
    private readonly FramePool _pool;
    private int _disposed;

    internal FrameBuffer(FramePool pool, byte[] array, int width, int height)
    {
        _pool = pool;
        Array = array;
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;

    /// <summary>The frame's bytes: width × 4 × height.</summary>
    public int Length => Width * 4 * Height;

    /// <summary>The pooled array, for native calls that write into it and length-bounded views. Valid until disposed.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "The pooled array itself is the point: native code writes into it and OCR wraps it without a copy.")]
    public byte[] Array { get; }

    /// <summary>Exactly <see cref="Length"/> bytes of <see cref="Array"/>.</summary>
    public Span<byte> Pixels => Array.AsSpan(0, Length);

    /// <summary>An exact-length copy that outlives this buffer (Live view frames, Scan &amp; translate).</summary>
    public byte[] ToArray() => Pixels.ToArray();

    /// <summary>Returns the array to the pool. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _pool.Return(Array);
    }
}
