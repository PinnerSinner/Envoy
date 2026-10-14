using Envoy.Core.Capture;
using Xunit;

namespace Envoy.Core.Tests.Capture;

public class FrameProcessingTests
{
    private static byte[] Frame(int width, int height, byte value)
    {
        var p = new byte[width * height * 4];
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = p[i + 1] = p[i + 2] = value;
            p[i + 3] = 255;
        }
        return p;
    }

    private static byte[] RandomFrame(int width, int height, int seed)
    {
        var p = new byte[width * height * 4];
        new Random(seed).NextBytes(p);
        return p;
    }

    /// <summary>The v0.1 ScreenCapturer.Fingerprint, kept here to pin the new hash to it.</summary>
    private static ulong V01Fingerprint(byte[] p)
    {
        const ulong prime = 1099511628211;
        var hash = 14695981039346656037;
        for (var i = 1; i < p.Length; i += 28)
        {
            hash ^= (ulong)(p[i] >> 3);
            hash *= prime;
        }
        return hash;
    }

    /// <summary>The v0.1 ScreenCapturer.Preprocess (per-pixel double maths), kept as the reference (REQ-CAP-22).</summary>
    private static void V01Preprocess(byte[] p, bool grayscale, double contrast, bool invert)
    {
        static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        for (var i = 0; i < p.Length; i += 4)
        {
            double b = p[i], g = p[i + 1], r = p[i + 2];
            if (grayscale)
            {
                var y = 0.299 * r + 0.587 * g + 0.114 * b;
                r = g = b = y;
            }
            if (contrast != 1.0)
            {
                r = (r - 128) * contrast + 128;
                g = (g - 128) * contrast + 128;
                b = (b - 128) * contrast + 128;
            }
            if (invert)
            {
                r = 255 - r;
                g = 255 - g;
                b = 255 - b;
            }
            p[i] = Clamp(b);
            p[i + 1] = Clamp(g);
            p[i + 2] = Clamp(r);
            p[i + 3] = 255;
        }
    }

    [Fact]
    public void Analyze_identical_frames_have_equal_hash()
    {
        var a = RandomFrame(64, 16, 1);
        var b = (byte[])a.Clone();
        Assert.Equal(FrameProcessing.Analyze(a), FrameProcessing.Analyze(b));
        Assert.Equal(V01Fingerprint(a), FrameProcessing.Analyze(a).Hash);
        Assert.NotEqual(FrameProcessing.Analyze(a).Hash, FrameProcessing.Analyze(RandomFrame(64, 16, 2)).Hash);
    }

    [Fact]
    public void Analyze_ignores_sub_quantisation_shimmer()
    {
        var a = Frame(64, 16, 64);
        var b = Frame(64, 16, 66); // 64 >> 3 == 66 >> 3
        Assert.Equal(FrameProcessing.Analyze(a).Hash, FrameProcessing.Analyze(b).Hash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(250)]
    public void Analyze_detects_8px_text_run(int startPixel)
    {
        var blank = Frame(64, 16, 10);
        var text = Frame(64, 16, 10);
        for (var px = startPixel; px < startPixel + 8; px++) text[px * 4 + 1] = 240;
        Assert.NotEqual(FrameProcessing.Analyze(blank).Hash, FrameProcessing.Analyze(text).Hash);
        Assert.False(FrameProcessing.Analyze(text).IsUniform());
    }

    [Fact]
    public void Analyze_reports_uniform_for_black_frame()
    {
        var black = FrameProcessing.Analyze(Frame(320, 120, 0));
        Assert.True(black.IsUniform());
        Assert.Equal(0, black.MinLuma);
        Assert.Equal(0, black.MaxLuma);
        var noisy = Frame(320, 120, 0);
        for (var i = 1; i < noisy.Length; i += 56) noisy[i] = 8; // within the tolerance
        Assert.True(FrameProcessing.Analyze(noisy).IsUniform());
        Assert.False(FrameProcessing.Analyze(RandomFrame(320, 120, 3)).IsUniform());
    }

    [Fact]
    public void Analyze_does_not_allocate()
    {
        var frame = RandomFrame(320, 120, 4);
        FrameProcessing.Analyze(frame); // JIT
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++) FrameProcessing.Analyze(frame);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    public static TheoryData<bool, double, bool> PreprocessCases()
    {
        var data = new TheoryData<bool, double, bool>();
        foreach (var grayscale in new[] { false, true })
        {
            foreach (var contrast in new[] { 0.5, 1.0, 1.3, 3.0 })
            {
                foreach (var invert in new[] { false, true }) data.Add(grayscale, contrast, invert);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PreprocessCases))]
    public void Preprocess_matches_v01_within_one(bool grayscale, double contrast, bool invert)
    {
        var expected = RandomFrame(64, 16, 42);
        var actual = (byte[])expected.Clone();
        V01Preprocess(expected, grayscale, contrast, invert);
        FrameProcessing.Preprocess(actual, new PreprocessOptions(grayscale, contrast, invert));
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1, $"byte {i}: v0.1 {expected[i]}, new {actual[i]}");
    }

    [Fact]
    public void Preprocess_does_not_allocate()
    {
        var frame = RandomFrame(320, 120, 5);
        var options = new PreprocessOptions(true, 1.3, true);
        FrameProcessing.Preprocess(frame, options); // JIT
        FrameProcessing.Preprocess(frame, options with { Grayscale = false });
        var before = GC.GetAllocatedBytesForCurrentThread();
        FrameProcessing.Preprocess(frame, options);
        FrameProcessing.Preprocess(frame, options with { Grayscale = false });
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Preprocess_sets_alpha_opaque()
    {
        var frame = RandomFrame(16, 4, 6);
        FrameProcessing.Preprocess(frame, new PreprocessOptions(false, 1.0, false));
        for (var i = 3; i < frame.Length; i += 4) Assert.Equal(255, frame[i]);
    }
}

