using NAudio.Dsp;
using Yappest.Core;

namespace Yappest.Audio;

public interface IVoiceEffect
{
    /// <summary>Read the current parameter values (key -> value).</summary>
    void Configure(Func<string, double> get);
    /// <summary>Process mono 48 kHz samples in place.</summary>
    void Process(float[] buf, int count);
    void Reset();
}

public sealed record ParamSpec(string Key, string Label, double Min, double Max, double Default, double Step, string Unit);

public sealed record EffectInfo(string Id, string Name, string Description, string Glyph, ParamSpec[] Params, Func<IVoiceEffect> Create);

public static class EffectCatalog
{
    static ParamSpec P(string key, string label, double def, string unit = "%", double min = 0, double max = 100, double step = 1)
        => new(key, label, min, max, def, step, unit);

    public static readonly EffectInfo[] All =
    {
        new("pitch", "Pitch", "Shift your voice up or down.", "\uE9E9",
            new[] { P("semitones", "Pitch", 4, "st", -12, 12) }, () => new PitchEffect()),

        new("barrier", "Sound Barrier", "Blown-out and awful. Scream to break it.", "\uE7BA",
            new[] { P("drive", "Drive", 60), P("grit", "Grit", 45), P("breakpoint", "Break point", 55), P("cutout", "Cut-out", 60) }, () => new SoundBarrierEffect()),

        new("robot", "Robot", "Metallic ring-modulated android.", "\uE99A",
            new[] { P("freq", "Frequency", 110, "Hz", 30, 400), P("metal", "Metal", 70) }, () => new RobotEffect()),

        new("demon", "Demon", "Low, gravelly and a bit unholy.", "\uE945",
            new[] { P("depth", "Depth", 8, "st", 3, 14), P("growl", "Growl", 50), P("dark", "Darkness", 50) }, () => new DemonEffect()),

        new("cavern", "Cavern", "You're shouting down a cave.", "\uE93E",
            new[] { P("size", "Size", 55), P("mix", "Wetness", 45), P("echo", "Echo", 35) }, () => new CavernEffect()),

        new("radio", "Radio", "Crackly walkie-talkie comms.", "\uE95A",
            new[] { P("static", "Static", 35), P("squash", "Squash", 50) }, () => new RadioEffect()),

        new("alien", "Alien", "Warbling visitor from another planet.", "\uE9D9",
            new[] { P("speed", "Wobble speed", 6, "Hz", 1, 15), P("depth", "Wobble depth", 60), P("warp", "Warp", 30) }, () => new AlienEffect()),

        new("whisper", "Whisper", "Voice turned into breath. Spooky.", "\uE993",
            new[] { P("breath", "Breath", 85), P("clarity", "Clarity", 60) }, () => new WhisperEffect()),
    };

    public static EffectInfo Get(string id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];

    public static double Value(Settings s, string effectId, ParamSpec p) =>
        s.FxParams.TryGetValue($"{effectId}.{p.Key}", out var v) ? v : p.Default;
}

// ======================= helpers =======================

static class Dsp
{
    public const int Rate = AudioEngine.Rate;
    public static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);
    public static float Clip(float x) => x > 1f ? 1f : x < -1f ? -1f : x;
}

// ======================= pitch =======================

/// <summary>Real-time pitch shifter (phase vocoder). Semitones in [-12, +12].</summary>
public sealed class PitchEffect : IVoiceEffect
{
    const int Fft = 1024;
    const int Osamp = 4;
    readonly SmbPitchShifter _shifter = new();

    public double Semitones { get; set; }

    public void Configure(Func<string, double> get) => Semitones = get("semitones");

    public void Process(float[] buf, int count)
    {
        if (Math.Abs(Semitones) < 0.01) return;
        float factor = (float)Math.Pow(2.0, Semitones / 12.0);
        _shifter.PitchShift(factor, count, Fft, Osamp, Dsp.Rate, buf);
    }

    public void Reset() { }
}

// ======================= sound barrier =======================

