using Envoy.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Envoy.Core.Tests.Diagnostics;

/// <summary>A sink that records every entry, for tests.</summary>
internal sealed class ListSink : ILogSink
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];

    public int Flushes { get; private set; }

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate) return [.. _entries];
        }
    }

    public void Write(LogEntry entry)
    {
        lock (_gate) _entries.Add(entry);
    }

    public void Flush() => Flushes++;
}

internal sealed class ThrowingSink : ILogSink
{
    public void Write(LogEntry entry) => throw new IOException("disk full");
    public void Flush() => throw new IOException("disk full");
}

public class LoggingTests
{
    private static (LogHub Hub, ListSink Sink) NewHub(TimeProvider? clock = null)
    {
        var hub = new LogHub(clock);
        var sink = new ListSink();
        hub.AddSink(sink);
        return (hub, sink);
    }

    [Fact]
    public void Hub_filters_below_minimum_level()
    {
        var (hub, sink) = NewHub();
        var log = hub.For("Test");
        log.Debug("hidden");
        log.Info("info");
        log.Warn("warn");
        Assert.Equal(["info", "warn"], sink.Entries.Select(e => e.Message));

        hub.MinimumLevel = LogLevel.Debug;
        Assert.True(log.IsEnabled(LogLevel.Debug));
        log.Debug("shown");
        Assert.Equal("shown", sink.Entries[^1].Message);
        Assert.Equal("Test", sink.Entries[^1].Category);

        hub.MinimumLevel = LogLevel.Error;
        log.Warn("dropped");
        log.Critical("kept");
        Assert.Equal("kept", sink.Entries[^1].Message);
        Assert.DoesNotContain(sink.Entries, e => e.Message == "dropped");
    }

    [Fact]
    public void Concurrent_writes_all_delivered()
    {
        var (hub, sink) = NewHub();
        var memory = new MemoryLogSink(10_000);
        hub.AddSink(memory);
        const int Writers = 8;
        const int PerWriter = 500;
        Parallel.For(0, Writers, new ParallelOptions { MaxDegreeOfParallelism = Writers }, w =>
        {
            var log = hub.For("W" + w);
            for (var i = 0; i < PerWriter; i++) log.Info("entry " + i);
        });
        Assert.Equal(Writers * PerWriter, sink.Entries.Count);
        Assert.Equal(Writers * PerWriter, memory.Snapshot().Count);
        Assert.Equal(0, hub.DroppedWrites);
    }

    [Fact]
    public void Throwing_sink_is_isolated()
    {
        var hub = new LogHub();
        hub.AddSink(new ThrowingSink());
        var good = new ListSink();
        hub.AddSink(good);
        var log = hub.For("Test");

        log.Error("one");
        log.Error("two");
        hub.Flush();

        Assert.Equal(["one", "two"], good.Entries.Select(e => e.Message));
        Assert.True(hub.DroppedWrites >= 2);
    }

    public static TheoryData<string, string> SecretCases => new()
    {
        { "registered", "hunter2-registered-value" },
        { "anthropic", "sk-ant-api03-abcdefghijKLMNOP" },
        { "aws", "AKIAABCDEFGHIJKLMNOP" },
        { "deepl", "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b:fx" },
        { "url-query", "https://translate.example.com/x?q=hello&key=abc" },
        { "url-userinfo", "https://user:pa55word@host.example.com/x" },
        { "header", "x-api-key: abc123secretvalue" },
    };

    [Theory]
    [MemberData(nameof(SecretCases))]
    public void Redacts_secrets_everywhere(string kind, string secret)
    {
        var (hub, sink) = NewHub();
        hub.Redactor.SetSecrets(["hunter2-registered-value"]);
        var log = hub.For("Test");
        var sensitivePart = kind switch
        {
            "url-query" => "q=hello",
            "url-userinfo" => "pa55word",
            "header" => "abc123secretvalue",
            _ => secret,
        };

        log.Warn("message " + secret);
        log.Warn("fields", null, LogField.Of("value", secret));
        log.Warn("exception", new InvalidOperationException("failed with " + secret));

        foreach (var entry in sink.Entries)
        {
            var all = entry.Message + " " + entry.Fields + " " + entry.Exception;
            Assert.DoesNotContain(sensitivePart, all, StringComparison.Ordinal);
            Assert.Contains(LogRedactor.Mask, all, StringComparison.Ordinal);
        }
        Assert.Equal(3, sink.Entries.Count);
    }

