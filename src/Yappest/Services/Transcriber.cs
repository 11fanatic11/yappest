using System.IO;
using System.Net.Http;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>Offline speech-to-text (Whisper via whisper.cpp). The model is a one-time download.</summary>
public static class Transcriber
{
    static WhisperFactory? _factory;
    static string? _factoryModel;
    static readonly SemaphoreSlim Gate = new(1, 1);
    static Timer? _idle;

    /// <summary>The model ships inside the installer (vendor\models); a downloaded copy in the profile is the fallback.</summary>
    public static string ModelFile(string model)
    {
        var bundled = Path.Combine(AppPaths.VendorDir, "models", $"ggml-{model}.bin");
        return File.Exists(bundled) ? bundled : Path.Combine(AppPaths.ModelsDir, $"ggml-{model}.bin");
    }
    public static bool ModelInstalled => File.Exists(ModelFile(Settings.Current.WhisperModel));

    public static string ModelSizeText(string model) => model switch { "tiny" => "75 MB", "base" => "142 MB", "small" => "466 MB", _ => "?" };

    public static event Action<double>? DownloadProgress;       // 0..1
    public static event Action? ModelChanged;

    public static async Task DownloadModelAsync(string model, CancellationToken ct = default)
    {
        var dest = Path.Combine(AppPaths.ModelsDir, $"ggml-{model}.bin");
        var tmp = dest + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{model}.bin";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) DownloadProgress?.Invoke((double)done / total);
            }
        }
        File.Move(tmp, dest, true);
        DownloadProgress?.Invoke(1);
        ModelChanged?.Invoke();
        Log.Info("whisper model downloaded: " + model);
    }

    /// <summary>16 kHz mono PCM16 wav (in memory) from 48k stereo float.</summary>
    static MemoryStream To16kWav(float[] stereo48k)
    {
        var src = new FloatArrayProvider(stereo48k);
        var mono = new StereoToMonoSampleProvider(src) { LeftVolume = 0.5f, RightVolume = 0.5f };
        var rs = new WdlResamplingSampleProvider(mono, 16000);
        var ms = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(ms, new SampleToWaveProvider16(rs));
        ms.Position = 0;
        return ms;
    }

    public static async Task<(string text, List<WordStamp> words)> TranscribeAsync(float[] stereo48k, CancellationToken ct = default)
    {
        var model = Settings.Current.WhisperModel;
        if (!File.Exists(ModelFile(model))) throw new FileNotFoundException("model");

        await Gate.WaitAsync(ct);
        try
        {
            if (_factory == null || _factoryModel != model)
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(ModelFile(model));
                _factoryModel = model;
            }
            _idle?.Dispose();

            using var wav = To16kWav(stereo48k);
            await using var processor = _factory.CreateBuilder()
                .WithLanguage("auto")
                .WithTokenTimestamps()
                .WithMaxSegmentLength(1)
                .SplitOnWord()
                .Build();

            var words = new List<WordStamp>();
            await foreach (var seg in processor.ProcessAsync(wav, ct))
                words.Add(new WordStamp { Text = seg.Text.Trim(), Start = seg.Start.TotalSeconds, End = seg.End.TotalSeconds });
            words = StripSoundTags(words);
            var text = string.Join(" ", words.Select(w => w.Text));
            return (text, words);
        }
        finally
        {
            _idle = new Timer(_ =>
            {
                if (Gate.Wait(0))
                {
                    try { _factory?.Dispose(); _factory = null; } finally { Gate.Release(); }
                }
            }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);
            Gate.Release();
        }
    }

    public static float[] DecodeFile(string path) => SoundCache.Decode(path);

    /// <summary>
    /// Whisper marks silence and noise with tags like [no audio], [BLANK_AUDIO] or (music). Because the transcript is split
    /// word by word, a tag arrives as several pieces ("[no", "audio]"), so drop everything from an opening bracket to its closing one.
    /// </summary>
    public static List<WordStamp> StripSoundTags(IEnumerable<WordStamp> words)
    {
        var kept = new List<WordStamp>();
        char close = '\0';
        foreach (var w in words)
        {
            var t = w.Text.Trim();
            if (t.Length == 0) continue;
            if (close == '\0' && (t[0] == '[' || t[0] == '(' || t[0] == '*'))
                close = t[0] == '[' ? ']' : t[0] == '(' ? ')' : '*';
            else if (close != '\0') { if (t.Contains(close)) close = '\0'; continue; }
            else { kept.Add(w); continue; }

            // the opening piece may also contain the closing bracket ("[music]")
            if (t.Length > 1 && t.IndexOf(close, 1) >= 0) close = '\0';
        }
        return kept;
    }

    sealed class FloatArrayProvider : ISampleProvider
    {
        readonly float[] _d; int _p;
        public FloatArrayProvider(float[] d) => _d = d;
        public WaveFormat WaveFormat => AudioEngine.StereoFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _d.Length - _p);
            Array.Copy(_d, _p, buffer, offset, n);
            _p += n;
            return n;
        }
    }
}