/// <summary>
/// Cranks the mic into the red and wrecks it. Speak normally: loud, gritty and crushed.
/// Scream past the break point and the sound barrier gives way: the signal shatters, cuts in and out and crackles.
/// </summary>
public sealed class SoundBarrierEffect : IVoiceEffect
{
    public double Drive { get; set; } = 60;
    public double Grit { get; set; } = 45;
    public double BreakPoint { get; set; } = 55;
    public double Cutout { get; set; } = 60;

    readonly Random _rng = new();
    float _env;
    float _held;
    int _holdCount;
    int _stateSamples;
    bool _gateOpen = true;
    int _crackle;
    float _broken;

    public void Configure(Func<string, double> get)
    {
        Drive = get("drive"); Grit = get("grit"); BreakPoint = get("breakpoint"); Cutout = get("cutout");
    }

    public void Reset()
    {
        _env = _held = 0; _holdCount = 0; _stateSamples = 0; _gateOpen = true; _crackle = 0; _broken = 0;
    }

    public void Process(float[] buf, int count)
    {
        double driveDb = 6 + Drive / 100.0 * 42;
        float gain = (float)Math.Pow(10, driveDb / 20);
        int decim = 1 + (int)(Grit / 100.0 * 9);
        int levels = (int)Math.Round(Dsp.Lerp(4096, 12, Grit / 100.0));
        float thresh = (float)Dsp.Lerp(0.55, 0.06, BreakPoint / 100.0);
        double cut = Cutout / 100.0;
        float attack = 0.2f, release = 0.0006f;
        const int rate = Dsp.Rate;

        for (int i = 0; i < count; i++)
        {
            float x = buf[i];
            float ax = Math.Abs(x);
            _env += (ax > _env ? attack : release) * (ax - _env);

            float over = _env > thresh ? Math.Min(1f, (_env - thresh) / Math.Max(0.02f, thresh)) : 0f;
            _broken += (over > _broken ? 0.01f : 0.0008f) * (over - _broken);

            float y = x * gain;
            y = y > 1f ? 1f : y < -1f ? -1f : y;
            if (y > 0.9f) y = 0.9f - (y - 0.9f) * 0.5f;

            if (_holdCount <= 0) { _held = y; _holdCount = decim; }
            _holdCount--;
            y = _held;

            y = (float)(Math.Round(y * levels) / levels);

            if (_broken > 0.08f && cut > 0.02)
            {
                if (_stateSamples <= 0)
                {
                    _gateOpen = !_gateOpen;
                    double closedMs = Dsp.Lerp(8, 140, cut) * (0.4 + _rng.NextDouble());
                    double openMs = Dsp.Lerp(160, 30, cut * _broken) * (0.4 + _rng.NextDouble());
                    _stateSamples = (int)((_gateOpen ? openMs : closedMs) * rate / 1000);
                    if (!_gateOpen) _crackle = rate / 200;
                }
                _stateSamples--;
                if (!_gateOpen) y *= 0.0f;
                if (_crackle > 0)
                {
                    _crackle--;
                    y += (float)(_rng.NextDouble() * 2 - 1) * 0.8f;
                }
                float ring = (float)Math.Sign(Math.Sin(2 * Math.PI * 70 * (i + _stateSamples) / rate));
                y = y * (1f - _broken * 0.45f) + y * ring * _broken * 0.45f;
            }
            else
            {
                _gateOpen = true; _stateSamples = 0;
            }

            buf[i] = Dsp.Clip(y);
        }
    }
}

// ======================= robot =======================

/// <summary>Ring modulation against a sine plus a short metallic comb: classic android voice.</summary>
public sealed class RobotEffect : IVoiceEffect
{
    double _freq = 110, _metal = 0.7;
    double _phase;
    readonly float[] _comb = new float[Dsp.Rate / 50];
    int _ci;

    public void Configure(Func<string, double> get) { _freq = get("freq"); _metal = get("metal") / 100.0; }
    public void Reset() { Array.Clear(_comb); _phase = 0; }

