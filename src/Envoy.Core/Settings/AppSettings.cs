using Envoy.Core.Translation;
using Envoy.Core.Translation.Providers;

namespace Envoy.Core.Settings;

/// <summary>Marks a string property that is encrypted (Windows DPAPI) when written to disk.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>
/// Marks a property holding player names or chat text. Diagnostics show only "[set]" or "[N entries]" (contract §7).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PersonalAttribute : Attribute;

/// <summary>Marks a property holding screen positions. Diagnostics show only the width × height (contract §7).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LocationAttribute : Attribute;

public sealed class AppSettings
{
    /// <summary>The schema version this build writes. Stays 1 until a real migration ships (contract §7).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The schema version read from disk. <see cref="SettingsStore.Save"/> always writes <see cref="CurrentVersion"/>.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The language you read and type in.</summary>
    public string MyLanguage { get; set; } = "en";

    /// <summary>Your in-game name, so your own messages aren't translated back to you.</summary>
    [Personal] public string MyPlayerName { get; set; } = "";

    /// <summary>"match-game" or a theme id.</summary>
    public string Theme { get; set; } = "match-game";

    /// <summary>"any" to auto-detect, or a game id from <see cref="Games.GameCatalog"/>.</summary>
    public string Game { get; set; } = Games.GameCatalog.Any;

    public bool StartWithWindows { get; set; }
    public bool StartCapturingAutomatically { get; set; } = true;
    public bool FirstRunCompleted { get; set; }

    /// <summary>
    /// Check GitHub Releases for a newer Envoy automatically (no data is sent besides the request itself). The master
    /// switch for every automatic update request; "Check for updates" on About always works (REQ-DIST-13).
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Update channel, background download and bookkeeping (spec 06). <see cref="CheckForUpdates"/> stays the master switch.</summary>
    public UpdateSettings Updates { get; set; } = new();

    /// <summary>Write a plain-text transcript of each session to the logs folder.</summary>
    public bool SaveTranscripts { get; set; }

    /// <summary>Players whose messages are hidden.</summary>
    [Personal] public List<string> MutedPlayers { get; set; } = [];

    public Pipeline.ChannelFilter Channels { get; set; } = Pipeline.ChannelFilter.Everything;

    public CaptureSettings Capture { get; set; } = new();

    /// <summary>How Envoy understands chat lines: wrapped lines, game notices, symbol cleanup, format (spec 04).</summary>
    public ChatSettings Chat { get; set; } = new();

