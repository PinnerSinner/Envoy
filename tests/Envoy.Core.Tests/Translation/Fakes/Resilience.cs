using Envoy.Core.Translation;
using Envoy.Core.Translation.Resilience;
using Microsoft.Extensions.Time.Testing;

namespace Envoy.Core.Tests.Translation.Fakes;

/// <summary>A runtime, a scripted provider and its resilient wrapper, on a fake clock.</summary>
internal sealed class ResilientSetup
{
    public ResilientSetup(string providerId, FakeTimeProvider? clock = null, double jitter = 1)
    {
        Clock = clock ?? TestClock.Create();
        Runtime = new TranslationRuntime(clock: Clock);
        Runtime.Diagnostic += d =>
        {
            lock (Diagnostics) Diagnostics.Add(d);
        };
        Runtime.ProviderHealthChanged += s =>
        {
            lock (Transitions) Transitions.Add(s);
        };
        Inner = new ScriptedTranslator(providerId);
        Guard = Runtime.Guard(providerId, "fingerprint");
        Chat = new ResilientTranslator(Inner, Guard, TranslationLane.Chat, Clock, () => jitter);
        Compose = new ResilientTranslator(Inner, Guard, TranslationLane.Compose, Clock, () => jitter);
    }

    public FakeTimeProvider Clock { get; }
    public TranslationRuntime Runtime { get; }
    public ScriptedTranslator Inner { get; }
    internal ProviderGuard Guard { get; }
    public ResilientTranslator Chat { get; }
    public ResilientTranslator Compose { get; }
    public List<TranslationDiagnostic> Diagnostics { get; } = [];
    public List<ProviderHealthSnapshot> Transitions { get; } = [];

    public ProviderHealthSnapshot Health => Runtime.Health.Get(Inner.Id);

    public static TranslationRequest Request(string text = "你好") => new(text, "en");
}
