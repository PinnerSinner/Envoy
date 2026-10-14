using Envoy.Core.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Envoy.App.Services;

public sealed record VoiceOption(string Id, string Name);

/// <summary>
/// Reads translated messages aloud with the Windows built-in voices (offline, free). Messages are queued so a burst
/// of chat is spoken in order, and anything older than a few seconds is skipped to stay current.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _player = new() { AudioCategory = MediaPlayerAudioCategory.Speech };
    private readonly Queue<(string Text, DateTime Queued)> _queue = new();
    private readonly object _gate = new();
    private readonly ILog _log;
    private bool _speaking;

    public SpeechService(ILog? log = null)
    {
        _log = log ?? NullLog.Instance;
        _player.MediaEnded += (_, _) => Next();
        _player.MediaFailed += (_, _) => Next();
    }

    public static IReadOnlyList<VoiceOption> Voices
    {
        get
        {
            try { return SpeechSynthesizer.AllVoices.Select(v => new VoiceOption(v.Id, $"{v.DisplayName} ({v.Language})")).ToList(); }
            catch (Exception) { return []; }
        }
    }

    public string VoiceId
    {
        set
        {
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == value);
            if (voice is not null) _synth.Voice = voice;
        }
    }

    public double Volume
    {
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public double Rate
    {
        set => _synth.Options.SpeakingRate = Math.Clamp(value, 0.5, 3.0);
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            _queue.Enqueue((text, DateTime.UtcNow));
            while (_queue.Count > 5) _queue.Dequeue();
            if (_speaking) return;
            _speaking = true;
        }
        SpeakNextAsync().Forget(_log, "speech");
    }

    public void Stop()
    {
        lock (_gate)
        {
            _queue.Clear();
            _speaking = false;
        }
        _player.Pause();
    }

    private void Next() => SpeakNextAsync().Forget(_log, "speech");

    private async Task SpeakNextAsync()
    {
        string? text = null;
        lock (_gate)
        {
            while (_queue.Count > 0)
            {
                var (t, queued) = _queue.Dequeue();
                if (DateTime.UtcNow - queued < TimeSpan.FromSeconds(15)) { text = t; break; }
            }
            if (text is null)
            {
                _speaking = false;
                return;
            }
        }

        try
        {
            var stream = await _synth.SynthesizeTextToStreamAsync(text);
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
        }
        catch (Exception ex)
        {
            // No voices installed or audio device missing: drop this one and carry on.
            _log.Warn("Speaking a message failed", ex);
            Next();
        }
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
        _synth.Dispose();
    }
}
