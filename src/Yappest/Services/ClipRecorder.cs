using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Lame;
using NAudio.Wave;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>
/// Keeps the last minute of audio (your mic after effects + whatever your PC is playing: game, soundboard, Discord)
/// and turns the tail into an mp3 on demand.
/// </summary>
public sealed class ClipRecorder : IDisposable
{
    public static ClipRecorder Instance { get; } = new();

    const int Rate = AudioEngine.Rate;
    readonly FloatRing _sysRing = new(Rate * 2 * 60);           // stereo interleaved
    WasapiLoopbackCapture? _loop;
    WdlResampler? _resampler;
    int _srcRate, _srcChannels;
    long _framesWritten;
    Stopwatch _clock = new();
    readonly object _gate = new();
    float[] _tmp = new float[16384];
    float[] _rs = new float[32768];

    public event Action<ClipItem>? ClipSaved;
    public string? LastError { get; private set; }

    /// <summary>Diagnostics: how much has been captured from the system loopback so far (frames).</summary>
    public long FramesCaptured => _framesWritten;
    public float[] SysTail(int frames) => _sysRing.Tail(frames * 2);

    public void Start()
    {
        lock (_gate)
        {
            Stop();
            try
            {
                var s = Settings.Current;
                var dev = Devices.Get(DataFlow.Render, s.SystemAudioDeviceId ?? s.OutputDeviceId);
                if (dev == null) { LastError = "No output device for system audio."; return; }
                _loop = new WasapiLoopbackCapture(dev);
                var wf = _loop.WaveFormat;
                _srcRate = wf.SampleRate; _srcChannels = wf.Channels;
                _resampler = null;
                if (_srcRate != Rate)
                {
                    _resampler = new WdlResampler();
                    _resampler.SetMode(true, 2, false);
                    _resampler.SetFilterParms();
                    _resampler.SetFeedMode(true);
                    _resampler.SetRates(_srcRate, Rate);
                }
                _framesWritten = 0;
                _clock.Restart();
                _loop.DataAvailable += OnData;
                _loop.StartRecording();
                LastError = null;
                Log.Info($"clip loopback on '{dev.FriendlyName}' {wf}");
            }
            catch (Exception ex) { Log.Error("clip loopback start", ex); LastError = ex.Message; }
        }
    }

    public void Stop()
    {
        try { _loop?.StopRecording(); } catch { }
        try { _loop?.Dispose(); } catch { }
        _loop = null;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            int frames = e.BytesRecorded / (4 * _srcChannels);
            if (frames <= 0) return;
            if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];

            // loopback is IEEE float in shared mode
            for (int f = 0; f < frames; f++)
            {
                int i = f * _srcChannels * 4;
                float l = BitConverter.ToSingle(e.Buffer, i);
                float r = _srcChannels > 1 ? BitConverter.ToSingle(e.Buffer, i + 4) : l;
                _tmp[f * 2] = l; _tmp[f * 2 + 1] = r;
            }

            float[] block = _tmp; int outFrames = frames;
            if (_resampler != null)
            {
                int avail = _resampler.ResamplePrepare(frames, 2, out float[] inBuf, out int inOff);
                Array.Copy(_tmp, 0, inBuf, inOff, frames * 2);
                int max = (int)(frames * (double)Rate / _srcRate) + 16;
                if (_rs.Length < max * 2) _rs = new float[max * 2];
                outFrames = _resampler.ResampleOut(_rs, 0, avail, max, 2);
                block = _rs;
            }

            FillGap(outFrames);
            _sysRing.Write(block.AsSpan(0, outFrames * 2));
            _framesWritten += outFrames;
        }
        catch (Exception ex) { Log.Error("loopback data", ex); }
    }

    /// <summary>Loopback goes quiet (sends nothing) during silence; keep the timeline honest with real silence.</summary>
    void FillGap(int incomingFrames)
    {
        long wall = (long)(_clock.Elapsed.TotalSeconds * Rate);
        long gap = wall - _framesWritten - incomingFrames;
        if (gap > Rate / 20)
        {
            _sysRing.WriteSilence((int)Math.Min(gap, Rate * 60) * 2);
            _framesWritten += gap;
        }
    }

    /// <summary>Grab the last <paramref name="seconds"/> of audio, encode to mp3 in the clips folder.</summary>
    public async Task<ClipItem?> SaveClipAsync(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 60);
        var s = Settings.Current;
        var game = GameDetector.Instance.Probe() ?? GameDetector.Instance.Current;
        int n = seconds * Rate;

        // snapshot rings right now so the clip ends at the moment the key was pressed
        lock (_gate) FillGap(0);
        var sys = _sysRing.Tail(n * 2);
        var mic = AudioEngine.Instance.ClipMicRing.Tail(n);

        return await Task.Run(() =>
        {
            try
            {
                var mix = new float[n * 2];
                int sysOff = n * 2 - sys.Length;
                int micOff = n - mic.Length;
                if (s.ClipIncludeSystem)
                    for (int i = 0; i < sys.Length; i++) mix[sysOff + i] = sys[i];
                if (s.ClipIncludeMic)
                    for (int i = 0; i < mic.Length; i++)
                    {
                        mix[(micOff + i) * 2] += mic[i];
                        mix[(micOff + i) * 2 + 1] += mic[i];
                    }
                for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(mix[i], -1f, 1f);

                Directory.CreateDirectory(s.ClipsFolder);
                var now = DateTime.Now;
                var gameName = game?.Name ?? "Desktop";
                var baseName = AppPaths.SafeFileName($"{now:yyyy-MM-dd HH-mm-ss} - {gameName}");
                var path = Path.Combine(s.ClipsFolder, baseName + ".mp3");
                WriteMp3(path, mix);

                var clip = new ClipItem
                {
                    File = path,
                    Created = now,
                    Game = gameName,
                    GameIconUrl = game == null ? null : GameIcons.ResolveAsync(game).GetAwaiter().GetResult(),
                    DurationSeconds = seconds,
                    Status = Transcriber.ModelInstalled ? "transcribing" : "nomodel",
                };
                ClipSaved?.Invoke(clip);
                _ = TranscribeInBackground(clip, mix);
                return clip;
            }
            catch (Exception ex)
            {
                Log.Error("save clip", ex);
                return null;
            }
        });
    }

    public static void WriteMp3(string path, float[] stereo48k)
    {
        using var writer = new LameMP3FileWriter(path, new WaveFormat(Rate, 16, 2), 192);
        var bytes = new byte[8192 * 2];
        int pos = 0;
        while (pos < stereo48k.Length)
        {
            int chunk = Math.Min(8192, stereo48k.Length - pos);
            for (int i = 0; i < chunk; i++)
            {
                short v = (short)(Math.Clamp(stereo48k[pos + i], -1f, 1f) * 32767);
                bytes[i * 2] = (byte)(v & 0xff);
                bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }
            writer.Write(bytes, 0, chunk * 2);
            pos += chunk;
        }
    }

    public static async Task TranscribeInBackground(ClipItem clip, float[]? samples = null)
    {
        if (!Transcriber.ModelInstalled) { clip.Status = "nomodel"; return; }
        try
        {
            clip.Status = "transcribing";
            samples ??= await Task.Run(() => Transcriber.DecodeFile(clip.File));
            var (text, words) = await Transcriber.TranscribeAsync(samples);
            clip.Words = words;
            clip.Transcript = text;
            clip.Status = "";
        }
        catch (Exception ex)
        {
            Log.Error("transcribe", ex);
            clip.Status = "failed";
        }
        ClipStore.Instance.Save();
    }

    public void Dispose() => Stop();
}