    public TranslationSettings Translation { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public ComposeSettings Compose { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>The setup guide and the privacy acknowledgement (spec 07a).</summary>
    public OnboardingSettings Onboarding { get; set; } = new();

    /// <summary>Advisory tray balloons and the War Room notice list (spec 07a).</summary>
    public NotificationSettings Notifications { get; set; } = new();

    public UsageSnapshot? Usage { get; set; }
}

/// <summary>
/// The setup guide (REQ-OUXA-1, 2, 10) and the privacy acknowledgement (REQ-OUXA-47). Holds no names, chat text or
/// positions, so it needs no marker. UI thread only; background code reads the acknowledgement through AppHost's
/// volatile flag (contract §7).
/// </summary>
public sealed class OnboardingSettings
{
    /// <summary>The guide was finished (Finish on the last step).</summary>
    public bool Completed { get; set; }

    /// <summary>The guide was closed without finishing (Skip setup, Esc or the close button).</summary>
    public bool Skipped { get; set; }

    /// <summary>The <c>OnboardingStep</c> name to resume at; "" = the first step.</summary>
    public string LastStep { get; set; } = "";

    /// <summary>
    /// The <c>PrivacyText.Version</c> the user acknowledged; 0 = never. Translation is off below
    /// <c>PrivacyText.Version</c> (contract §8.1), so every existing settings file starts with translation off.
    /// </summary>
    public int PrivacyAcknowledgedVersion { get; set; }
}

/// <summary>
/// Advisory notices (spec 07a, contract §2.2). Turning balloons off keeps the War Room list. Holds no names, chat text
/// or positions: <see cref="Dismissed"/> holds notice keys such as <c>ocr-missing:ja,ko</c> and <c>provider:deepl</c>.
/// </summary>
public sealed class NotificationSettings
{
    /// <summary>Show advisory balloons in the notification area (the master switch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Balloon when a text recognition pack for a language Envoy reads is missing.</summary>
    public bool OcrLanguageMissing { get; set; } = true;

    /// <summary>Balloon when a translation provider keeps failing.</summary>
    public bool ProviderFailing { get; set; } = true;

    /// <summary>Balloon when an Envoy update is available.</summary>
    public bool UpdateAvailable { get; set; } = true;

    /// <summary>Notice keys the user chose "Don't remind me" for. A dismissed key is never shown or listed again.</summary>
    public List<string> Dismissed { get; set; } = [];
}

/// <summary>How Envoy updates itself (spec 06). Holds no names, chat text or positions, so it needs no marker.</summary>
public sealed class UpdateSettings
{
    /// <summary>Stable: releases only. Beta: GitHub prereleases too (REQ-DIST-18).</summary>
    public Envoy.Core.Updates.UpdateChannel Channel { get; set; } = Envoy.Core.Updates.UpdateChannel.Stable;

    /// <summary>Installed copies download updates in the background (never during a game).</summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>Version the user chose to skip ("" = none). Automatic checks ignore it (REQ-DIST-22).</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>When the last update check finished. Written only on the UI thread (via Dispatcher.BeginInvoke).</summary>
    public DateTimeOffset? LastCheckUtc { get; set; }

    /// <summary>Version that last ran; drives the one-time "Envoy was updated to …" notice (REQ-DIST-23).</summary>
    public string LastRunVersion { get; set; } = "";

    /// <summary>
    /// Version last announced in an update balloon ("" = none). Only stops a second balloon for the same version
    /// (contract §2.5); 06 fixed its meaning, 07a adds it as its only reader and writer (UI thread).
    /// </summary>
    public string LastAnnouncedVersion { get; set; } = "";
}

/// <summary>Logging and problem pop-ups (About › Troubleshooting).</summary>
public sealed class DiagnosticsSettings
{
    /// <summary>Show a non-activating pop-up when something goes wrong. Problems are logged either way.</summary>
    public bool ShowErrorToasts { get; set; } = true;

    /// <summary>Write Debug entries to the log as well.</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Write chat text and player names to the log instead of their length. Off unless a developer asks.</summary>
    public bool LogChatText { get; set; }
}

public sealed record ScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"{Width}×{Height} at ({X}, {Y})";
}

/// <summary>
/// The chat region as an offset inside a game window's client area (physical pixels), plus the client size when it was
/// chosen, so the region follows the window when it moves and scales when it is resized (REQ-CAP-1, 2).
/// </summary>
public sealed record WindowRelativeRegion(int OffsetX, int OffsetY, int Width, int Height, int ClientWidth, int ClientHeight)
{
    public bool IsEmpty => Width <= 0 || Height <= 0 || ClientWidth <= 0 || ClientHeight <= 0;
}

public sealed class CaptureSettings
{
    /// <summary>
    /// Chat region per game id (physical screen pixels). "any" is used when no game-specific one exists. Since spec 05
    /// this is the fallback used when no game window is found or no <see cref="WindowRegions"/> entry exists.
    /// </summary>
    [Location] public Dictionary<string, ScreenRegion> Regions { get; set; } = new();

    /// <summary>Chat region per game id, relative to that game's window (written when you select the area with the game open).</summary>
    [Location] public Dictionary<string, WindowRelativeRegion> WindowRegions { get; set; } = new();

    /// <summary>Check fast while chat is moving and slow down while it is quiet (REQ-CAP-14). Off = every <see cref="IntervalMs"/>.</summary>
    public bool AdaptivePolling { get; set; } = true;

