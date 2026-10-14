using System.Text.Json.Nodes;
using Envoy.Core.Settings;
using Xunit;

namespace Envoy.Core.Tests.Settings;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-migrate-" + Guid.NewGuid().ToString("N"));

    public SettingsMigrationTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    private sealed class Step(int from, Action<JsonObject> apply) : ISettingsMigration
    {
        public int FromVersion { get; } = from;
        public string Description => "step " + FromVersion;
        public void Apply(JsonObject settings) => apply(settings);
    }

    [Fact]
    public void Missing_version_means_v1()
    {
        Assert.Equal(1, SettingsMigrator.ReadVersion(new JsonObject()));
        Assert.Equal(1, SettingsMigrator.ReadVersion(new JsonObject { ["version"] = "two" }));
        Assert.Equal(3, SettingsMigrator.ReadVersion(new JsonObject { ["version"] = 3 }));
        Assert.Equal(2, SettingsMigrator.ReadVersion(new JsonObject { ["Version"] = 2 }));
    }

    [Fact]
    public void Runs_steps_in_order()
    {
        var order = new List<int>();
        var migrator = new SettingsMigrator(
        [
            new Step(2, o => { order.Add(2); o["b"] = (int)o["a"]! + 1; }),
            new Step(1, o => { order.Add(1); o["a"] = 1; }),
        ], currentVersion: 3);
        var json = new JsonObject();
        var run = migrator.Migrate(json, 1);
        Assert.Equal([1, 2], order);
        Assert.Equal(2, run.Count);
        Assert.Equal(2, (int)json["b"]!);
        Assert.Equal(3, (int)json["version"]!);

        order.Clear();
        migrator.Migrate(new JsonObject { ["a"] = 5 }, 2);
        Assert.Equal([2], order);
    }

    [Fact]
    public void Invalid_chain_throws()
    {
        Assert.Throws<ArgumentException>(() => new SettingsMigrator([new Step(1, _ => { })], currentVersion: 3)); // gap
        Assert.Throws<ArgumentException>(() => new SettingsMigrator([new Step(1, _ => { }), new Step(1, _ => { })], currentVersion: 2)); // duplicate
        Assert.Throws<ArgumentException>(() => new SettingsMigrator([new Step(2, _ => { })], currentVersion: 2)); // beyond current
        Assert.Throws<ArgumentException>(() => new SettingsMigrator([], currentVersion: 2)); // missing
        _ = new SettingsMigrator([], currentVersion: 1);
    }

    [Fact]
    public void Shipped_registry_is_consistent()
    {
        Assert.Empty(SettingsMigrations.All); // contract §7: no steps until a real migration ships
        _ = new SettingsMigrator(SettingsMigrations.All, AppSettings.CurrentVersion);
    }

    [Fact]
    public void Backs_up_before_migrating()
    {
        const string V1 = """{ "version": 1, "myLanguage": "de", "oldName": "ZZ" }""";
        File.WriteAllText(SettingsPath, V1);
        var store = new SettingsStore(SettingsPath, new NoOpProtector(), null, null,
            [new Step(1, o => { o["myPlayerName"] = o["oldName"]!.GetValue<string>(); o.Remove("oldName"); })], currentVersion: 2);

        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.Migrated, result.Status);
        Assert.Equal(1, result.FileVersion);
        Assert.Equal("ZZ", result.Settings.MyPlayerName);
        Assert.Equal(SettingsPath + ".v1.bak", result.BackupPath);
        Assert.Equal(V1, File.ReadAllText(SettingsPath + ".v1.bak"));
        store.Save(result.Settings);
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(SettingsPath))!["version"]!);
    }

    [Fact]
    public void Newer_version_loads_best_effort_with_backup()
    {
        const string V9 = """{ "version": 9, "myLanguage": "ko", "somethingNew": { "x": 1 } }""";
        File.WriteAllText(SettingsPath, V9);
        var store = new SettingsStore(SettingsPath, new NoOpProtector());

        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.NewerVersion, result.Status);
        Assert.Equal(9, result.FileVersion);
        Assert.Equal("ko", result.Settings.MyLanguage);
        Assert.Equal(V9, File.ReadAllText(SettingsPath + ".v9.bak"));

        File.WriteAllText(SettingsPath, V9.Replace("ko", "ja", StringComparison.Ordinal));
        store.LoadWithReport();
        Assert.Equal(V9, File.ReadAllText(SettingsPath + ".v9.bak")); // copied once
    }

    [Fact]
    public void Migration_failure_returns_MigrationFailed_and_keeps_backup()
    {
        const string V1 = """{ "version": 1, "myLanguage": "ru" }""";
        File.WriteAllText(SettingsPath, V1);
        var store = new SettingsStore(SettingsPath, new NoOpProtector(), null, null,
            [new Step(1, _ => throw new InvalidOperationException("step broke"))], currentVersion: 2);

        var result = store.LoadWithReport();

        Assert.Equal(SettingsLoadStatus.MigrationFailed, result.Status);
        Assert.Equal("ru", result.Settings.MyLanguage);
        Assert.Contains("step broke", result.Error, StringComparison.Ordinal);
        Assert.Equal(V1, File.ReadAllText(SettingsPath + ".v1.bak"));
        Assert.False(File.Exists(store.LastKnownGoodPath));
    }
}
