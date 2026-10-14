using Envoy.Core.Diagnostics;
using Envoy.Core.Tests.Translation.Fakes;
using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Xunit;

namespace Envoy.Core.Tests.Translation;

public class TranslationLoggingTests
{
    private static (ResilientSetup Setup, MemoryLogSink Sink, IDisposable Logging) Attach(string providerId)
    {
        var s = new ResilientSetup(providerId, jitter: 0);
        var hub = new LogHub(s.Clock);
        var sink = new MemoryLogSink();
        hub.AddSink(sink);
        return (s, sink, TranslationLogging.Attach(s.Runtime, hub.For("Translation")));
    }

    [Fact]
    public async Task Logs_each_transition_at_Info_under_Translation()
    {
        var (s, sink, logging) = Attach(ProviderCatalog.Claude);
        using (logging)
        {
            s.Inner.Default = (_, _) => Task.FromException<TranslationResult>(new TranslationException("x", null, TranslationErrorKind.Auth, 401));
            await Assert.ThrowsAsync<TranslationException>(() => s.Chat.TranslateAsync(ResilientSetup.Request()));
        }
        var transition = Assert.Single(sink.Snapshot(), e => e.Message.StartsWith(TranslationLogging.TransitionMessage, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Info, transition.Level);
        Assert.Equal("Translation", transition.Category);
        Assert.Equal("Provider state claude AuthFailed", transition.Message);
        Assert.Contains("provider=claude", transition.Fields, StringComparison.Ordinal);
        Assert.Contains("state=AuthFailed", transition.Fields, StringComparison.Ordinal);
        Assert.Contains("kind=Auth", transition.Fields, StringComparison.Ordinal);

        // Detached: no more entries.
        var count = sink.Snapshot().Count;
        s.Runtime.ResetProvider(ProviderCatalog.Claude);
        Assert.Equal(count, sink.Snapshot().Count);
    }

    [Fact]
    public async Task Logs_diagnostic_at_Info_without_text()
    {
        var (s, sink, logging) = Attach(ProviderCatalog.DeepL);
        using (logging)
        {
            s.Inner.Fail(TranslationErrorKind.Transient, 503).Succeed();
            await s.Chat.TranslateAsync(ResilientSetup.Request("这是聊天文本"));
        }
        var attempts = sink.Snapshot().Where(e => e.Message.StartsWith(TranslationLogging.AttemptMessage, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, e => Assert.Equal(LogLevel.Info, e.Level));
        Assert.Contains("outcome=failure", attempts[0].Fields, StringComparison.Ordinal);
        Assert.Contains("kind=Transient", attempts[0].Fields, StringComparison.Ordinal);
        Assert.Contains("status=503", attempts[0].Fields, StringComparison.Ordinal);
        Assert.Contains("attempt=1", attempts[0].Fields, StringComparison.Ordinal);
        Assert.Contains("outcome=success", attempts[1].Fields, StringComparison.Ordinal);
        Assert.Contains("attempt=2", attempts[1].Fields, StringComparison.Ordinal);
        Assert.All(sink.Snapshot(), e => Assert.DoesNotContain("聊天", LogFormatter.Format(e), StringComparison.Ordinal));
    }
}