    /// <summary>Fastest check with adaptive polling (clamped to 150–2000 ms).</summary>
    public int MinIntervalMs { get; set; } = 300;

    /// <summary>Slowest check with adaptive polling (clamped to MinIntervalMs–5000 ms).</summary>
    public int MaxIntervalMs { get; set; } = 1500;

    /// <summary>The fixed check interval when <see cref="AdaptivePolling"/> is off (clamped to 150–5000 ms).</summary>
    public int IntervalMs { get; set; } = 700;

    /// <summary>Give up on one text-recognition pass after this long (clamped to 1000–30000 ms). Settings file only.</summary>
    public int OcrTimeoutMs { get; set; } = 4000;

    /// <summary>Pause while another window covers the chat area (the screen copy would read that window instead).</summary>
    public bool PauseWhenCovered { get; set; } = true;

    /// <summary>Pause while neither the game nor Envoy is the active window.</summary>
    public bool PauseWhenNotForeground { get; set; }

    /// <summary>The capture method (<see cref="Envoy.Core.Capture.CaptureBackendIds"/>); unknown ids fall back to "gdi".</summary>
    public string Backend { get; set; } = Envoy.Core.Capture.CaptureBackendIds.Gdi;

    /// <summary>Chat text is small; scaling it up before OCR noticeably improves accuracy.</summary>
    public double Upscale { get; set; } = 2.0;

    public bool Grayscale { get; set; } = true;
    public bool Invert { get; set; }
    public double Contrast { get; set; } = 1.3;

    /// <summary>Extra OCR languages to try in addition to your own (Envoy codes). Empty = every OCR language installed.</summary>
    public List<string> OcrLanguages { get; set; } = ["zh-CN", "ko", "ja", "ru"];

    public int MinCharacters { get; set; } = 2;

    /// <summary>Only read chat while an Age game is running (saves CPU when you're not playing).</summary>
    public bool OnlyWhileGameRuns { get; set; } = true;
}

/// <summary>The Reading Chat page's "Understanding chat" card (spec 04).</summary>
public sealed class ChatSettings
{
    /// <summary>Join a message that wraps onto the next line into one message (REQ-CHAT-5).</summary>
    public bool StitchWrappedLines { get; set; } = true;

    /// <summary>Don't show game notices (REQ-CHAT-27). Notices are never translated either way.</summary>
    public bool HideSystemLines { get; set; } = true;

    /// <summary>One notice pattern per entry; see docs/chat-formats.md. May contain player names.</summary>
    [Personal] public List<string> SystemLinePatterns { get; set; } = [];

    /// <summary>Treat a line with no ':' that starts with a known player's name as a notice (REQ-CHAT-28). Off by default.</summary>
    public bool DetectSystemLinesByPlayerName { get; set; }

    /// <summary>Remove stray symbols that OCR reads from rank and civilisation icons (REQ-CHAT-39).</summary>
    public bool CleanSymbols { get; set; } = true;

    /// <summary>"auto" (match the game) or a chat format profile id from chat-formats.json (REQ-CHAT-33).</summary>
    public string FormatProfile { get; set; } = Envoy.Core.Chat.ChatFormatCatalog.Auto;
}

public sealed class TranslationSettings
{
    public string Primary { get; set; } = ProviderCatalog.GoogleFree;
    public List<string> Fallbacks { get; set; } = [ProviderCatalog.EdgeFree];

    /// <summary>Don't translate messages already in your language.</summary>
    public bool SkipMyLanguage { get; set; } = true;
    public bool UseSlangGlossary { get; set; } = true;

    /// <summary>Daily character cap for paid providers (0 = no cap). ~50k characters ≈ 1,500 chat lines.</summary>
    public int DailyCharacterLimit { get; set; } = 50_000;

