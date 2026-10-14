using System.Globalization;

namespace Envoy.Core.Capture;

/// <summary>One minute's resource reading of <c>Envoy.exe --soak</c> (REQ-CAP-43).</summary>
public sealed record SoakSample(int Minute, long PrivateBytes, int Handles, int GdiObjects, int UserObjects,
                                int PoolOutstanding, long PoolRetainedBytes, int History, int Players);

/// <summary>The soak run's pass rule: nothing may grow by more than 20 % between minute 5 and the end (contract §8.5).</summary>
public static class SoakBudget
{
    /// <summary>The minute whose sample is the baseline (start-up allocations have settled by then).</summary>
    public const int BaselineMinute = 5;

    /// <summary>
    /// Compares the first sample at or after minute 5 with the last one and returns one log line per value that grew by
    /// more than <paramref name="maxGrowth"/> (and by more than one unit, so a count of 0 → 1 is not a leak). Empty for
    /// runs that end at or before the baseline.
    /// </summary>
    public static IReadOnlyList<string> Violations(IReadOnlyList<SoakSample> samples, double maxGrowth = 0.20)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var baseline = samples.FirstOrDefault(s => s.Minute >= BaselineMinute);
        var last = samples.Count > 0 ? samples[^1] : null;
        if (baseline is null || last is null || last.Minute <= baseline.Minute) return [];

        var lines = new List<string>();
        void Compare(string name, long before, long after)
        {
            if (after - before > 1 && after > before * (1 + maxGrowth))
            {
                var growth = before == 0 ? "from zero" : ((after - before) * 100.0 / before).ToString("0", CultureInfo.InvariantCulture) + "%";
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name} grew {growth} between minute {baseline.Minute} and minute {last.Minute} ({before} → {after})"));
            }
        }

        Compare(nameof(SoakSample.PrivateBytes), baseline.PrivateBytes, last.PrivateBytes);
        Compare(nameof(SoakSample.Handles), baseline.Handles, last.Handles);
        Compare(nameof(SoakSample.GdiObjects), baseline.GdiObjects, last.GdiObjects);
        Compare(nameof(SoakSample.UserObjects), baseline.UserObjects, last.UserObjects);
        Compare(nameof(SoakSample.PoolOutstanding), baseline.PoolOutstanding, last.PoolOutstanding);
        Compare(nameof(SoakSample.PoolRetainedBytes), baseline.PoolRetainedBytes, last.PoolRetainedBytes);
        Compare(nameof(SoakSample.History), baseline.History, last.History);
        Compare(nameof(SoakSample.Players), baseline.Players, last.Players);
        return lines;
    }
}
