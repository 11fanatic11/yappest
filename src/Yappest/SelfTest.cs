using System.IO;
using System.Text;
using NAudio.Wave;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;

namespace Yappest;

/// <summary>Headless checks for the DSP and clip pipeline: <c>Yappest.exe --selftest [speech.wav]</c>. Writes selftest.txt next to the settings.</summary>
public static class SelfTest
{
    static readonly StringBuilder Out = new();
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fail++;
        Out.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    }

    public static int Run(string? speechWav)
    {
        try
        {
            Settings.Load();
            const int rate = AudioEngine.Rate;

            // ---- pitch effect: 150 Hz in, +7 semitones out should be ~224.8 Hz
            var pitch = new PitchEffect { Semitones = 7 };
            var buf = Tone(150, 0.4f, rate * 2);
            for (int i = 0; i < buf.Length; i += 480) Slice(buf, i, pitch);
            float f = EstimateFreq(buf, rate, rate / 2, rate * 2);
            Check("pitch +7 st moves 150 Hz to ~225 Hz", Math.Abs(f - 224.8) < 12, $"measured {f:0.0} Hz");

            var pitch0 = new PitchEffect { Semitones = 0 };
            var b0 = Tone(150, 0.4f, rate);
            var copy = (float[])b0.Clone();
            pitch0.Process(b0, b0.Length);
            Check("pitch 0 st is bypassed", b0.SequenceEqual(copy));

            // ---- sound barrier: quiet speech -> loud; scream -> dropouts
            var sb = new SoundBarrierEffect { Drive = 60, Grit = 45, BreakPoint = 55, Cutout = 60 };
            var quiet = Tone(180, 0.05f, rate * 2);
            sb.Process(quiet, quiet.Length);
            Check("barrier makes a quiet voice loud", Rms(quiet) > 0.25, $"rms {Rms(quiet):0.000} (input 0.035)");

            sb.Reset();
            var scream = Tone(180, 0.9f, rate * 3);
            sb.Process(scream, scream.Length);
            double gaps = SilentFraction(scream, rate / 100);          // runs of >=10 ms of silence
            var calm = Tone(180, 0.05f, rate * 3);
            sb.Reset(); sb.Process(calm, calm.Length);
            double calmGaps = SilentFraction(calm, rate / 100);
            Check("screaming breaks the barrier (cuts in and out)", gaps > 0.08 && calmGaps < 0.01, $"silent {gaps:P0} when screaming, {calmGaps:P0} when calm");

            // ---- every voice in the catalog: finite, bounded, audible, and different from the dry signal
            foreach (var info in EffectCatalog.All)
            {
                var fx = info.Create();
                fx.Configure(k => info.Params.First(p => p.Key == k).Default);
                var sig = Tone(160, 0.3f, rate * 2);
                var dry = (float[])sig.Clone();
                for (int i = 0; i < sig.Length; i += 480) Slice(sig, i, fx);
                bool finite = sig.All(float.IsFinite);
                float peak = sig.Max(Math.Abs);
                double diff = Math.Sqrt(sig.Zip(dry, (a, b) => (double)(a - b) * (a - b)).Sum() / sig.Length);
                Check($"voice '{info.Name}' is stable and audible", finite && peak <= 1.0001f && Rms(sig) > 0.01 && (info.Id == "pitch" ? true : diff > 0.01),
                    $"rms {Rms(sig):0.000}, peak {peak:0.00}, differs from dry by {diff:0.000}");
            }

            // ---- Voicemod local API reachability (bogus key on purpose, only checks the handshake path)
            if (VoicemodImporter.IsInstalled())
            {
                using var vm = new VoicemodApi();
                bool ok = vm.ConnectAsync("yappest-selftest-invalid-key").GetAwaiter().GetResult();
                Out.AppendLine($"INFO  voicemod api with an invalid key: connected={ok}, note: {vm.Error ?? "none"}");
            }

            // ---- transcript tags like "[no audio]" arrive split across words and must disappear
            var tagged = new[] { "[no", "audio]", "hello", "(music)", "world", "[BLANK_AUDIO]", "*laughs*", "ok" }
                .Select((t, i) => new WordStamp { Text = t, Start = i, End = i + 0.5 });
            var stripped = string.Join(" ", Transcriber.StripSoundTags(tagged).Select(w => w.Text));
            Check("sound tags are removed from transcripts", stripped == "hello world ok", $"'{stripped}'");

            // ---- update flow against a local feed (no installer is ever run)
            {
                var installer = new byte[300_000]; new Random(5).NextBytes(installer);
                var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installer));
                using var http = new System.Net.HttpListener();
                int port = 48100 + Environment.ProcessId % 500;
                http.Prefixes.Add($"http://127.0.0.1:{port}/");
                http.Start();
                string feed = "{\"version\":\"9.9.9\",\"notes\":[\"Test note\"],\"url\":\"http://127.0.0.1:" + port + "/setup.exe\",\"sha256\":\"" + sha + "\",\"size\":300000}";
                var serve = Task.Run(() =>
                {
                    for (int i = 0; i < 6; i++)
                    {
                        System.Net.HttpListenerContext ctx;
                        try { ctx = http.GetContext(); } catch { return; }
                        var body = ctx.Request.Url!.AbsolutePath.EndsWith("setup.exe") ? installer : Encoding.UTF8.GetBytes(feed);
                        ctx.Response.OutputStream.Write(body); ctx.Response.Close();
                    }
                });
                Environment.SetEnvironmentVariable("YAPPEST_UPDATE_URL", $"http://127.0.0.1:{port}/update.json");
                var chk = Updater.CheckAsync().GetAwaiter().GetResult();
                Check("update check sees a newer version", chk.Status == UpdateStatus.Available && chk.Info?.Version.ToString() == "9.9.9" && chk.Info.Notes.Contains("Test note"), $"{chk.Status}, current {Updater.CurrentText}");
                var path = Updater.DownloadAsync(chk.Info!, null).GetAwaiter().GetResult();
                Check("update download is verified against its checksum", File.Exists(path) && new FileInfo(path).Length == 300_000);
                File.Delete(path);
                var bad = chk.Info! with { Sha256 = new string('0', 64) };
                bool rejected = false;
                try { Updater.DownloadAsync(bad, null).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
                Check("a download with the wrong checksum is rejected and deleted", rejected && !File.Exists(Path.Combine(Path.GetTempPath(), "Yappest-Setup-9.9.9.exe")));
                var untrusted = chk.Info with { Url = "https://evil.example.com/x.exe" };
                Environment.SetEnvironmentVariable("YAPPEST_UPDATE_URL", null);
                http.Stop();
            }

            // ---- game icon lookup (network): Minecraft via Discord's public application endpoint
            {
                var icon = GameIcons.ResolveAsync(new GameInfo("Minecraft", null, "javaw", "1402418491272986635", null)).GetAwaiter().GetResult();
                Check("game icon is fetched and cached for Minecraft", icon != null && File.Exists(icon) && new FileInfo(icon).Length > 2000, icon ?? "none");
            }

            // ---- ring buffer
            var ring = new FloatRing(1000);
            ring.Write(Enumerable.Range(0, 1500).Select(i => (float)i).ToArray());
            var tail = ring.Tail(10);
            Check("ring keeps the newest samples", tail[^1] == 1499 && tail[0] == 1490);

            // ---- mp3 round trip
            var tmp = Path.Combine(Path.GetTempPath(), "yappest-selftest.mp3");
            var stereo = new float[rate * 2];
            for (int i = 0; i < rate; i++) { var v = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / rate)); stereo[i * 2] = v; stereo[i * 2 + 1] = v; }
            ClipRecorder.WriteMp3(tmp, stereo);
            var dur = SoundCache.ProbeDuration(tmp);
            Check("clip mp3 is written and readable", File.Exists(tmp) && Math.Abs(dur - 1.0) < 0.15, $"{dur:0.00}s, {new FileInfo(tmp).Length} bytes");
            var decoded = SoundCache.Decode(tmp);
            Check("clip mp3 decodes to engine format", decoded.Length > rate * 2 * 0.9 && Rms(decoded) > 0.2, $"{decoded.Length / 2} frames");
            File.Delete(tmp);

            // ---- the app's own clip sound
            var sfx = AppPaths.AppSound("audclip.wav");
            Check("audclip.wav is present and decodes", File.Exists(sfx) && SoundCache.Get(sfx).Length > 1000, sfx);

            // ---- Voicemod cache (info only)
            if (VoicemodImporter.IsInstalled())
            {
                var scan = VoicemodImporter.Scan();
                Out.AppendLine($"INFO  voicemod cache: {scan.Usable.Count} readable sounds, {scan.Unreadable} unreadable (protected format)");
            }

            // ---- live capture: numbers only, no audio is stored
            var eng = AudioEngine.Instance;
            eng.Start();
            ClipRecorder.Instance.Start();
            Thread.Sleep(4000);
            var micTail = eng.ClipMicRing.Tail(rate * 3);
            var sysTail = ClipRecorder.Instance.SysTail(rate * 3);
            Check("engine started with a microphone and the virtual cable", eng.IsRunning && eng.LastError == null, $"cable found: {eng.CableFound}, error: {eng.LastError ?? "none"}");
            Check("mic ring is filling in real time", micTail.Length >= rate * 3 * 0.9, $"{micTail.Length} of {rate * 3} samples, rms {Rms(micTail):0.0000}");
            Check("system loopback ring is filling in real time", sysTail.Length >= rate * 2 * 3 * 0.9, $"{sysTail.Length / 2} of {rate * 3} frames, rms {Rms(sysTail):0.0000}, captured {ClipRecorder.Instance.FramesCaptured}");

            // ---- Stream Deck control socket round trip
            StreamDeckServer.Instance.Start();
            using (var ws = new System.Net.WebSockets.ClientWebSocket())
            {
                ws.ConnectAsync(new Uri($"ws://127.0.0.1:{StreamDeckServer.Instance.Port}/"), CancellationToken.None).GetAwaiter().GetResult();
                var rb = new byte[65536];
                var r = ws.ReceiveAsync(rb, CancellationToken.None).GetAwaiter().GetResult();
                var first = Encoding.UTF8.GetString(rb, 0, r.Count);
                r = ws.ReceiveAsync(rb, CancellationToken.None).GetAwaiter().GetResult();
                var second = Encoding.UTF8.GetString(rb, 0, r.Count);
                Check("stream deck socket sends state and sounds on connect", first.Contains("\"event\":\"state\"") && second.Contains("\"event\":\"sounds\""), $"{first.Length}+{second.Length} bytes");
                var before = Settings.Current.VoiceChangerEnabled;
                ws.SendAsync(Encoding.UTF8.GetBytes("{\"action\":\"toggleVoiceChanger\"}"), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
                r = ws.ReceiveAsync(rb, CancellationToken.None).GetAwaiter().GetResult();
                var pushed = Encoding.UTF8.GetString(rb, 0, r.Count);
                Check("stream deck command changes state and pushes an update", Settings.Current.VoiceChangerEnabled != before && pushed.Contains($"\"voiceChanger\":{(!before).ToString().ToLower()}"), pushed.Length > 120 ? pushed[..120] : pushed);
                Settings.Current.VoiceChangerEnabled = before;
            }
            eng.Dispose(); ClipRecorder.Instance.Stop();

            // ---- optional: transcription of a speech wav
            if (speechWav != null && File.Exists(speechWav))
            {
                if (!Transcriber.ModelInstalled) Check("whisper model installed", false, "download it first");
                else
                {
                    var samples = SoundCache.Decode(speechWav);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (text, words) = Transcriber.TranscribeAsync(samples).GetAwaiter().GetResult();
                    Out.AppendLine($"      transcript: \"{text}\"  ({words.Count} words, {sw.Elapsed.TotalSeconds:0.0}s for {samples.Length / 2.0 / rate:0.0}s audio)");
                    foreach (var w in words.Take(12)) Out.AppendLine($"        {w.Start,5:0.00} - {w.End,5:0.00}  {w.Text}");
                    Check("whisper returns words with timestamps", words.Count >= 3 && words.All(w => w.End >= w.Start));
                }
            }
        }
        catch (Exception ex)
        {
            Check("self test ran without exceptions", false, ex.ToString());
        }
        File.WriteAllText(Path.Combine(AppPaths.Root, "selftest.txt"), Out.ToString());
        return _fail;
    }

    // process in 480-frame blocks like the live engine (10 ms)
    static float[] Slice(float[] buf, int i, IVoiceEffect fx)
    {
        int n = Math.Min(480, buf.Length - i);
        var block = new float[n];
        Array.Copy(buf, i, block, 0, n);
        fx.Process(block, n);
        Array.Copy(block, 0, buf, i, n);
        return Array.Empty<float>();
    }

    static float[] Tone(double hz, float amp, int n)
    {
        var d = new float[n];
        for (int i = 0; i < n; i++)
            d[i] = amp * (float)(Math.Sin(2 * Math.PI * hz * i / AudioEngine.Rate) + 0.35 * Math.Sin(2 * Math.PI * hz * 2 * i / AudioEngine.Rate));
        return d;
    }

    static double Rms(float[] d) => Math.Sqrt(d.Sum(x => (double)x * x) / Math.Max(1, d.Length));

    static float EstimateFreq(float[] d, int rate, int from, int to)
    {
        // zero crossings of the fundamental (rising edge)
        int crossings = 0; int first = -1, last = -1;
        for (int i = from + 1; i < to; i++)
            if (d[i - 1] < 0 && d[i] >= 0) { crossings++; if (first < 0) first = i; last = i; }
        return crossings < 2 ? 0 : (crossings - 1) * rate / (float)(last - first);
    }

    static double SilentFraction(float[] d, int minRun)
    {
        int run = 0, silent = 0;
        foreach (var x in d)
        {
            if (Math.Abs(x) < 0.001f) run++;
            else { if (run >= minRun) silent += run; run = 0; }
        }
        if (run >= minRun) silent += run;
        return silent / (double)d.Length;
    }
}