    public void Process(float[] buf, int count)
    {
        double inc = 2 * Math.PI * _freq / Dsp.Rate;
        int delay = Math.Clamp((int)(Dsp.Rate / Math.Max(60.0, _freq * 2)), 8, _comb.Length - 1);
        float fb = (float)(0.35 + _metal * 0.45);
        for (int i = 0; i < count; i++)
        {
            float x = buf[i];
            float carrier = (float)Math.Sin(_phase);
            _phase += inc; if (_phase > Math.PI * 2) _phase -= Math.PI * 2;
            float ring = x * carrier;
            float wet = (float)(x * (1 - _metal) + ring * _metal * 1.6);

            int ri = (_ci - delay + _comb.Length) % _comb.Length;
            float y = wet + _comb[ri] * fb;
            _comb[_ci] = Dsp.Clip(y);
            _ci = (_ci + 1) % _comb.Length;
            buf[i] = Dsp.Clip(y * 0.7f);
        }
    }
}

// ======================= demon =======================

/// <summary>Pitch down, growl (amplitude flutter + saturation) and darken.</summary>
public sealed class DemonEffect : IVoiceEffect
{
    readonly SmbPitchShifter _shifter = new();
    double _depth = 8, _growl = 0.5, _dark = 0.5;
    double _lfo;
    BiQuadFilter _lp = BiQuadFilter.LowPassFilter(Dsp.Rate, 3500, 0.7f);
    double _lastDark = -1;

    public void Configure(Func<string, double> get) { _depth = get("depth"); _growl = get("growl") / 100.0; _dark = get("dark") / 100.0; }
    public void Reset() { _lfo = 0; }

    public void Process(float[] buf, int count)
    {
        if (Math.Abs(_dark - _lastDark) > 0.01)
        {
            _lp = BiQuadFilter.LowPassFilter(Dsp.Rate, (float)Dsp.Lerp(6500, 1400, _dark), 0.7f);
            _lastDark = _dark;
        }
        _shifter.PitchShift((float)Math.Pow(2.0, -_depth / 12.0), count, 1024, 4, Dsp.Rate, buf);

        float drive = (float)(1 + _growl * 5);
        for (int i = 0; i < count; i++)
        {
            _lfo += 2 * Math.PI * 38 / Dsp.Rate;
            if (_lfo > Math.PI * 2) _lfo -= Math.PI * 2;
            float flutter = (float)(1 - _growl * 0.55 * (0.5 + 0.5 * Math.Sin(_lfo)));
            float y = (float)Math.Tanh(buf[i] * drive * 1.4f) * flutter;
            y = _lp.Transform(y);
            buf[i] = Dsp.Clip(y * (float)(0.85 + _dark * 0.3));
        }
    }
}

// ======================= cavern =======================

/// <summary>Schroeder reverb plus a slow echo: a big stone room.</summary>
public sealed class CavernEffect : IVoiceEffect
{
    static readonly int[] CombDelays = { 1557, 1617, 1491, 1422, 1277, 1356 };
    static readonly int[] AllpassDelays = { 556, 441, 341 };

    readonly float[][] _combBuf;
    readonly int[] _combIdx;
    readonly float[] _combLp;
    readonly float[][] _apBuf;
    readonly int[] _apIdx;
    readonly float[] _echoBuf = new float[Dsp.Rate];       // up to 1 s
    int _echoIdx;
    double _size = 0.55, _mix = 0.45, _echo = 0.35;

    public CavernEffect()
    {
        double sc = Dsp.Rate / 44100.0 * 1.9;
        _combBuf = CombDelays.Select(d => new float[(int)(d * sc)]).ToArray();
        _combIdx = new int[CombDelays.Length];
        _combLp = new float[CombDelays.Length];
        _apBuf = AllpassDelays.Select(d => new float[(int)(d * sc)]).ToArray();
        _apIdx = new int[AllpassDelays.Length];
    }

    public void Configure(Func<string, double> get) { _size = get("size") / 100.0; _mix = get("mix") / 100.0; _echo = get("echo") / 100.0; }

    public void Reset()
    {
        foreach (var b in _combBuf) Array.Clear(b);
        foreach (var b in _apBuf) Array.Clear(b);
        Array.Clear(_echoBuf);
        Array.Clear(_combLp);
    }

