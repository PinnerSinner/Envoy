using Envoy.Core.Chat;
using Envoy.Core.Diagnostics;
using Envoy.Core.Languages;
using Envoy.Core.Ocr;
using Envoy.Core.Settings;
using Envoy.Core.Translation;

namespace Envoy.Core.Pipeline;

/// <summary>
/// An immutable snapshot of everything the pipeline reads (contract §7, REQ-CHAT-20). AppHost builds one with
/// <see cref="From"/> in <c>ApplySettings</c> and publishes it through <see cref="ChatPipeline.Options"/>.
/// </summary>
public sealed record ChatPipelineOptions
{
    public string MyLanguage { get; init; } = "en";
    public string MyPlayerName { get; init; } = "";
    public bool SkipMyLanguage { get; init; } = true;
    public bool UseSlangGlossary { get; init; } = true;
    public int MinCharacters { get; init; } = 2;

    /// <summary>Players whose messages are dropped before translation.</summary>
    public IReadOnlyList<string> MutedPlayers { get; init; } = [];

    public ChannelFilter Channels { get; init; } = ChannelFilter.Everything;

    /// <summary>Join lines that wrap onto the next line into one message (REQ-CHAT-5).</summary>
    public bool StitchWrappedLines { get; init; } = true;

    /// <summary>Don't show game notices (REQ-CHAT-27).</summary>
    public bool HideSystemLines { get; init; } = true;

    /// <summary>The user's notice patterns (REQ-CHAT-23).</summary>
    public IReadOnlyList<string> SystemLinePatterns { get; init; } = [];

    /// <summary>Treat a sender-less line that starts with a known player's name as a notice (REQ-CHAT-28).</summary>
    public bool DetectSystemLinesByPlayerName { get; init; }

    /// <summary>Remove stray symbols that OCR reads from icons (REQ-CHAT-36 to 39).</summary>
    public bool CleanSymbols { get; init; } = true;

    /// <summary>The chat format; null means the built-in generic one.</summary>
    public ChatFormatProfile? Format { get; init; }

