using System.Collections.Concurrent;
using System.Speech.Synthesis;

namespace RacingHelper.App.Services;

/// <summary>Speaks race-engineer messages on a background thread using the Windows speech voices.</summary>
public sealed class Voice : IDisposable
{
    readonly Func<AppSettings> _settings;
    readonly BlockingCollection<(string text, int prio, DateTime at)> _queue = new();
    readonly Thread _thread;
    SpeechSynthesizer? _synth;
    string _voice = "";

    public Voice(Func<AppSettings> settings)
    {
        _settings = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "voice" };
        _thread.Start();
    }

    public IReadOnlyList<string> Voices()
    {
        try
        {
            using var s = new SpeechSynthesizer();
            return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public void Say(string text, int priority = 1)
    {
        if (priority >= 3)
        {
            // urgent: drop anything queued and interrupt
            while (_queue.TryTake(out _)) { }
            try { _synth?.SpeakAsyncCancelAll(); } catch { }
        }
        _queue.Add((text, priority, DateTime.Now));
    }

    void Run()
    {
        try { _synth = new SpeechSynthesizer(); _synth.SetOutputToDefaultAudioDevice(); }
        catch { return; }
        foreach (var (text, prio, at) in _queue.GetConsumingEnumerable())
        {
            if ((DateTime.Now - at).TotalSeconds > (prio >= 2 ? 15 : 8)) continue; // stale
            try
            {
                var s = _settings();
                if (s.VoiceName != _voice)
                {
                    _voice = s.VoiceName;
                    if (!string.IsNullOrEmpty(_voice)) _synth.SelectVoice(_voice);
                }
                _synth.Rate = Math.Clamp(s.VoiceRate, -10, 10);
                _synth.Volume = Math.Clamp(s.VoiceVolume, 0, 100);
                _synth.Speak(text);
            }
            catch { /* voice missing / device busy */ }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _synth?.SpeakAsyncCancelAll(); _synth?.Dispose(); } catch { }
    }
}