    [Fact]
    public void Url_keeps_host_and_path_and_plain_guid_survives()
    {
        var redactor = new LogRedactor();
        Assert.Equal("https://[redacted]@h/x?[redacted]", redactor.Redact("https://u:p@h/x?q=hello&key=abc"));
        Assert.Equal("see https://github.com/PinnerSinner/Envoy/releases now",
            redactor.Redact("see https://github.com/PinnerSinner/Envoy/releases now"));
        const string Guid = "0f1e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b";
        Assert.Equal("id " + Guid, redactor.Redact("id " + Guid));
        Assert.Equal("Authorization: [redacted] next", redactor.Redact("Authorization: DeepL-Auth-Key abcdef123 next"));
        Assert.Equal("auth_key=[redacted]&text=x", redactor.Redact("auth_key=abcdef123&text=x"));
    }

    [Fact]
    public void Short_values_are_not_registered_as_secrets()
    {
        var redactor = new LogRedactor();
        redactor.SetSecrets(["", "   ", "abc", "12345", null, "123456"]);
        Assert.Equal(["123456"], redactor.Secrets);
        Assert.Equal("abc 12345 [redacted]", redactor.Redact("abc 12345 123456"));
    }

    [Fact]
    public void Secrets_are_a_snapshot()
    {
        var settings = new Envoy.Core.Settings.AppSettings();
        settings.Translation.DeepLKey = "first-deepl-key-value";
        var redactor = new LogRedactor();
        redactor.SetSecrets(Envoy.Core.Settings.SettingsRedaction.SecretValues(settings));

        settings.Translation.DeepLKey = "second-deepl-key-value";
        Assert.Equal("[redacted]", redactor.Redact("first-deepl-key-value"));
        Assert.Equal("second-deepl-key-value", redactor.Redact("second-deepl-key-value"));
    }

    [Fact]
    public void Longer_secrets_are_masked_before_their_prefixes()
    {
        var redactor = new LogRedactor();
        redactor.SetSecrets(["abcdef", "abcdefghijkl"]);
        Assert.Equal("[redacted]", redactor.Redact("abcdefghijkl"));
    }

    [Fact]
    public void Sensitive_fields_masked_unless_opted_in()
    {
        var (hub, sink) = NewHub();
        var log = hub.For("Chat");
        log.Info("line", LogField.Sensitive("text", "你好世界"));
        Assert.Equal("text=<4 chars>", sink.Entries[^1].Fields);

        hub.IncludeSensitive = true;
        log.Info("line 2", LogField.Sensitive("text", "say \"hi\"\r\nbye"));
        Assert.Equal("text=\"say \\\"hi\\\"\\r\\nbye\"", sink.Entries[^1].Fields);

        hub.IncludeSensitive = false;
        log.Info("line 3", LogField.Sensitive("text", null), LogField.Of("count", 3), LogField.Of("name", "two words"));
        Assert.Equal("text=<0 chars> count=3 name=\"two words\"", sink.Entries[^1].Fields);
    }

