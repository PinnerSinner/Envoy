using Envoy.Core.Chat;
using Envoy.Core.Pipeline;
using Xunit;

namespace Envoy.Core.Tests.Pipeline;

public class OverlayCopyTextTests
{
    private static IncomingMessage Message(string? sender, string text, IncomingStatus status, string? translation = null) =>
        new(new ChatMessage(text, "All", sender, text), DateTimeOffset.UtcNow) { Status = status, Translation = translation };

    [Fact]
    public void Translated_includes_sender_translation_and_original() =>
        Assert.Equal("Minh: good game\ngg wp bạn", OverlayCopyText.Format(Message("Minh", "gg wp bạn", IncomingStatus.Translated, "good game")));

    [Fact]
    public void No_sender_no_prefix()
    {
        Assert.Equal("good game\ngg wp bạn", OverlayCopyText.Format(Message(null, "gg wp bạn", IncomingStatus.Translated, "good game")));
        Assert.Equal("hello", OverlayCopyText.Format(Message("", "hello", IncomingStatus.Untranslated)));
    }

    [Fact]
    public void Original_omitted_when_identical() =>
        Assert.Equal("Bob: gg", OverlayCopyText.Format(Message("Bob", "gg", IncomingStatus.Translated, "gg")));

    [Theory]
    [InlineData(IncomingStatus.Pending)]
    [InlineData(IncomingStatus.Failed)]
    [InlineData(IncomingStatus.Untranslated)]
    public void Pending_failed_and_skipped_use_original(IncomingStatus status) =>
        Assert.Equal("Bob: привет", OverlayCopyText.Format(Message("Bob", "привет", status, status == IncomingStatus.Failed ? null : "hi")));
}