    public void Process(float[] buf, int count)
    {
        float fb = (float)Dsp.Lerp(0.72, 0.93, _size);
        float damp = 0.35f;
        int echoDelay = (int)(Dsp.Rate * Dsp.Lerp(0.18, 0.42, _size));
        float echoFb = (float)(_echo * 0.55);
        float wet = (float)_mix, dry = (float)(1 - _mix * 0.6);

        for (int i = 0; i < count; i++)
        {
            float x = buf[i];

            // slow echo with feedback
            int er = (_echoIdx - echoDelay + _echoBuf.Length) % _echoBuf.Length;
            float echoed = _echoBuf[er];
            _echoBuf[_echoIdx] = Dsp.Clip(x + echoed * echoFb);
            _echoIdx = (_echoIdx + 1) % _echoBuf.Length;
            float input = x + echoed * (float)(_echo * 0.8);

            // parallel combs
            float sum = 0;
            for (int c = 0; c < _combBuf.Length; c++)
            {
                var b = _combBuf[c];
                float o = b[_combIdx[c]];
                _combLp[c] = o * (1 - damp) + _combLp[c] * damp;
                b[_combIdx[c]] = input * 0.3f + _combLp[c] * fb;
                if (++_combIdx[c] >= b.Length) _combIdx[c] = 0;
                sum += o;
            }
            sum /= _combBuf.Length * 0.5f;

            // series allpass
            for (int a = 0; a < _apBuf.Length; a++)
            {
                var b = _apBuf[a];
                float bo = b[_apIdx[a]];
                float v = sum + bo * 0.5f;
                b[_apIdx[a]] = v;
                sum = bo - sum * 0.5f;
                if (++_apIdx[a] >= b.Length) _apIdx[a] = 0;
            }

            buf[i] = Dsp.Clip(input * dry + sum * wet * 1.4f);
        }
    }
}

// ======================= radio =======================

/// <summary>Band-limited, squashed and crackly: walkie-talkie comms.</summary>
public sealed class RadioEffect : IVoiceEffect
{
    readonly BiQuadFilter _hp = BiQuadFilter.HighPassFilter(Dsp.Rate, 420, 0.8f);
    readonly BiQuadFilter _lp = BiQuadFilter.LowPassFilter(Dsp.Rate, 3000, 0.8f);
    readonly BiQuadFilter _lp2 = BiQuadFilter.LowPassFilter(Dsp.Rate, 3000, 0.8f);
    readonly Random _rng = new();
    double _static = 0.35, _squash = 0.5;
    float _env;
    int _hold, _holdCount;
    float _held;
    int _crackle;

    public void Configure(Func<string, double> get) { _static = get("static") / 100.0; _squash = get("squash") / 100.0; }
    public void Reset() { _env = 0; _held = 0; _crackle = 0; }

    public void Process(float[] buf, int count)
    {
        float gain = (float)(1.2 + _squash * 7);
        int decim = 1 + (int)(_squash * 3);
        for (int i = 0; i < count; i++)
        {
            float x = _hp.Transform(buf[i]);
            // fast compressor-ish: divide by smoothed envelope
            float ax = Math.Abs(x);
            _env += (ax > _env ? 0.01f : 0.0004f) * (ax - _env);
            float comp = x / (0.08f + _env * (float)(0.3 + _squash));
            float y = (float)Math.Tanh(comp * 0.9f * (gain / 3f));

            if (_holdCount <= 0) { _held = y; _holdCount = decim; }
            _holdCount--;
            y = _held;

            y = _lp2.Transform(_lp.Transform(y));

            // hiss that swells while you are not speaking, like an open squelch
            float hiss = (float)((_rng.NextDouble() * 2 - 1) * 0.07 * _static * (1.0 - Math.Min(1.0, _env * 8)) + (_rng.NextDouble() * 2 - 1) * 0.012 * _static);
            if (_crackle > 0) { _crackle--; hiss += (float)((_rng.NextDouble() * 2 - 1) * 0.5 * _static); }
            else if (_static > 0.05 && _rng.NextDouble() < 0.00007 * _static) _crackle = 40 + _rng.Next(160);

            buf[i] = Dsp.Clip((y * 0.9f + hiss));
        }
    }
}

// ======================= alien =======================

/// <summary>Two wandering vibrato delays and a touch of ring modulation: an unsettling warble.</summary>
public sealed class AlienEffect : IVoiceEffect
{
    readonly float[] _delay = new float[Dsp.Rate / 20];     // 50 ms
    int _w;
    double _l1, _l2, _ring;
    double _speed = 6, _depth = 0.6, _warp = 0.3;