    [Fact]
    public void Formatter_writes_one_line_per_entry_plus_indented_exception()
    {
        var time = new DateTimeOffset(2026, 10, 14, 18, 3, 12, 481, TimeSpan.Zero);
        var entry = new LogEntry(time, LogLevel.Warning, "Translation", "Provider failed", "provider=deepl", null);
        Assert.Equal("2026-10-14T18:03:12.481Z WRN [Translation] Provider failed provider=deepl", LogFormatter.Format(entry));

        Exception ex;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }
        var withException = entry with { Message = "two\nlines", Level = LogLevel.Critical, Exception = ex.ToString() };
        var lines = LogFormatter.Format(withException).Split(Environment.NewLine);
        Assert.Equal("2026-10-14T18:03:12.481Z CRT [Translation] two\\nlines provider=deepl", lines[0]);
        Assert.True(lines.Length >= 3);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("  ", l, StringComparison.Ordinal));
        Assert.Contains("boom", lines[1], StringComparison.Ordinal);

        var repeated = entry with { RepeatCount = 4 };
        Assert.Contains("Provider failed (repeated 4 times in 60 s) provider=deepl", LogFormatter.Format(repeated), StringComparison.Ordinal);
    }

    [Fact]
    public void Forget_logs_faults_and_cancellation()
    {
        var (hub, sink) = NewHub();
        hub.MinimumLevel = LogLevel.Debug;
        var log = hub.For("Tasks");

        Task.FromException(new InvalidOperationException("background boom")).Forget(log, "x");
        var fault = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Contains("background boom", fault.Exception, StringComparison.Ordinal);
        Assert.Equal("x failed", fault.Message);

        Task.FromCanceled(new CancellationToken(true)).Forget(log, "y");
        Assert.Equal(LogLevel.Debug, sink.Entries[^1].Level);

        var pending = new TaskCompletionSource();
        pending.Task.Forget(log, "later");
        pending.SetException(new TimeoutException("late"));
        Assert.Equal(LogLevel.Error, sink.Entries[^1].Level);
        Assert.Contains("late", sink.Entries[^1].Exception, StringComparison.Ordinal);

        Task.CompletedTask.Forget(log, "ok");
        Assert.Equal(3, sink.Entries.Count);
    }

    [Fact]
    public void Memory_sink_is_a_ring()
    {
        var memory = new MemoryLogSink(3);
        for (var i = 0; i < 5; i++)
        {
            var level = i % 2 == 0 ? LogLevel.Warning : LogLevel.Info;
            memory.Write(new LogEntry(DateTimeOffset.UnixEpoch, level, "C", "m" + i, "", null));
        }
        Assert.Equal(["m2", "m3", "m4"], memory.Snapshot().Select(e => e.Message));
        Assert.Equal(["m2", "m4"], memory.Snapshot(LogLevel.Warning).Select(e => e.Message));
        Assert.Equal(["m4"], memory.Snapshot(max: 1).Select(e => e.Message));
    }

    [Fact]
    public void User_profile_path_is_aliased()
    {
        var (hub, sink) = NewHub();
        hub.Redactor.AddPathAlias(@"C:\Users\Jane.Doe", "%USERPROFILE%");
        hub.For("Test").Warn(@"could not open c:\users\jane.doe\AppData\Roaming\Envoy\settings.json");
        Assert.Equal(@"could not open %USERPROFILE%\AppData\Roaming\Envoy\settings.json", sink.Entries[^1].Message);
    }

    [Fact]
    public void Repeats_are_collapsed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var (hub, sink) = NewHub(clock);
        var log = hub.For("Capture");
        for (var i = 0; i < 5; i++)
        {
            log.Warn("OCR failed");
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        log.Warn("Something else");
        Assert.Equal(["OCR failed", "Something else"], sink.Entries.Select(e => e.Message));

        clock.Advance(TimeSpan.FromSeconds(60));
        log.Warn("OCR failed");
        Assert.Equal(4, sink.Entries.Count);
        Assert.Equal(4, sink.Entries[2].RepeatCount);
        Assert.Equal("OCR failed", sink.Entries[2].Message);
        Assert.Equal(0, sink.Entries[3].RepeatCount);

        log.Warn("OCR failed");
        hub.Flush();
        Assert.Equal(1, sink.Entries[^1].RepeatCount);
    }

    [Fact]
    public void Dispose_flushes_and_never_throws()
    {
        var hub = new LogHub();
        hub.AddSink(new ThrowingSink());
        var sink = new ListSink();
        hub.AddSink(sink);
        hub.For("x").Info("before");
        hub.Dispose();
        hub.Dispose();
        hub.For("x").Info("after");
        Assert.Equal(["before"], sink.Entries.Select(e => e.Message));
        Assert.True(sink.Flushes >= 1);
    }

    [Fact]
    public void A_field_whose_ToString_throws_is_dropped_not_thrown()
    {
        var (hub, sink) = NewHub();
        hub.For("x").Info("bad", LogField.Of("v", new BadToString()));
        Assert.Empty(sink.Entries);
        Assert.Equal(1, hub.DroppedWrites);
    }

    private sealed class BadToString
    {
        public override string ToString() => throw new InvalidOperationException("nope");
    }
}
