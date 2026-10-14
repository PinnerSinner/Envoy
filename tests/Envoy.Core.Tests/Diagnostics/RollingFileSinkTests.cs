using Envoy.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

public sealed class RollingFileSinkTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "envoy-log-tests-" + Guid.NewGuid().ToString("N"));

    public RollingFileSinkTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static LogEntry Entry(string message) =>
        new(DateTimeOffset.UnixEpoch, LogLevel.Info, "Test", message, "", null);

    [Fact]
    public void Rolls_over_and_keeps_max_files_without_touching_other_files()
    {
        var transcript = Path.Combine(_folder, "chat-2026-10-14_180000.txt");
        File.WriteAllText(transcript, "chat");
        var other = Path.Combine(_folder, "envoy-notes.log");
        File.WriteAllText(other, "keep");

        const int Cap = 1024;
        using (var sink = new RollingFileSink(_folder, maxBytes: Cap, maxFiles: 3))
        {
            var line = new string('x', 60);
            var written = 0;
            var i = 0;
            while (written < 3 * Cap)
            {
                var entry = Entry($"{i++:D4} {line}");
                sink.Write(entry);
                written += LogFormatter.Format(entry).Length + Environment.NewLine.Length;
            }
            sink.Write(Entry("LAST"));
        }

        var logs = Directory.GetFiles(_folder).Select(Path.GetFileName).Where(n => n!.EndsWith(".log", StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(["envoy-notes.log", "envoy.1.log", "envoy.2.log", "envoy.log"], logs);
        foreach (var name in new[] { "envoy.log", "envoy.1.log", "envoy.2.log" })
            Assert.True(new FileInfo(Path.Combine(_folder, name)).Length <= Cap + 120, name);
        Assert.Contains("LAST", File.ReadAllText(Path.Combine(_folder, "envoy.log")), StringComparison.Ordinal);
        Assert.Equal("chat", File.ReadAllText(transcript));
        Assert.Equal("keep", File.ReadAllText(other));
    }

    [Fact]
    public void Appends_across_instances()
    {
        using (var first = new RollingFileSink(_folder)) first.Write(Entry("one"));
        using (var second = new RollingFileSink(_folder)) second.Write(Entry("two"));
        var lines = File.ReadAllLines(Path.Combine(_folder, "envoy.log"));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("one", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("two", lines[1], StringComparison.Ordinal);
        Assert.False(File.ReadAllBytes(Path.Combine(_folder, "envoy.log"))[0] == 0xEF, "no UTF-8 BOM");
    }

    [Fact]
    public void Oversized_existing_file_rolls_on_first_write()
    {
        File.WriteAllText(Path.Combine(_folder, "envoy.log"), new string('o', 5000));
        using (var sink = new RollingFileSink(_folder, maxBytes: 1024, maxFiles: 5)) sink.Write(Entry("fresh"));
        Assert.Equal(5000, new FileInfo(Path.Combine(_folder, "envoy.1.log")).Length);
        Assert.EndsWith("fresh", File.ReadAllText(Path.Combine(_folder, "envoy.log")).TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unwritable_target_never_throws()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "envoy.log")); // a directory squatting on the file name
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var hub = new LogHub(clock);
        var sink = new RollingFileSink(_folder, clock: clock);
        hub.AddSink(sink);
        var log = hub.For("Test");

        log.Error("first");
        Assert.Equal(1, hub.DroppedWrites);
        log.Error("second"); // inside the retry delay: dropped without touching the disk
        Assert.Equal(2, hub.DroppedWrites);

        Directory.Delete(Path.Combine(_folder, "envoy.log"));
        clock.Advance(RollingFileSink.RetryDelay);
        log.Error("third");
        Assert.Equal(2, hub.DroppedWrites);
        hub.Dispose();
        Assert.Contains("third", File.ReadAllText(Path.Combine(_folder, "envoy.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_roll_keeps_appending_past_the_cap_and_retries_later()
    {
        // envoy.1.log as a non-empty folder makes the rename fail, like a viewer holding the file without FILE_SHARE_DELETE.
        var blocker = Path.Combine(_folder, "envoy.1.log");
        Directory.CreateDirectory(blocker);
        File.WriteAllText(Path.Combine(blocker, "held"), "x");
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        const int Cap = 400;
        var current = Path.Combine(_folder, "envoy.log");
        using var sink = new RollingFileSink(_folder, maxBytes: Cap, maxFiles: 2, clock: clock);

        var i = 0;
        while (new FileInfo(current) is { Exists: false } || new FileInfo(current).Length <= Cap) sink.Write(Entry($"{i++:D3} {new string('a', 40)}"));
        sink.Write(Entry("over the cap"));
        Assert.Contains("over the cap", File.ReadAllText(current), StringComparison.Ordinal);
        Assert.True(Directory.Exists(blocker));

        // Up to the hard cap it keeps writing; past it, writes are dropped (thrown to the hub) rather than growing forever.
        var dropped = false;
        for (var n = 0; n < 100 && !dropped; n++)
        {
            try
            {
                sink.Write(Entry($"more {n} {new string('b', 40)}"));
            }
            catch (IOException)
            {
                dropped = true;
            }
        }
        Assert.True(dropped);
        Assert.True(new FileInfo(current).Length <= Cap * RollingFileSink.HardCapFactor);

        // Released: after the retry delay the roll succeeds and envoy.log starts afresh.
        Directory.Delete(blocker, recursive: true);
        clock.Advance(RollingFileSink.RetryDelay);
        sink.Write(Entry("after release"));
        Assert.True(File.Exists(blocker));
        Assert.Contains("over the cap", File.ReadAllText(blocker), StringComparison.Ordinal);
        Assert.Contains("after release", File.ReadAllText(current), StringComparison.Ordinal);
        Assert.True(new FileInfo(current).Length < Cap);
    }

    [Fact]
    public void ExistingFiles_lists_newest_first()
    {
        File.WriteAllText(Path.Combine(_folder, "envoy.2.log"), "2");
        File.WriteAllText(Path.Combine(_folder, "envoy.1.log"), "1");
        File.WriteAllText(Path.Combine(_folder, "envoy.9.log"), "beyond maxFiles");
        using var sink = new RollingFileSink(_folder);
        sink.Write(Entry("now"));
        Assert.Equal(["envoy.log", "envoy.1.log", "envoy.2.log"], sink.ExistingFiles().Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("envoy.log", true)]
    [InlineData("envoy.3.log", true)]
    [InlineData("chat-2026.txt", false)]
    [InlineData("envoy-notes.log", false)]
    [InlineData("envoy.log.bak", false)]
    public void Only_envoy_log_names_are_log_files(string name, bool expected) =>
        Assert.Equal(expected, RollingFileSink.IsLogFileName(name));
}
