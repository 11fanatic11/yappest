using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Threading;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;
using Yappest.UI;

namespace Yappest;

public partial class App : System.Windows.Application
{
    const string PipeName = "Yappest.ShowWindow";
    static Mutex? _mutex;
    MainWindow? _main;
    bool _quitting;

    [STAThread]
    public static int Main(string[] args)
    {
        // elevated helper modes (launched by the app itself when the user approves the Windows prompt)
        if (args.Contains("--elevated-install")) return VirtualCable.ElevatedInstall();
        if (args.Contains("--elevated-rename")) return VirtualCable.ElevatedRename();
        if (args.Contains("--elevated-setup")) return VirtualCable.ElevatedSetup();
        if (args.Contains("--selftest"))
        {
            var i = Array.IndexOf(args, "--selftest");
            return SelfTest.Run(i + 1 < args.Length ? args[i + 1] : null);
        }

        if (args.Contains("--render-toast"))
        {
            var rt = new App();
            rt.InitializeComponent();
            ThemeService.Apply(ThemeService.DefaultAccent);
            ToastWindow.RenderPreview("Clipped the last 30s", args[Array.IndexOf(args, "--render-toast") + 1]);
            return 0;
        }

        _mutex = new Mutex(true, "Yappest.SingleInstance", out bool first);
        if (!first)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(1500);
                c.WriteByte(1);
            }
            catch { }
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Error("ui exception", ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("fatal", ev.ExceptionObject as Exception);

        Settings.Load();
        SoundLibrary.Instance.Load();
        ClipStore.Instance.Load();
        ThemeService.Apply(Settings.Current.AccentColor);

        bool background = e.Args.Contains("--background");
        bool firstRun = !Settings.Current.SetupComplete;

        HotkeyService.Instance.Init();
        TrayService.Instance.Init();
        Controller.RebindHotkeys();
        Settings.Changed += () => Dispatcher.BeginInvoke(Controller.RebindHotkeys);
        Settings.Changed += () => Dispatcher.BeginInvoke(() => ThemeService.Apply(Settings.Current.AccentColor));

        StartAudio();
        _ = GameDetector.Instance.InitAsync().ContinueWith(_ => GameIcons.BackfillAsync(ClipStore.Instance.Clips));
        _ = AutoSetupAsync();
        _ = BootUpdateCheckAsync();
        if (Settings.Current.StreamDeckEnabled) StreamDeckServer.Instance.Start();
        ListenForSecondInstance();

        // dev helper: open the clip editor on a wav file (written into the test profile only)
        var demo = e.Args.FirstOrDefault(a => a.StartsWith("--demo-clip="));
        if (demo != null && Environment.GetEnvironmentVariable("YAPPEST_DATA") != null)
        {
            Task.Run(async () =>
            {
                var samples = SoundCache.Decode(demo["--demo-clip=".Length..]);
                var dir = Path.Combine(AppPaths.Root, "democlips");
                Directory.CreateDirectory(dir);
                var mp3 = Path.Combine(dir, "demo.mp3");
                ClipRecorder.WriteMp3(mp3, samples);
                var clip = new ClipItem { File = mp3, Game = "Phasmophobia", DurationSeconds = Math.Round(samples.Length / 2.0 / AudioEngine.Rate, 1), Status = "transcribing" };
                await Dispatcher.InvokeAsync(() => ClipStore.Instance.Add(clip));
                await ClipRecorder.TranscribeInBackground(clip, samples);
                await Dispatcher.InvokeAsync(() => ShowMain("clips", clip));
            });
        }

        // Windows 10/11 run-at-login is on by default after install
        if (firstRun) StartupService.Apply(true);

        if (firstRun)
        {
            var wiz = new SetupWindow();
            wiz.Show();
        }
        else if (!background)
        {
            var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page="));
            ShowMain(pageArg?[7..]);
        }
    }

    /// <summary>A short while after launch, look for a newer version. It only ever asks; installing is always the user's choice.</summary>
    async Task BootUpdateCheckAsync()
    {
        await Task.Delay(8000);
        if (!Settings.Current.AutoCheckUpdates) return;
        var r = await Updater.CheckAsync();
        if (r.Status == UpdateStatus.Failed) { Log.Info("update check failed: " + r.Error); return; }
        if (r.Status != UpdateStatus.Available || r.Info == null) return;
        if (r.Info.Version.ToString(3) == Settings.Current.SkippedUpdateVersion) return;
        await Dispatcher.InvokeAsync(() => UpdateWindow.ShowPrompt(r.Info));
    }

    const int CurrentSetupVersion = 2;

    /// <summary>
    /// First boot (and after updates that need it): one admin prompt installs the virtual mic if needed,
    /// names it "Yappest Input" and switches off Windows' "Listen to this device" on it.
    /// </summary>
    async Task AutoSetupAsync()
    {
        var s = Settings.Current;
        if (s.SetupVersion >= CurrentSetupVersion || s.SetupAttempts >= 2) return;
        if (!VirtualCable.NeedsSetup()) { s.SetupVersion = CurrentSetupVersion; Settings.Commit(); return; }

        await Task.Delay(1500);
        s.SetupAttempts++;
        bool ok = await VirtualCable.RunElevatedAsync("--elevated-setup");
        await Task.Delay(2500);
        if (ok && !VirtualCable.NeedsSetup()) s.SetupVersion = CurrentSetupVersion;
        Settings.Commit();
        await Dispatcher.InvokeAsync(StartAudio);
    }

    public void StartAudio()
    {
        AudioEngine.Instance.Start();
        ClipRecorder.Instance.Start();
    }

    public void ShowMain(string? page = null, ClipItem? clip = null)
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        if (page != null) _main.Navigate(page, clip);
    }

    public bool IsQuitting => _quitting;

    public void Quit()
    {
        _quitting = true;
        Settings.SaveNow();
        ClipStore.Instance.Save();
        TrayService.Instance.Dispose();
        StreamDeckServer.Instance.Dispose();
        ClipRecorder.Instance.Dispose();
        AudioEngine.Instance.Dispose();
        HotkeyService.Instance.Dispose();
        Shutdown();
    }

    void ListenForSecondInstance()
    {
        Task.Run(async () =>
        {
            while (!_quitting)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    server.ReadByte();
                    Dispatcher.Invoke(() => ShowMain());
                }
                catch (Exception ex) { Log.Warn("pipe: " + ex.Message); await Task.Delay(500); }
            }
        });
    }
}
