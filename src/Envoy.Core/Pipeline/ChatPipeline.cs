using Envoy.Core.Chat;
using Envoy.Core.Languages;
using Envoy.Core.Translation;

namespace Envoy.Core.Pipeline;

public sealed class ChatPipelineOptions
{
    public string MyLanguage { get; set; } = "en";
    public string MyPlayerName { get; set; } = "";
    public bool SkipMyLanguage { get; set; } = true;
    public bool UseSlangGlossary { get; set; } = true;
    public int MinCharacters { get; set; } = 2;

    /// <summary>Players whose messages are dropped before translation.</summary>
    public List<string> MutedPlayers { get; set; } = [];

    public ChannelFilter Channels { get; set; } = ChannelFilter.Everything;
}

public enum ChannelFilter
{
    Everything,
    /// <summary>Only messages the game marked as team/ally chat.</summary>
    TeamOnly,
    /// <summary>Hide team chat (e.g. when you only need the opponents translated).</summary>
    ExceptTeam,
}

/// <summary>
/// Turns raw OCR lines into translated messages: parse → drop own/duplicate/noise lines → glossary → translate.
/// Platform independent so it can be unit tested; the Windows app feeds it lines and renders the events.
/// </summary>
public sealed class ChatPipeline(ITranslator translator, ChatPipelineOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ChatDeduplicator _dedup = new(clock);

    public ChatPipelineOptions Options { get; } = options;
    public ITranslator Translator { get; set; } = translator;

    /// <summary>Per-player language memory for this session.</summary>
    public PlayerDirectory Players { get; } = new(clock);

    /// <summary>Raised when a new message is first seen (status Pending or final).</summary>
    public event Action<IncomingMessage>? MessageReceived;

    /// <summary>Raised when a pending message's translation completes or fails.</summary>
    public event Action<IncomingMessage>? MessageUpdated;

    /// <summary>Language of the most recent message from another player, used as the default reply language.</summary>
    public string? LastOpponentLanguage { get; private set; }

    /// <summary>
    /// Best language to reply in: the language of whoever spoke last if it isn't yours (so in a 2v2 you answer
    /// the Korean player in Korean right after they talk), otherwise the last foreign language seen.
    /// </summary>
    public string? SuggestedReplyLanguage =>
        Players.LastSpeaker?.Language is { } lang && !LanguageCatalog.SameLanguage(lang, Options.MyLanguage)
            ? lang
            : LastOpponentLanguage;

    public void Reset() => _dedup.Clear();

    /// <summary>Forget everything about the current match (players, languages, seen lines).</summary>
    public void NewMatch()
    {
        _dedup.Clear();
        Players.Clear();
        LastOpponentLanguage = null;
    }

    /// <summary>Feeds one OCR pass. Returns the messages that were new.</summary>
    public async Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IEnumerable<string> lines, CancellationToken ct = default)
    {
        var fresh = new List<IncomingMessage>();
        foreach (var line in lines)
        {
            var chat = ChatLineParser.Parse(line);
            if (!IsWorthShowing(chat)) continue;
            if (!_dedup.TryAccept(chat.Raw)) continue;

            var message = new IncomingMessage(chat, _clock.GetUtcNow());
            fresh.Add(message);
            Classify(message);
            MessageReceived?.Invoke(message);
        }

        // Translate concurrently so a burst of chat doesn't queue up behind one slow request.
        await Task.WhenAll(fresh.Where(m => m.Status == IncomingStatus.Pending).Select(m => TranslateAsync(m, ct))).ConfigureAwait(false);
        return fresh;
    }

    private bool IsWorthShowing(ChatMessage chat)
    {
        if (TextSimilarity.TextElementCount(chat.Text) < Options.MinCharacters) return false;
        // Lines that are only punctuation/digits are almost always OCR noise from the game UI.
        if (!chat.Text.Any(char.IsLetter) && SlangGlossary.Match(chat.Text) is null) return false;
        if (IsMe(chat.Sender)) return false;
        if (MuteList.IsMuted(chat.Sender, Options.MutedPlayers)) return false;
        return Options.Channels switch
        {
            ChannelFilter.TeamOnly => chat.Channel == "Team",
            ChannelFilter.ExceptTeam => chat.Channel != "Team",
            _ => true,
        };
    }

    private bool IsMe(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(Options.MyPlayerName)) return false;
        var a = TextSimilarity.Normalize(sender);
        var b = TextSimilarity.Normalize(Options.MyPlayerName);
        return a.Length > 0 && (a == b || a.EndsWith(b, StringComparison.Ordinal) || TextSimilarity.Similarity(a, b) >= 0.85);
    }

    private void Classify(IncomingMessage message)
    {
        var text = message.Chat.Text;
        var guessed = ScriptDetector.GuessLanguage(text);

        if (Options.UseSlangGlossary && SlangGlossary.Match(text) is { } slang)
        {
            message.SourceLanguage = slang.Language;
            message.FromGlossary = true;
            NoteOpponentLanguage(slang.Language, message.Chat.Sender);
            if (Options.MyLanguage == "en")
            {
                message.Translation = slang.Meaning;
                message.ProviderId = "glossary";
                message.Status = IncomingStatus.Translated;
            }
            else
            {
                // Meanings are written in English; translate the meaning (not the slang) for other readers.
                message.GlossaryMeaning = slang.Meaning;
            }
            return;
        }

        if (guessed is not null) NoteOpponentLanguage(guessed, message.Chat.Sender);
        else Players.Observe(message.Chat.Sender, null);

        // Script check settles "already in my language" offline for non-Latin languages.
        if (Options.SkipMyLanguage && guessed is not null && LanguageCatalog.SameLanguage(guessed, Options.MyLanguage))
        {
            message.SourceLanguage = guessed;
            message.Status = IncomingStatus.Untranslated;
        }
        // Latin-script text could be any of a dozen languages, so it goes to the provider, which detects it
        // and reports back when it was already in our language.
    }

    private async Task TranslateAsync(IncomingMessage message, CancellationToken ct)
    {
        try
        {
            if (message.GlossaryMeaning is { } meaning)
            {
                var explained = await Translator.TranslateAsync(new TranslationRequest(meaning, Options.MyLanguage, "en"), ct).ConfigureAwait(false);
                message.Translation = explained.Text;
                message.ProviderId = "glossary";
                message.Status = IncomingStatus.Translated;
                MessageUpdated?.Invoke(message);
                return;
            }

            var result = await Translator.TranslateAsync(new TranslationRequest(message.Chat.Text, Options.MyLanguage), ct).ConfigureAwait(false);
            message.SourceLanguage = result.DetectedSourceLanguage ?? message.SourceLanguage;
            message.ProviderId = result.ProviderId;

            var unchanged = string.Equals(result.Text.Trim(), message.Chat.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            if (LanguageCatalog.SameLanguage(message.SourceLanguage, Options.MyLanguage) || unchanged)
            {
                message.Status = IncomingStatus.Untranslated;
                Players.Observe(message.Chat.Sender, message.SourceLanguage);
            }
            else
            {
                message.Translation = result.Text;
                message.Status = IncomingStatus.Translated;
                if (message.SourceLanguage is not null) NoteOpponentLanguage(message.SourceLanguage, message.Chat.Sender);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            message.Error = ex.Message;
            message.Status = IncomingStatus.Failed;
        }
        MessageUpdated?.Invoke(message);
    }

    private void NoteOpponentLanguage(string code, string? sender)
    {
        Players.Observe(sender, code);
        if (!LanguageCatalog.SameLanguage(code, Options.MyLanguage)) LastOpponentLanguage = LanguageCatalog.Normalize(code);
    }
}
