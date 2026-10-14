using System.Text.Json.Nodes;

namespace Envoy.Core.Settings;

/// <summary>One step that upgrades the raw settings JSON from <see cref="FromVersion"/> to <c>FromVersion + 1</c>.</summary>
public interface ISettingsMigration
{
    int FromVersion { get; }
    string Description { get; }
    void Apply(JsonObject settings);
}

/// <summary>The shipped migration steps. Empty until a real migration ships (contract §7).</summary>
public static class SettingsMigrations
{
    public static IReadOnlyList<ISettingsMigration> All { get; } = [];
}

/// <summary>
/// Runs ordered <see cref="ISettingsMigration"/> steps N→N+1 on the raw JSON (REQ-REL-30). The chain must start at 1
/// and be gap-free and duplicate-free up to <c>currentVersion</c>; anything else throws at construction.
/// </summary>
public sealed class SettingsMigrator
{
    private readonly ISettingsMigration[] _steps;

    /// <exception cref="ArgumentException">A step is missing, duplicated, or outside 1…currentVersion-1.</exception>
    public SettingsMigrator(IEnumerable<ISettingsMigration> migrations, int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        _steps = [.. migrations.OrderBy(m => m.FromVersion)];
        CurrentVersion = currentVersion;
        var expected = Enumerable.Range(1, currentVersion - 1).ToArray();
        var actual = _steps.Select(s => s.FromVersion).ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new ArgumentException(
                $"Settings migrations must cover versions 1..{currentVersion - 1} exactly once; found [{string.Join(", ", actual)}].",
                nameof(migrations));
        }
    }

    public int CurrentVersion { get; }

    /// <summary>The <c>version</c> property; 1 when it is missing or not a number.</summary>
    public static int ReadVersion(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var node = settings["version"] ?? settings["Version"];
        return node is JsonValue value && value.TryGetValue<int>(out var version) ? version : 1;
    }

    /// <summary>Applies every step from <paramref name="fromVersion"/> up, then sets <c>version</c>. Returns the steps run.</summary>
    public IReadOnlyList<ISettingsMigration> Migrate(JsonObject settings, int fromVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var run = _steps.Where(s => s.FromVersion >= fromVersion).ToList();
        foreach (var step in run) step.Apply(settings);
        settings.Remove("Version");
        settings["version"] = CurrentVersion;
        return run;
    }
}