    public void Configure(Func<string, double> get) { _speed = get("speed"); _depth = get("depth") / 100.0; _warp = get("warp") / 100.0; }
    public void Reset() { Array.Clear(_delay); }

    public void Process(float[] buf, int count)
    {
        double maxSwing = 0.0042 * Dsp.Rate * _depth * 2.2;      // samples
        double centre = maxSwing + 4;
        for (int i = 0; i < count; i++)
        {
            _delay[_w] = buf[i];

            _l1 += 2 * Math.PI * _speed / Dsp.Rate;
            _l2 += 2 * Math.PI * (_speed * 0.37 + 0.3) / Dsp.Rate;
            _ring += 2 * Math.PI * 310 / Dsp.Rate;
            if (_l1 > Math.PI * 2) _l1 -= Math.PI * 2;
            if (_l2 > Math.PI * 2) _l2 -= Math.PI * 2;
            if (_ring > Math.PI * 2) _ring -= Math.PI * 2;

            double d = centre + maxSwing * (0.65 * Math.Sin(_l1) + 0.35 * Math.Sin(_l2));
            double rp = _w - d;
            while (rp < 0) rp += _delay.Length;
            int i0 = (int)rp; int i1 = (i0 + 1) % _delay.Length;
            float frac = (float)(rp - i0);
            float y = _delay[i0] * (1 - frac) + _delay[i1] * frac;

            float ring = y * (float)Math.Sin(_ring);
            y = (float)(y * (1 - _warp * 0.7) + ring * _warp * 1.4);
            // sprinkle of tremolo for the "other planet" feel
            y *= (float)(1 - 0.25 * _depth * (0.5 + 0.5 * Math.Sin(_l1 * 1.9)));

            _w = (_w + 1) % _delay.Length;
            buf[i] = Dsp.Clip(y * 1.15f);
        }
    }
}

// ======================= whisper =======================

/// <summary>A noise vocoder: the shape of your voice (12 bands) is imprinted on breath noise.</summary>
public sealed class WhisperEffect : IVoiceEffect
{
    const int Bands = 12;
    readonly BiQuadFilter[] _ana = new BiQuadFilter[Bands];
    readonly BiQuadFilter[] _car = new BiQuadFilter[Bands];
    readonly float[] _env = new float[Bands];
    readonly Random _rng = new();
    double _breath = 0.85, _clarity = 0.6;
    double _q = -1;

    public WhisperEffect() => Build(4.5);

    void Build(double q)
    {
        _q = q;
        for (int b = 0; b < Bands; b++)
        {
            // log-spaced 180 Hz .. 7 kHz
            double f = 180 * Math.Pow(7000.0 / 180.0, b / (double)(Bands - 1));
            _ana[b] = BiQuadFilter.BandPassFilterConstantPeakGain(Dsp.Rate, (float)f, (float)q);
            _car[b] = BiQuadFilter.BandPassFilterConstantPeakGain(Dsp.Rate, (float)f, (float)q);
        }
    }

    public void Configure(Func<string, double> get)
    {
        _breath = get("breath") / 100.0; _clarity = get("clarity") / 100.0;
        double q = Dsp.Lerp(3.0, 7.0, _clarity);
        if (Math.Abs(q - _q) > 0.2) Build(q);
    }

    public void Reset() { Array.Clear(_env); }

    public void Process(float[] buf, int count)
    {
        float release = (float)Dsp.Lerp(0.004, 0.02, _clarity);
        float wetGain = 42f;
        for (int i = 0; i < count; i++)
        {
            float x = buf[i];
            float noise = (float)(_rng.NextDouble() * 2 - 1);
            float sum = 0;
            for (int b = 0; b < Bands; b++)
            {
                float a = Math.Abs(_ana[b].Transform(x));
                _env[b] += (a > _env[b] ? 0.08f : release) * (a - _env[b]);
                sum += _car[b].Transform(noise) * _env[b];
            }
            float wet = sum * wetGain / Bands * 2.2f;
            buf[i] = Dsp.Clip((float)(wet * _breath + x * (1 - _breath) * 0.6));
        }
    }
}
