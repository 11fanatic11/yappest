using NAudio.Wave;

namespace Yappest.Audio;

/// <summary>Thread-safe float ring buffer (single or multi channel interleaved), overwrites oldest data.</summary>
public sealed class FloatRing
{
    readonly float[] _buf;
    readonly object _gate = new();
    long _written;            // total samples ever written
    long _read;               // total samples ever consumed (for streaming use)

    public FloatRing(int capacity) => _buf = new float[capacity];

    public int Capacity => _buf.Length;

    /// <summary>Samples currently available for streaming reads.</summary>
    public int Available
    {
        get { lock (_gate) return (int)Math.Min(_written - _read, _buf.Length); }
    }

    public long TotalWritten { get { lock (_gate) return _written; } }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_gate)
        {
            if (data.Length >= _buf.Length)
                data = data[^_buf.Length..];
            int pos = (int)(_written % _buf.Length);
            int first = Math.Min(data.Length, _buf.Length - pos);
            data[..first].CopyTo(_buf.AsSpan(pos));
            if (first < data.Length) data[first..].CopyTo(_buf.AsSpan(0));
            _written += data.Length;
        }
    }

    public void WriteSilence(int count)
    {
        var z = new float[Math.Min(count, _buf.Length)];
        Write(z);
        if (count > z.Length) lock (_gate) _written += count - z.Length;
    }

    /// <summary>Streaming read of up to count samples. Returns number read.</summary>
    public int Read(Span<float> dest)
    {
        lock (_gate)
        {
            long avail = _written - _read;
            if (avail > _buf.Length) { _read = _written - _buf.Length; avail = _buf.Length; }
            int n = (int)Math.Min(avail, dest.Length);
            int pos = (int)(_read % _buf.Length);
            int first = Math.Min(n, _buf.Length - pos);
            _buf.AsSpan(pos, first).CopyTo(dest);
            if (first < n) _buf.AsSpan(0, n - first).CopyTo(dest[first..]);
            _read += n;
            return n;
        }
    }

    public void SkipTo(int keepSamples)
    {
        lock (_gate)
        {
            long avail = _written - _read;
            if (avail > keepSamples) _read = _written - keepSamples;
        }
    }

    /// <summary>Copy the most recent <paramref name="count"/> samples without consuming (for clipping).</summary>
    public float[] Tail(int count)
    {
        lock (_gate)
        {
            count = (int)Math.Min(count, Math.Min(_written, _buf.Length));
            var result = new float[count];
            long start = _written - count;
            int pos = (int)(start % _buf.Length);
            int first = Math.Min(count, _buf.Length - pos);
            Array.Copy(_buf, pos, result, 0, first);
            if (first < count) Array.Copy(_buf, 0, result, first, count - first);
            return result;
        }
    }
}

/// <summary>Reads a mono ring as stereo 48k float with drift control; silence on underrun.</summary>
public sealed class MonoRingSource : ISampleProvider
{
    readonly FloatRing _ring;
    readonly int _cushion = AudioEngine.Rate * 25 / 1000;       // 25 ms
    readonly int _maxLatency = AudioEngine.Rate * 140 / 1000;   // 140 ms
    bool _primed;
    float[] _tmp = new float[4096];

    public MonoRingSource(FloatRing ring) => _ring = ring;

    public WaveFormat WaveFormat => AudioEngine.StereoFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (_tmp.Length < frames) _tmp = new float[frames];

        int avail = _ring.Available;
        if (!_primed)
        {
            if (avail >= _cushion + frames) _primed = true;
        }
        if (_primed && avail > _maxLatency)
            _ring.SkipTo(_cushion + frames);

        int got = _primed ? _ring.Read(_tmp.AsSpan(0, frames)) : 0;
        if (got < frames) _primed = false;   // underrun: rebuild cushion

        for (int i = 0; i < frames; i++)
        {
            float v = i < got ? _tmp[i] : 0f;
            buffer[offset + i * 2] = v;
            buffer[offset + i * 2 + 1] = v;
        }
        return count;
    }
}
