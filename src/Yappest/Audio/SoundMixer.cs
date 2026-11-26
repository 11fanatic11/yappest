using NAudio.Wave;

namespace Yappest.Audio;

/// <summary>Mixes any number of one-shot sounds (48k stereo float). Always returns a full buffer so outputs keep running.</summary>
public sealed class SoundMixer : ISampleProvider
{
    sealed class Voice
    {
        public required float[] Data;
        public int Pos;
        public float Volume;
        public string? SoundId;
        public int Token;
    }

    readonly List<Voice> _voices = new();
    readonly object _gate = new();

    public float Gain { get; set; } = 1f;
    public WaveFormat WaveFormat => AudioEngine.StereoFormat;

    /// <summary>Raised (from the audio thread) when a sound finishes naturally.</summary>
    public event Action<string>? SoundEnded;

    public int Play(float[] data, float volume, string? soundId, bool restartIfPlaying = true)
    {
        lock (_gate)
        {
            if (soundId != null && restartIfPlaying)
                _voices.RemoveAll(v => v.SoundId == soundId);
            var voice = new Voice { Data = data, Volume = volume, SoundId = soundId, Token = Environment.TickCount };
            _voices.Add(voice);
            return voice.Token;
        }
    }

    public void StopAll() { lock (_gate) _voices.Clear(); }

    public void Stop(string soundId) { lock (_gate) _voices.RemoveAll(v => v.SoundId == soundId); }

    public bool IsPlaying(string soundId) { lock (_gate) return _voices.Any(v => v.SoundId == soundId); }

    public IReadOnlyList<string> PlayingIds { get { lock (_gate) return _voices.Where(v => v.SoundId != null).Select(v => v.SoundId!).ToList(); } }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        List<string>? ended = null;
        lock (_gate)
        {
            for (int vi = _voices.Count - 1; vi >= 0; vi--)
            {
                var v = _voices[vi];
                int n = Math.Min(count, v.Data.Length - v.Pos);
                float g = v.Volume * Gain;
                for (int i = 0; i < n; i++)
                    buffer[offset + i] += v.Data[v.Pos + i] * g;
                v.Pos += n;
                if (v.Pos >= v.Data.Length)
                {
                    _voices.RemoveAt(vi);
                    if (v.SoundId != null) (ended ??= new()).Add(v.SoundId);
                }
            }
        }
        if (ended != null) foreach (var id in ended) SoundEnded?.Invoke(id);
        return count;
    }
}

/// <summary>Sums two stereo sources and hard-limits to [-1, 1].</summary>
public sealed class SumSource : ISampleProvider
{
    readonly ISampleProvider _a, _b;
    float[] _tmp = new float[8192];

    public SumSource(ISampleProvider a, ISampleProvider b) { _a = a; _b = b; }

    public WaveFormat WaveFormat => AudioEngine.StereoFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        _a.Read(buffer, offset, count);
        if (_tmp.Length < count) _tmp = new float[count];
        _b.Read(_tmp, 0, count);
        for (int i = 0; i < count; i++)
        {
            float v = buffer[offset + i] + _tmp[i];
            buffer[offset + i] = v > 1f ? 1f : v < -1f ? -1f : v;
        }
        return count;
    }
}
