using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using Yappest.Core;

namespace Yappest.Audio;

/// <summary>
/// mic -> (gain, mute, voice changer) -> [Yappest Input cable] (+ soundboard)
///                                    -> [your headphones] (hear myself + soundboard)
///                                    -> [clip buffer]
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int Rate = 48000;
    public static readonly WaveFormat StereoFormat = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

    public static AudioEngine Instance { get; } = new();

    // ---- state shown in UI
    public volatile float InputLevel;       // 0..1 peak of the raw mic
    public volatile float OutputLevel;      // 0..1 peak after effects
    public bool IsRunning { get; private set; }
    public bool CableFound { get; private set; }
    public string? LastError { get; private set; }
    public event Action? StatusChanged;
    public event Action<string, bool>? SoundStateChanged;       // soundId, playing

    // ---- pipeline
    WasapiCapture? _capture;
    WasapiOut? _cableOut, _monitorOut;
    MMDevice? _micDev, _outDev, _cableDev;
    readonly FloatRing _cableRing = new(Rate);                  // 1 s cap
    readonly FloatRing _monitorRing = new(Rate);
    public readonly FloatRing ClipMicRing = new(Rate * 60);     // 60 s mono, post effects
    readonly SoundMixer _cableSounds = new();
    readonly SoundMixer _monitorSounds = new();
    WdlResampler? _resampler;
    int _micRate, _micChannels;
    bool _micFloat; int _micBits;
    float[] _mono = new float[16384];
    float[] _resampled = new float[16384];
    readonly object _lifecycle = new();
    string? _lastEffect;

    AudioEngine()
    {
        _cableSounds.SoundEnded += id => SoundStateChanged?.Invoke(id, IsSoundPlaying(id));
        _monitorSounds.SoundEnded += id => { if (id == PreviewId) PreviewEnded?.Invoke(); };
        Settings.Changed += ApplyLiveSettings;
    }

    // ================= lifecycle =================

    public void Start()
    {
        lock (_lifecycle)
        {
            StopInternal();
            var s = Settings.Current;
            LastError = null;
            try
            {
                _micDev = Devices.Get(DataFlow.Capture, s.InputDeviceId);
                _outDev = Devices.Get(DataFlow.Render, s.OutputDeviceId);
                if (string.IsNullOrEmpty(s.InputDeviceId))
                {
                    var pick = Devices.PickDefault(Devices.List(DataFlow.Capture, true));
                    if (pick != null) { _micDev?.Dispose(); _micDev = Devices.Get(DataFlow.Capture, pick.Id); }
                }
                if (string.IsNullOrEmpty(s.OutputDeviceId))
                {
                    var pick = Devices.PickDefault(Devices.List(DataFlow.Render, true));
                    if (pick != null) { _outDev?.Dispose(); _outDev = Devices.Get(DataFlow.Render, pick.Id); }
                }
                _cableDev = Devices.FindCableRender();
                CableFound = _cableDev != null;

                ApplyLiveSettings();

                // If someone picked the cable itself as mic we would feed back; refuse.
                if (_micDev != null && Devices.IsVirtualCable(_micDev.FriendlyName))
                {
                    LastError = "The microphone is set to the virtual cable. Pick your real microphone in Settings.";
                    _micDev = null;
                }

                // outputs first so the rings are being drained
                var emptyRing = new MonoRingSource(_cableRing);
                if (_cableDev != null)
                {
                    var src = new SumSource(emptyRing, _cableSounds);
                    _cableOut = new WasapiOut(_cableDev, AudioClientShareMode.Shared, true, 40);
                    _cableOut.Init(src);
                    _cableOut.Play();
                }
                if (_outDev != null)
                {
                    var src = new SumSource(new MonoRingSource(_monitorRing), _monitorSounds);
                    _monitorOut = new WasapiOut(_outDev, AudioClientShareMode.Shared, true, 40);
                    _monitorOut.Init(src);
                    _monitorOut.Play();
                }

                if (_micDev != null)
                {
                    _capture = new WasapiCapture(_micDev, true, 20);
                    var wf = _capture.WaveFormat;
                    _micRate = wf.SampleRate; _micChannels = wf.Channels;
                    var enc = wf.Encoding;
                    if (wf is WaveFormatExtensible ext) enc = ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71") ? WaveFormatEncoding.IeeeFloat : WaveFormatEncoding.Pcm;
                    _micFloat = enc == WaveFormatEncoding.IeeeFloat;
                    _micBits = wf.BitsPerSample;
                    _resampler = _micRate != Rate ? new WdlResampler() : null;
                    _resampler?.SetMode(true, 2, false);
                    _resampler?.SetFilterParms();
                    _resampler?.SetFeedMode(true);
                    _resampler?.SetRates(_micRate, Rate);
                    _capture.DataAvailable += OnMicData;
                    _capture.RecordingStopped += (_, e) =>
                    {
                        if (e.Exception != null) { Log.Error("mic stopped", e.Exception); LastError = "Microphone stopped: " + e.Exception.Message; StatusChanged?.Invoke(); }
                    };
                    _capture.StartRecording();
                }
                else if (LastError == null) LastError = "No microphone found.";

                IsRunning = true;
                Log.Info($"engine started mic='{_micDev?.FriendlyName}' out='{_outDev?.FriendlyName}' cable='{_cableDev?.FriendlyName}'");
            }
            catch (Exception ex)
            {
                Log.Error("engine start", ex);
                LastError = ex.Message;
                StopInternal();
            }
        }
        StatusChanged?.Invoke();
    }

    public void Restart() => Start();

    void StopInternal()
    {
        IsRunning = false;
        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _cableOut?.Stop(); _cableOut?.Dispose(); } catch { }
        try { _monitorOut?.Stop(); _monitorOut?.Dispose(); } catch { }
        _capture = null; _cableOut = null; _monitorOut = null;
        _micDev?.Dispose(); _outDev?.Dispose(); _cableDev?.Dispose();
        _micDev = _outDev = _cableDev = null;
        InputLevel = OutputLevel = 0;
    }

    public void Dispose()
    {
        lock (_lifecycle) StopInternal();
    }

    // ================= live settings =================

    public void ApplyLiveSettings()
    {
        var s = Settings.Current;
        _cableSounds.Gain = (float)s.SoundboardVolume;
        _monitorSounds.Gain = (float)s.MonitorVolume;

        var info = EffectCatalog.Get(s.ActiveEffect);
        lock (_effects)
        {
            if (!_effects.TryGetValue(info.Id, out var fx)) _effects[info.Id] = fx = info.Create();
            fx.Configure(key => s.FxParams.TryGetValue($"{info.Id}.{key}", out var v) ? v : info.Params.First(p => p.Key == key).Default);
            if (_lastEffect != info.Id) { _lastEffect = info.Id; fx.Reset(); }
            _activeFx = fx;
        }
    }

    readonly Dictionary<string, IVoiceEffect> _effects = new();
    volatile IVoiceEffect? _activeFx;

    // ================= mic path =================

    void OnMicData(object? sender, WaveInEventArgs e)
    {
        try { ProcessMic(e.Buffer, e.BytesRecorded); }
        catch (Exception ex) { Log.Error("mic process", ex); }
    }

    void ProcessMic(byte[] bytes, int length)
    {
        var s = Settings.Current;
        int bytesPerSample = _micBits / 8;
        int frames = length / (bytesPerSample * _micChannels);
        if (frames <= 0) return;
        if (_mono.Length < frames) _mono = new float[frames];

        // decode to mono float
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _micChannels; c++)
            {
                int idx = (f * _micChannels + c) * bytesPerSample;
                float v;
                if (_micFloat && bytesPerSample == 4) v = BitConverter.ToSingle(bytes, idx);
                else if (bytesPerSample == 2) v = BitConverter.ToInt16(bytes, idx) / 32768f;
                else if (bytesPerSample == 3) v = ((bytes[idx] << 8) | (bytes[idx + 1] << 16) | (bytes[idx + 2] << 24)) / 2147483648f;
                else if (bytesPerSample == 4) v = BitConverter.ToInt32(bytes, idx) / 2147483648f;
                else v = 0;
                sum += v;
            }
            _mono[f] = sum / _micChannels;
        }

        float[] block = _mono;
        int count = frames;

        // resample to 48k if needed
        if (_resampler != null)
        {
            int avail = _resampler.ResamplePrepare(frames, 1, out float[] inBuf, out int inOff);
            Array.Copy(_mono, 0, inBuf, inOff, frames);
            int maxOut = (int)(frames * (double)Rate / _micRate) + 16;
            if (_resampled.Length < maxOut) _resampled = new float[maxOut];
            count = _resampler.ResampleOut(_resampled, 0, avail, maxOut, 1);
            block = _resampled;
        }
        if (count <= 0) return;

        // raw level meter
        float peak = 0;
        for (int i = 0; i < count; i++) { float a = Math.Abs(block[i]); if (a > peak) peak = a; }
        InputLevel = Math.Max(peak, InputLevel * 0.85f);

        bool muted = s.MicMuted;
        if (muted)
        {
            Array.Clear(block, 0, count);
        }
        else
        {
            float vol = (float)s.MicVolume;
            if (Math.Abs(vol - 1f) > 0.001f) for (int i = 0; i < count; i++) block[i] *= vol;

            if (s.VoiceChangerEnabled) _activeFx?.Process(block, count);
        }

        float opeak = 0;
        for (int i = 0; i < count; i++) { float a = Math.Abs(block[i]); if (a > opeak) opeak = a; }
        OutputLevel = Math.Max(opeak, OutputLevel * 0.85f);

        var span = block.AsSpan(0, count);
        _cableRing.Write(span);
        if (s.HearMyself && !muted) _monitorRing.Write(span);

        // the clip buffer wants the mic only if it isn't already audible through the system loopback
        if (s.ClipIncludeMic && !(s.HearMyself && !muted)) ClipMicRing.Write(span);
        else ClipMicRing.WriteSilence(count);
    }

    // ================= sounds =================

    public bool PlaySound(SoundItem item)
    {
        try
        {
            var data = SoundCache.Get(item.FullPath);
            float vol = (float)item.Volume;
            _cableSounds.Play(data, vol, item.Id);
            _monitorSounds.Play(data, vol, item.Id);
            item.Playing = true;
            SoundStateChanged?.Invoke(item.Id, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("play sound " + item.Name, ex);
            return false;
        }
    }

    public void StopSound(string id)
    {
        _cableSounds.Stop(id);
        _monitorSounds.Stop(id);
        SoundStateChanged?.Invoke(id, false);
    }

    public bool IsSoundPlaying(string id) => _cableSounds.IsPlaying(id) || _monitorSounds.IsPlaying(id);

    public void StopAllSounds()
    {
        var ids = _cableSounds.PlayingIds.Concat(_monitorSounds.PlayingIds).Distinct().ToList();
        _cableSounds.StopAll();
        _monitorSounds.StopAll();
        foreach (var id in ids) SoundStateChanged?.Invoke(id, false);
    }

    const string PreviewId = "__preview__";

    /// <summary>Preview audio only in your headphones (clip editor, clips list); never reaches Yappest Input.</summary>
    public void PlayPreview(float[] data, float volume = 1f) => _monitorSounds.Play(data, volume, PreviewId);
    public void StopPreview() { _monitorSounds.Stop(PreviewId); PreviewEnded?.Invoke(); }
    /// <summary>Raised when a preview finishes by itself or is stopped.</summary>
    public event Action? PreviewEnded;
    public bool IsPreviewPlaying => _monitorSounds.IsPlaying(PreviewId);

    /// <summary>Play one of the app's own effect sounds on the output device (and on the input if asked).</summary>
    public void PlayAppSound(string file, bool alsoToInput, double volume)
    {
        try
        {
            var path = AppPaths.AppSound(file);
            if (!System.IO.File.Exists(path)) return;
            var data = SoundCache.Get(path);
            _monitorSounds.Play(data, (float)volume, null);
            if (alsoToInput) _cableSounds.Play(data, (float)volume, null);
        }
        catch (Exception ex) { Log.Error("app sound", ex); }
    }

    // ================= controls used by UI, hotkeys, Stream Deck =================

    public void SetMuted(bool muted)
    {
        var s = Settings.Current;
        if (s.MicMuted == muted) return;
        s.MicMuted = muted;
        Settings.Commit();
    }

    public void ToggleMute() => SetMuted(!Settings.Current.MicMuted);
}