    [Secret] public string DeepLKey { get; set; } = "";
    [Secret] public string ClaudeKey { get; set; } = "";
    public string ClaudeModel { get; set; } = ClaudeTranslator.DefaultModel;
    public string LibreTranslateUrl { get; set; } = "http://localhost:5000";
    [Secret] public string LibreTranslateKey { get; set; } = "";
    [Secret] public string AwsAccessKeyId { get; set; } = "";
    [Secret] public string AwsSecretAccessKey { get; set; } = "";
    public string AwsRegion { get; set; } = "us-east-1";

    /// <summary>Give up on a chat line after this many seconds, across retries and fallbacks (clamped to 3–60).</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Remembered translations kept in memory (and on disk when <see cref="RememberTranslations"/> is on); clamped to 100–20000.</summary>
    public int CacheMaxEntries { get; set; } = 2000;

    /// <summary>Save translations between sessions in translation-cache.json (off by default, like transcripts).</summary>
    public bool RememberTranslations { get; set; }
}

public enum OverlayAnchor
{
    Free,
    AboveChat,
    BelowChat,
    RightOfChat,
}

public sealed class OverlaySettings
{
    public bool Visible { get; set; } = true;
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.AboveChat;
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 400;
    public double Width { get; set; } = 560;
    public double MaxHeight { get; set; } = 320;
    public double Opacity { get; set; } = 0.92;
    public double BackgroundOpacity { get; set; } = 0.78;
    public double FontSize { get; set; } = 15;
    public string FontFamily { get; set; } = "";
    public int MaxMessages { get; set; } = 8;
    public int MessageLifetimeSeconds { get; set; } = 25;
    public bool ShowOriginal { get; set; } = true;
    public bool ShowSender { get; set; } = true;
    public bool ShowLanguageBadge { get; set; } = true;
    public bool ShowProvider { get; set; }
    public bool PlaySound { get; set; }

    /// <summary>Read translated messages aloud with a Windows voice.</summary>
    public bool SpeakMessages { get; set; }
    public bool SpeakSenderName { get; set; } = true;
    public string SpeechVoiceId { get; set; } = "";
    public double SpeechVolume { get; set; } = 0.8;
    public double SpeechRate { get; set; } = 1.1;
}

public enum SendMode
{
    /// <summary>Open chat in the game, type the translation and press Enter.</summary>
    TypeAndSend,
    /// <summary>Open chat and type it, but let you press Enter yourself.</summary>
    TypeOnly,
    /// <summary>Just copy the translation to the clipboard.</summary>
    Clipboard,
}

public enum TypingMethod
{
    /// <summary>Send each character as a Unicode keystroke (works with any keyboard layout).</summary>
    UnicodeKeystrokes,
    /// <summary>Put the text on the clipboard and press Ctrl+V.</summary>
    Paste,
}

public sealed class ComposeSettings
{
    public TypingMethod TypingMethod { get; set; } = TypingMethod.UnicodeKeystrokes;

    /// <summary>"auto" = the language of the last message you received.</summary>
    public string DefaultTarget { get; set; } = "auto";
    public SendMode SendMode { get; set; } = SendMode.TypeAndSend;

    /// <summary>Key that opens chat in the game. Enter in every AoE title by default.</summary>
    public string OpenChatKey { get; set; } = "Enter";
    public int KeystrokeDelayMs { get; set; } = 6;
    public int FocusDelayMs { get; set; } = 120;

    /// <summary>Append the original after the translation, e.g. "你好 (hello)".</summary>
    public bool AppendOriginal { get; set; }
}

public sealed class HotkeySettings
{
    public string Compose { get; set; } = "Ctrl+Shift+T";
    public string ToggleOverlay { get; set; } = "Ctrl+Shift+O";
    public string ToggleCapture { get; set; } = "Ctrl+Shift+P";
    public string SelectRegion { get; set; } = "Ctrl+Shift+R";
    public string OverlayEditMode { get; set; } = "Ctrl+Shift+E";
    public string ScanAndTranslate { get; set; } = "Ctrl+Shift+Q";
}