public class BlankFramesTests
{
    private static readonly FrameSignature Uniform = new(1, 10, 12);
    private static readonly FrameSignature Busy = new(2, 10, 200);

    [Fact]
    public void Streak_counts_uniform_frames_and_saturates_at_threshold()
    {
        var streak = 0;
        for (var i = 1; i <= BlankFrames.Threshold; i++)
        {
            streak = BlankFrames.Next(streak, Uniform);
            Assert.Equal(i, streak);
        }
        Assert.Equal(BlankFrames.Threshold, BlankFrames.Next(streak, Uniform));
    }

    [Fact]
    public void Non_uniform_frame_resets() =>
        Assert.Equal(0, BlankFrames.Next(BlankFrames.Threshold, Busy));
}

public class FramePoolTests
{
    [Fact]
    public void Rent_returns_exact_length_span_and_stride()
    {
        var pool = new FramePool();
        using var buffer = pool.Rent(30, 7);
        Assert.Equal(30 * 4 * 7, buffer.Length);
        Assert.Equal(buffer.Length, buffer.Pixels.Length);
        Assert.Equal(120, buffer.Stride);
        Assert.True(buffer.Array.Length >= buffer.Length);
    }

    [Fact]
    public void Dispose_returns_array_and_next_rent_reuses_it()
    {
        var pool = new FramePool();
        var first = pool.Rent(320, 120);
        var array = first.Array;
        first.Dispose();
        using var second = pool.Rent(320, 120);
        Assert.Same(array, second.Array);
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        var pool = new FramePool();
        var buffer = pool.Rent(10, 10);
        buffer.Dispose();
        buffer.Dispose();
        Assert.Equal(0, pool.Outstanding);
        using var a = pool.Rent(10, 10);
        using var b = pool.Rent(10, 10);
        Assert.NotSame(a.Array, b.Array); // the array went back once, not twice
    }

    [Fact]
    public void ToArray_copies_exact_pixels()
    {
        var pool = new FramePool();
        using var buffer = pool.Rent(5, 3);
        for (var i = 0; i < buffer.Length; i++) buffer.Pixels[i] = (byte)i;
        var copy = buffer.ToArray();
        Assert.Equal(buffer.Length, copy.Length);
        Assert.NotSame(buffer.Array, copy);
        Assert.Equal(buffer.Pixels.ToArray(), copy);
    }

    [Fact]
    public void Outstanding_tracks_rent_and_return()
    {
        var pool = new FramePool();
        var a = pool.Rent(100, 10);
        var b = pool.Rent(100, 10);
        Assert.Equal(2, pool.Outstanding);
        Assert.True(pool.RetainedBytesHighWater >= 2 * 4000);
        a.Dispose();
        Assert.Equal(1, pool.Outstanding);
        b.Dispose();
        Assert.Equal(0, pool.Outstanding);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(0, 10));
    }
}
