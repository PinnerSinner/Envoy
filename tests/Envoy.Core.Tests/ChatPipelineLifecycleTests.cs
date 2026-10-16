using System.Collections.Concurrent;
using Envoy.Core.Ocr;
using Envoy.Core.Pipeline;
using Envoy.Core.Translation;
using Xunit;

namespace Envoy.Core.Tests;

/// <summary>A translator that holds every request until released; it honours the request's token.</summary>
internal sealed class GatedTranslator : ITranslator
{
    private readonly ConcurrentQueue<TaskCompletionSource<TranslationResult>> _waiting = new();
    private int _started;

    public string Id => "gated";
    public bool IsMetered => false;
    public int Started => Volatile.Read(ref _started);
    public Exception? Throw { get; set; }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (Throw is not null) throw Throw;
        var gate = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting.Enqueue(gate);
        Interlocked.Increment(ref _started);
        using var registration = cancellationToken.Register(() => gate.TrySetCanceled(cancellationToken));
        var result = await gate.Task.ConfigureAwait(false);
        return result with { Text = "T:" + request.Text };
    }

    /// <summary>Completes every request still waiting.</summary>
    public void ReleaseAll()
    {
        while (_waiting.TryDequeue(out var gate)) gate.TrySetResult(new TranslationResult("", "zh-CN", Id));
    }

    public async Task WaitForStartedAsync(int count)
    {
        for (var i = 0; i < 500 && Started < count; i++) await Task.Delay(10);
        Assert.True(Started >= count, $"only {Started} of {count} translations started");
    }
}

public class ChatPipelineLifecycleTests
{
    private static IReadOnlyList<OcrLine> Lines(params string[] texts) => OcrLine.FromTexts(texts, 18, 4);

    private sealed class Recorder
    {
        public ConcurrentDictionary<Guid, int> Updates { get; } = new();
        public ConcurrentBag<IncomingMessage> Received { get; } = [];

        public Recorder(ChatPipeline p)
        {
            p.MessageReceived += Received.Add;
            p.MessageUpdated += m => Updates.AddOrUpdate(m.Id, 1, (_, n) => n + 1);
        }

        public void AssertEachUpdatedOnce()
        {
            Assert.All(Received.Where(m => m.Status != IncomingStatus.Untranslated || Updates.ContainsKey(m.Id)),
                m => Assert.Equal(1, Updates.GetValueOrDefault(m.Id)));
            Assert.DoesNotContain(Received, m => m.Status == IncomingStatus.Pending);
        }
    }

    private static void AssertCancelled(IncomingMessage m)
    {
        Assert.Equal(IncomingStatus.Failed, m.Status);
        Assert.Equal(PipelineText.Cancelled, m.Error);
        Assert.True(m.WasCancelled);
    }

    [Fact]
    public async Task Caller_token_cancel_mid_translation_still_completes()
    {
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        using var watcher = new CancellationTokenSource();

        var run = p.ProcessAsync(Lines("小明: 你好"), watcher.Token);
        await t.WaitForStartedAsync(1);
        await watcher.CancelAsync(); // the watcher stops
        t.ReleaseAll();

        var msg = Assert.Single(await run);
        Assert.Equal(IncomingStatus.Translated, msg.Status);
        Assert.False(msg.WasCancelled);
        r.AssertEachUpdatedOnce();
    }

    [Fact]
    public async Task NewMatch_mid_translation_ends_failed_cancelled()
    {
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        var run = p.ProcessAsync(Lines("小明: 你好"));
        await t.WaitForStartedAsync(1);

        p.NewMatch();

        AssertCancelled(Assert.Single(await run));
        r.AssertEachUpdatedOnce();
        // The new match translates normally.
        var next = p.ProcessAsync(Lines("小明: 再见"));
        await t.WaitForStartedAsync(2);
        t.ReleaseAll();
        Assert.Equal(IncomingStatus.Translated, Assert.Single(await next).Status);
    }

    [Fact]
    public async Task Reset_mid_translation_ends_failed_cancelled()
    {
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        var run = p.ProcessAsync(Lines("小明: 你好"));
        await t.WaitForStartedAsync(1);

        p.Reset();

        AssertCancelled(Assert.Single(await run));
        r.AssertEachUpdatedOnce();
    }

    [Fact]
    public async Task Dispose_mid_translation_ends_failed_cancelled()
    {
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        var run = p.ProcessAsync(Lines("小明: 你好", "Kim: 안녕하세요"));
        await t.WaitForStartedAsync(2);

        p.Dispose();

        var messages = await run;
        Assert.Equal(2, messages.Count);
        Assert.All(messages, AssertCancelled);
        r.AssertEachUpdatedOnce();
        p.Dispose(); // twice is fine
    }

    [Fact]
    public async Task Translator_OperationCanceled_ends_failed_cancelled()
    {
        var t = new GatedTranslator { Throw = new OperationCanceledException() };
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        AssertCancelled(Assert.Single(await p.ProcessAsync(Lines("小明: 你好"))));
        r.AssertEachUpdatedOnce();
    }

    [Fact]
    public async Task Every_pending_message_gets_exactly_one_update()
    {
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        string[] names = ["Amber", "Basil", "Cedar", "Dahlia", "Ember", "Fennel", "Garnet", "Hazel", "Iris", "Juniper", "Kestrel", "Laurel"];
        string[] texts = ["你好", "再来一局", "进攻吧", "小心快攻", "我去采金", "需要木头", "城堡时代", "守住城墙", "发展经济", "建造农场", "侦察敌人", "撤退"];
        var runs = Enumerable.Range(0, 3)
            .Select(c => p.ProcessAsync(Lines(Enumerable.Range(c * 4, 4).Select(i => $"{names[i]}: {texts[i]}").ToArray())))
            .ToList();
        await t.WaitForStartedAsync(12);

        p.NewMatch();
        t.ReleaseAll();

        var all = (await Task.WhenAll(runs)).SelectMany(m => m).ToList();
        Assert.Equal(12, all.Count);
        Assert.Equal(12, r.Updates.Count);
        Assert.All(r.Updates.Values, n => Assert.Equal(1, n));
        Assert.DoesNotContain(all, m => m.Status == IncomingStatus.Pending);
        r.AssertEachUpdatedOnce();
    }

    [Fact]
    public async Task Process_after_dispose_returns_empty()
    {
        var t = new GatedTranslator();
        var p = new ChatPipeline(t, new ChatPipelineOptions());
        var r = new Recorder(p);
        p.Dispose();
        Assert.Empty(await p.ProcessAsync(Lines("小明: 你好")));
        Assert.Empty(r.Received);
        Assert.Equal(0, t.Started);
    }
}
