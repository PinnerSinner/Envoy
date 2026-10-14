using Envoy.Core.Diagnostics;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

public sealed class DataInventoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-inv-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] Seeded =
    [
        "settings.json", "settings.json.bak", "settings.json.v1.bak", "settings.json.reset-20261014-180000.bak",
        "settings.json.broken", "settings.json.tmp", "phrases.json", "logs/envoy.log", "logs/envoy.1.log",
        "logs/chat-2026-10-14_180000.txt", "last-crash.json", "last-crash.json.tmp", "session.json", "error.log",
    ];

    public DataInventoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "logs"));
        foreach (var rel in Seeded) File.WriteAllText(Full(rel), rel);
        File.WriteAllText(Full("notes.txt"), "mine");
        File.WriteAllText(Full("logs/mine.txt"), "mine");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string Full(string rel) => Path.Combine(_folder, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Rel(string full) => Path.GetRelativePath(_folder, full).Replace(Path.DirectorySeparatorChar, '/');

    [Fact]
    public void Enumerate_lists_only_inventory()
    {
        var files = DataInventory.Enumerate(_folder);
        Assert.Equal(Seeded.Order(StringComparer.Ordinal), files.Select(f => Rel(f.Path)).Order(StringComparer.Ordinal));
        Assert.All(files, f => Assert.True(f.Bytes > 0));
        Assert.Equal("logs/envoy*.log", files.Single(f => Rel(f.Path) == "logs/envoy.1.log").Pattern);
        Assert.Empty(DataInventory.Enumerate(Path.Combine(_folder, "missing")));
    }

    [Fact]
    public void DeleteAll_deletes_only_inventory()
    {
        Directory.CreateDirectory(Full("logs/sub"));
        File.WriteAllText(Full("logs/sub/envoy.log"), "nested, never touched");

        var result = DataInventory.DeleteAll(_folder);

        Assert.Equal(Seeded.Length, result.Deleted.Count);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(Full("notes.txt")));
        Assert.True(File.Exists(Full("logs/mine.txt")));
        Assert.True(File.Exists(Full("logs/sub/envoy.log")));
        Assert.All(Seeded, rel => Assert.False(File.Exists(Full(rel)), rel));
    }

    [Fact]
    public void DeleteAll_removes_empty_folders()
    {
        File.Delete(Full("notes.txt"));
        File.Delete(Full("logs/mine.txt"));
        var result = DataInventory.DeleteAll(_folder);
        Assert.Empty(result.Failed);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Locked_file_is_reported()
    {
        DeleteResult result;
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(Full("session.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            result = DataInventory.DeleteAll(_folder);
        }
        else
        {
            // Unix lets an open file be deleted, so simulate the sharing violation Windows raises.
            result = DataInventory.DeleteAll(_folder, path =>
            {
                if (path == Full("session.json")) throw new IOException("The file is in use by another process.");
                File.Delete(path);
            });
        }
        Assert.Equal([Full("session.json")], result.Failed);
        Assert.True(File.Exists(Full("session.json")));
        Assert.Equal(Seeded.Length - 1, result.Deleted.Count);
        Assert.True(Directory.Exists(_folder)); // not empty, so kept
    }

    [Fact]
    public void Every_item_is_documented_in_privacy_md()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Envoy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var privacy = File.ReadAllText(Path.Combine(dir.FullName, "docs", "privacy.md"));
        Assert.Contains("## What Envoy stores on your PC", privacy, StringComparison.Ordinal);
        foreach (var item in DataInventory.Items)
            Assert.Contains("`" + item.Pattern + "`", privacy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings.json", "settings.json")]
    [InlineData("settings.json.v3.bak", "settings.json.v*.bak")]
    [InlineData("logs/envoy.4.log", "logs/envoy*.log")]
    [InlineData(@"logs\chat-x.txt", "logs/chat-*.txt")]
    [InlineData("logs/sub/envoy.log", null)]
    [InlineData("notes.txt", null)]
    public void Match_uses_the_patterns(string rel, string? pattern) =>
        Assert.Equal(pattern, DataInventory.Match(rel)?.Pattern);
}