    /// <summary>A snapshot of <paramref name="settings"/>: every collection is copied (contract §7).</summary>
    public static ChatPipelineOptions From(AppSettings settings, ChatFormatProfile? format)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ChatPipelineOptions
        {
            MyLanguage = settings.MyLanguage,
            MyPlayerName = settings.MyPlayerName,
            SkipMyLanguage = settings.Translation.SkipMyLanguage,
            UseSlangGlossary = settings.Translation.UseSlangGlossary,
            MinCharacters = settings.Capture.MinCharacters,
            MutedPlayers = settings.MutedPlayers.ToArray(),
            Channels = settings.Channels,
            StitchWrappedLines = settings.Chat.StitchWrappedLines,
            HideSystemLines = settings.Chat.HideSystemLines,
            SystemLinePatterns = settings.Chat.SystemLinePatterns.ToArray(),
            DetectSystemLinesByPlayerName = settings.Chat.DetectSystemLinesByPlayerName,
            CleanSymbols = settings.Chat.CleanSymbols,
            Format = format,
        };
    }
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
/// Turns one capture's OCR lines into translated messages: analyse (clean, notices, stitching) → dedupe → filter →
/// glossary → translate. Platform independent so it can be unit tested; the Windows app feeds it lines and renders the
/// events. Owns the pending-message lifecycle (contract §2.6): every message emitted as Pending reaches exactly one
/// terminal state and raises <see cref="MessageUpdated"/> exactly once.
/// </summary>
public sealed class ChatPipeline : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly ILog _log;
    private readonly ChatDeduplicator _dedup;
    private readonly object _lifetimeGate = new();
    private CancellationTokenSource _lifetime = new();
    private State _state;
    private ITranslator _translator;
    private int _inFlight;
    private bool _disposed;
    private string? _lastAnalysisError;

    public ChatPipeline(ITranslator translator, ChatPipelineOptions options, TimeProvider? clock = null, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLog.Instance;
        _dedup = new ChatDeduplicator(clock);
        _translator = translator;
        Players = new PlayerDirectory(clock);
        _state = BuildState(options, previous: null);
    }

    /// <summary>
    /// The options snapshot. The getter is a volatile read; the setter (UI thread) compiles the notice patterns once
    /// and publishes the new state with a volatile write. Each capture reads the state once.
    /// </summary>
    public ChatPipelineOptions Options
    {
        get => Volatile.Read(ref _state).Options;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _state, BuildState(value, Volatile.Read(ref _state)));
        }
    }

    /// <summary>The user's notice patterns that could not be used, for the Reading Chat card.</summary>
    public IReadOnlyList<PatternError> SystemPatternErrors => Volatile.Read(ref _state).PatternErrors;

    public ITranslator Translator
    {
        get => Volatile.Read(ref _translator);
        set => Volatile.Write(ref _translator, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Per-player language memory for this session.</summary>
    public PlayerDirectory Players { get; }

    /// <summary>Raised when a new message is first seen (status Pending or final).</summary>
    public event Action<IncomingMessage>? MessageReceived;

    /// <summary>Raised exactly once when a Pending message reaches its terminal state.</summary>
    public event Action<IncomingMessage>? MessageUpdated;

    /// <summary>Language of the most recent message from another player, used as the default reply language.</summary>
    public string? LastOpponentLanguage { get; private set; }

    /// <summary>The exception type of the last capture whose analysis failed and fell back to line mode (REQ-CHAT-45).</summary>
    public string? LastAnalysisError => Volatile.Read(ref _lastAnalysisError);

    /// <summary>Test hook: runs before each analysis, so a test can make analysis throw (AC-CHAT-23).</summary>
    internal Action? BeforeAnalyze { get; set; }

    /// <summary>
    /// Best language to reply in: the language of whoever spoke last if it isn't yours (so in a 2v2 you answer
    /// the Korean player in Korean right after they talk), otherwise the last foreign language seen.
    /// </summary>
    public string? SuggestedReplyLanguage =>
        Players.LastSpeaker?.Language is { } lang && !LanguageCatalog.SameLanguage(lang, Options.MyLanguage)
            ? lang
            : LastOpponentLanguage;

    /// <summary>Forgets seen messages and cancels in-flight translations (region change).</summary>
    public void Reset()
    {
        _dedup.Clear();
        CancelLifetime("reset", replace: true);
    }

    /// <summary>Forget everything about the current match (players, languages, seen lines) and cancel translations.</summary>
    public void NewMatch()
    {
        _dedup.Clear();
        Players.Clear();
        LastOpponentLanguage = null;
        CancelLifetime("newMatch", replace: true);
    }

    /// <summary>Cancels in-flight translations; later captures return nothing.</summary>
    public void Dispose() => CancelLifetime("dispose", replace: false);

    /// <summary>
    /// Feeds one capture's merged lines (contract §2.7). <paramref name="ct"/> cancels only the analysis, never a
    /// translation: translations run under the pipeline's own lifetime (contract §2.6).
    /// </summary>
    public async Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IReadOnlyList<OcrLine> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (IsDisposed) return [];
        var state = Volatile.Read(ref _state);
        var options = state.Options;

        ct.ThrowIfCancellationRequested();
        var entries = AnalyzeSafely(state, lines, logFailure: true);
        entries = entries.Select(e => e.Kind == ChatLineKind.Chat && e.Sender is { } s && Players.Canonicalize(s) is { } c && c != s
            ? e with { Sender = c }
            : e).ToList();
        ct.ThrowIfCancellationRequested();

        var accepted = _dedup.AcceptNew(entries); // every entry, so filtered ones are remembered too (REQ-CHAT-18)

        CancellationToken lifetime;
        lock (_lifetimeGate)
        {
            if (_disposed) return [];
            lifetime = _lifetime.Token;
        }

        var fresh = new List<IncomingMessage>();
        foreach (var chat in accepted)
        {
            if (!ShouldEmit(chat, options)) continue;
            var message = new IncomingMessage(chat, _clock.GetUtcNow());
            fresh.Add(message);
            Classify(message, options);
            MessageReceived?.Invoke(message);
        }

        // Translate concurrently so a burst of chat doesn't queue up behind one slow request.
        var pending = fresh.Where(m => m.Status == IncomingStatus.Pending).ToList();
        if (pending.Count > 0)
            await Task.WhenAll(pending.Select(m => TranslateAsync(m, options, lifetime))).ConfigureAwait(false);
        return fresh;
    }

    /// <summary>Feeds one capture's lines without geometry (stitching then joins adjacent lines, REQ-CHAT-10).</summary>
    public Task<IReadOnlyList<IncomingMessage>> ProcessAsync(IEnumerable<string> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return ProcessAsync(OcrLine.FromTexts(lines), ct);
    }

    /// <summary>Analysis only: no dedupe, no events, no translation, no player-directory writes. Thread-safe.</summary>
    public IReadOnlyList<ChatMessage> Analyze(IReadOnlyList<OcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return AnalyzeSafely(Volatile.Read(ref _state), lines, logFailure: false);
    }

    /// <summary>"Understood as" text for the Live view (contract §2.8); no side effects.</summary>
    public string Describe(IReadOnlyList<OcrLine> lines) => ChatUnderstandingText.Describe(Analyze(lines));

    private bool IsDisposed
    {
        get { lock (_lifetimeGate) return _disposed; }
    }

    private IReadOnlyList<ChatMessage> AnalyzeSafely(State state, IReadOnlyList<OcrLine> lines, bool logFailure)
    {
        try
        {
            BeforeAnalyze?.Invoke();
            return ChatCaptureAnalyzer.Analyze(lines, ContextFor(state));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // REQ-CHAT-45: one line per message for this capture. The exception message may quote chat, so only its
            // type and stack are logged.
            if (logFailure)
            {
                Volatile.Write(ref _lastAnalysisError, ex.GetType().Name);
                _log.Warn("Chat analysis failed; reading this capture line by line", null, LogField.Of("error", ex.GetType().Name),
                    LogField.Of("stack", ex.StackTrace));
            }
            return lines.Select(l => ChatLineParser.Parse(l.Text ?? "")).Where(m => m.Raw.Length > 0).ToList();
        }
    }

    private ChatContext ContextFor(State state)
    {
        if (!state.Options.DetectSystemLinesByPlayerName) return state.Context;
        var players = Players.KnownNames.ToList();
        if (!string.IsNullOrWhiteSpace(state.Options.MyPlayerName)) players.Add(state.Options.MyPlayerName);
        return state.Context with { KnownPlayers = players };
    }

    private State BuildState(ChatPipelineOptions options, State? previous)
    {
        var profile = options.Format ?? ChatFormatProfile.Generic;
        var user = options.SystemLinePatterns;
        var all = user.Concat(profile.SystemPatternsFor(options.MyLanguage)).ToList();
        var patterns = Chat.SystemLinePatterns.Compile(all, out var allErrors);
        var errors = allErrors.Where(e => e.Index < user.Count).ToList(); // the profile's own were reported by the catalog

        if (errors.Count > 0 && (previous is null || !previous.Options.SystemLinePatterns.SequenceEqual(user, StringComparer.Ordinal)))
        {
            foreach (var error in errors)
            {
                _log.Info("Notice pattern ignored", LogField.Of("count", errors.Count), LogField.Of("line", error.Index + 1),
                    LogField.Sensitive("pattern", error.Pattern), LogField.Sensitive("error", error.Message));
            }
        }

        var context = new ChatContext(patterns, [], options.StitchWrappedLines && profile.StitchEnabled, profile.Stitching,
            options.CleanSymbols, options.DetectSystemLinesByPlayerName, profile);
        return new State(options, context, errors);
    }

    private static bool ShouldEmit(ChatMessage chat, ChatPipelineOptions options)
    {
        if (chat.Kind == ChatLineKind.System)
            return !options.HideSystemLines && TextSimilarity.TextElementCount(chat.Text) >= options.MinCharacters;
        return IsWorthShowing(chat, options);
    }

    private static bool IsWorthShowing(ChatMessage chat, ChatPipelineOptions options)
    {
        if (TextSimilarity.TextElementCount(chat.Text) < options.MinCharacters) return false;
        // Lines that are only punctuation/digits are almost always OCR noise from the game UI.
        if (!chat.Text.Any(char.IsLetter) && SlangGlossary.Match(chat.Text) is null) return false;
        if (IsMe(chat.Sender, options)) return false;
        if (MuteList.IsMuted(chat.Sender, options.MutedPlayers)) return false;
        return options.Channels switch
        {
            ChannelFilter.TeamOnly => chat.Channel == "Team",
            ChannelFilter.ExceptTeam => chat.Channel != "Team",
            _ => true,
        };
    }

    private static bool IsMe(string? sender, ChatPipelineOptions options)
    {
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(options.MyPlayerName)) return false;
        var a = TextSimilarity.Normalize(sender);
        var b = TextSimilarity.Normalize(options.MyPlayerName);
        return a.Length > 0 && (a == b || a.EndsWith(b, StringComparison.Ordinal) || TextSimilarity.Similarity(a, b) >= 0.85);
    }

    private void Classify(IncomingMessage message, ChatPipelineOptions options)
    {
        // Notices are never translated and teach nothing about players (REQ-CHAT-26, 27).
        if (message.Chat.Kind == ChatLineKind.System)
        {
            message.SourceLanguage = options.MyLanguage;
            message.Status = IncomingStatus.Untranslated;
            return;
        }

        var text = message.Chat.Text;
        var guessed = ScriptDetector.GuessLanguage(text);

        if (options.UseSlangGlossary && SlangGlossary.Match(text) is { } slang)
        {
            message.SourceLanguage = slang.Language;
            message.FromGlossary = true;
            NoteOpponentLanguage(slang.Language, message.Chat.Sender, options);
            if (options.MyLanguage == "en")
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

        if (guessed is not null) NoteOpponentLanguage(guessed, message.Chat.Sender, options);
        else Players.Observe(message.Chat.Sender, null);

        // Script check settles "already in my language" offline for non-Latin languages.
        if (options.SkipMyLanguage && guessed is not null && LanguageCatalog.SameLanguage(guessed, options.MyLanguage))
        {
            message.SourceLanguage = guessed;
            message.Status = IncomingStatus.Untranslated;
        }
        // Latin-script text could be any of a dozen languages, so it goes to the provider, which detects it
        // and reports back when it was already in our language.
    }

    /// <summary>
    /// Ends in exactly one place, which sets the terminal state and raises <see cref="MessageUpdated"/> once
    /// (REQ-CHAT-47). Any cancellation ends as Failed("cancelled") with <see cref="IncomingMessage.WasCancelled"/>.
    /// </summary>
    private async Task TranslateAsync(IncomingMessage message, ChatPipelineOptions options, CancellationToken lifetime)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            lifetime.ThrowIfCancellationRequested();
            if (message.GlossaryMeaning is { } meaning)
            {
                var explained = await Translator.TranslateAsync(new TranslationRequest(meaning, options.MyLanguage, "en"), lifetime).ConfigureAwait(false);
                message.Translation = explained.Text;
                message.ProviderId = "glossary";
                message.Status = IncomingStatus.Translated;
            }
            else
            {
                var result = await Translator.TranslateAsync(new TranslationRequest(message.Chat.Text, options.MyLanguage), lifetime).ConfigureAwait(false);
                message.SourceLanguage = result.DetectedSourceLanguage ?? message.SourceLanguage;
                message.ProviderId = result.ProviderId;

                var unchanged = string.Equals(result.Text.Trim(), message.Chat.Text.Trim(), StringComparison.OrdinalIgnoreCase);
                if (LanguageCatalog.SameLanguage(message.SourceLanguage, options.MyLanguage) || unchanged)
                {
                    message.Status = IncomingStatus.Untranslated;
                    Players.Observe(message.Chat.Sender, message.SourceLanguage);
                }
                else
                {
                    message.Translation = result.Text;
                    message.Status = IncomingStatus.Translated;
                    if (message.SourceLanguage is not null) NoteOpponentLanguage(message.SourceLanguage, message.Chat.Sender, options);
                }
            }
        }
        catch (OperationCanceledException)
        {
            message.Error = PipelineText.Cancelled;
            message.WasCancelled = true;
            message.Status = IncomingStatus.Failed;
        }
        catch (Exception ex)
        {
            message.Error = ex.Message;
            message.Status = IncomingStatus.Failed;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
        MessageUpdated?.Invoke(message);
    }

    private void CancelLifetime(string reason, bool replace)
    {
        CancellationTokenSource old;
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            old = _lifetime;
            if (replace) _lifetime = new CancellationTokenSource();
            else _disposed = true;
        }
        var count = Volatile.Read(ref _inFlight);
        if (count > 0) _log.Debug("Translations cancelled", LogField.Of("reason", reason), LogField.Of("count", count));
        old.Cancel();
        // A replaced source is only cancelled: a translation may still link to its token. It holds no timer, so the GC
        // takes it; the last one is disposed with the pipeline.
        if (!replace) old.Dispose();
    }

    private void NoteOpponentLanguage(string code, string? sender, ChatPipelineOptions options)
    {
        Players.Observe(sender, code);
        if (!LanguageCatalog.SameLanguage(code, options.MyLanguage)) LastOpponentLanguage = LanguageCatalog.Normalize(code);
    }

    /// <summary>What one capture reads: the options, the analyzer context built from them, and the pattern errors.</summary>
    private sealed record State(ChatPipelineOptions Options, ChatContext Context, IReadOnlyList<PatternError> PatternErrors);
}
