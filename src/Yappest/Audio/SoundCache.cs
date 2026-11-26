using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Yappest.Core;

namespace Yappest.Audio;

/// <summary>Decodes sound files once into 48 kHz stereo float arrays.</summary>
public static class SoundCache
{
    static readonly Dictionary<string, (DateTime stamp, float[] data)> Cache = new();
    static readonly LinkedList<string> Lru = new();
    static readonly object Gate = new();
    const long MaxBytes = 300L * 1024 * 1024;

    public static ISampleProvider OpenReader(string path, out IDisposable owner)
    {
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var v = new VorbisWaveReader(path);
            owner = v;
            return v;
        }
        var m = new MediaFoundationReader(path);
        owner = m;
        return m.ToSampleProvider();
    }

    /// <summary>Convert any provider to 48k stereo.</summary>
    public static ISampleProvider ToEngineFormat(ISampleProvider sp)
    {
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new MultiplexingSampleProvider(new[] { sp }, 2);
        if (sp.WaveFormat.SampleRate != AudioEngine.Rate)
            sp = new WdlResamplingSampleProvider(sp, AudioEngine.Rate);
        return sp;
    }

    public static float[] Decode(string path)
    {
        var sp = OpenReader(path, out var owner);
        try
        {
            var eng = ToEngineFormat(sp);
            var list = new List<float[]>();
            int total = 0;
            var buf = new float[AudioEngine.Rate * 2];
            int n;
            while ((n = eng.Read(buf, 0, buf.Length)) > 0)
            {
                var c = new float[n];
                Array.Copy(buf, c, n);
                list.Add(c);
                total += n;
                if (total > AudioEngine.Rate * 2 * 60 * 20) break;   // hard cap: 20 minutes
            }
            var all = new float[total];
            int o = 0;
            foreach (var c in list) { Array.Copy(c, 0, all, o, c.Length); o += c.Length; }
            return all;
        }
        finally { owner.Dispose(); }
    }

    public static float[] Get(string path)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var hit))
            {
                Lru.Remove(path); Lru.AddFirst(path);
                return hit.data;
            }
        }
        var data = Decode(path);
        lock (Gate)
        {
            Cache[path] = (DateTime.UtcNow, data);
            Lru.Remove(path); Lru.AddFirst(path);
            long bytes = Cache.Values.Sum(v => (long)v.data.Length * 4);
            while (bytes > MaxBytes && Lru.Count > 1)
            {
                var last = Lru.Last!.Value;
                bytes -= (long)Cache[last].data.Length * 4;
                Cache.Remove(last);
                Lru.RemoveLast();
            }
        }
        return data;
    }

    public static void Invalidate(string path)
    {
        lock (Gate) { Cache.Remove(path); Lru.Remove(path); }
    }

    public static double ProbeDuration(string path)
    {
        try
        {
            if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                // own the stream: when the file isn't valid Vorbis the reader throws without releasing it, and the file couldn't be deleted
                using var fs = File.OpenRead(path);
                using var v = new VorbisWaveReader(fs);
                return v.TotalTime.TotalSeconds;
            }
            using var m = new MediaFoundationReader(path);
            return m.TotalTime.TotalSeconds;
        }
        catch (Exception ex)
        {
            Log.Warn("probe " + path + ": " + ex.Message);
            return 0;
        }
    }
}
