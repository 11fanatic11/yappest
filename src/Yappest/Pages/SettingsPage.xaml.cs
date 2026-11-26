using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;
using Yappest.UI;

namespace Yappest.Pages;

public partial class SettingsPage : UserControl
{
    bool _loading;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };

    public SettingsPage()
    {
        InitializeComponent();
        BuildSwatches();
        Loaded += (_, _) =>
        {
            Fill(); _poll.Start();
            if (Environment.GetEnvironmentVariable("YAPPEST_SCROLL_END") != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroller.ScrollToEnd());   // screenshots only
        };
        Unloaded += (_, _) => _poll.Stop();
        _poll.Tick += (_, _) => RefreshStatuses();
        Settings.Changed += () => Dispatcher.BeginInvoke(RefreshToggles);
        Transcriber.ModelChanged += () => Dispatcher.BeginInvoke(RefreshStatuses);

        // hotkeys
        ClipKey.ValueChanged += v => { Settings.Current.ClipHotkey = v; Settings.Commit(); Dispatcher.BeginInvoke(CheckHotkeyErrors); };
        MuteKey.ValueChanged += v => { Settings.Current.MuteHotkey = v; Settings.Commit(); Dispatcher.BeginInvoke(CheckHotkeyErrors); };
        StopKey.ValueChanged += v => { Settings.Current.StopSoundsHotkey = v; Settings.Commit(); Dispatcher.BeginInvoke(CheckHotkeyErrors); };
    }

    // ---------- fill ----------
    void Fill()
    {
        _loading = true;
        var s = Settings.Current;

        FillCombo(MicBox, Devices.List(DataFlow.Capture, true), s.InputDeviceId, null);
        FillCombo(OutBox, Devices.List(DataFlow.Render, true), s.OutputDeviceId, null);
        FillCombo(SysBox, Devices.List(DataFlow.Render, true), s.SystemAudioDeviceId, "Same as playback");

        ClipKey.Value = s.ClipHotkey;
        MuteKey.Value = s.MuteHotkey;
        StopKey.Value = s.StopSoundsHotkey;
        RefreshToggles();
        FolderText.Text = s.ClipsFolder;
        HexBox.Text = s.AccentColor;
        VmKey.Text = s.VoicemodApiKey;
        SyncSwatches();
        RefreshStatuses();
        CheckHotkeyErrors();
        _loading = false;
    }

    static void FillCombo(ComboBox box, List<DeviceInfo> items, string? selectedId, string? defaultLabel)
    {
        box.Items.Clear();
        if (defaultLabel != null) box.Items.Add(new DeviceInfo("", defaultLabel, false));
        foreach (var d in items) box.Items.Add(d);
        var sel = box.Items.OfType<DeviceInfo>().FirstOrDefault(d => d.Id == selectedId)
                  ?? (defaultLabel != null ? box.Items[0] : Devices.PickDefault(box.Items.OfType<DeviceInfo>()));
        box.SelectedItem = sel ?? (box.Items.Count > 0 ? box.Items[0] : null);
    }

    void RefreshToggles()
    {
        _loading = true;
        var s = Settings.Current;
        ClipMicSwitch.IsChecked = s.ClipIncludeMic;
        ClipSysSwitch.IsChecked = s.ClipIncludeSystem;
        ToastSwitch.IsChecked = s.ClipToast;
        AutoUpdateSwitch.IsChecked = s.AutoCheckUpdates;
        VersionText.Text = "Yappest " + Updater.CurrentText;
        ClipSoundSwitch.IsChecked = s.ClipSoundToInput;
        StartupSwitch.IsChecked = s.RunOnStartup;
        TraySwitch.IsChecked = s.CloseToTray;
        LenSlider.Value = s.ClipSeconds;
        LenText.Text = s.ClipSeconds >= 60 ? "1 minute" : $"{s.ClipSeconds} seconds";
        _loading = false;
    }

    void RefreshStatuses()
    {
        // virtual cable
        var st = VirtualCable.State();
        switch (st)
        {
            case VirtualCable.CableState.Missing:
                CableStatus.Text = "Not installed. Yappest needs a small audio driver to create the virtual microphone. Windows will ask for permission once.";
                CableBtn.Content = "Install"; CableBtn.IsEnabled = true; break;
            case VirtualCable.CableState.Installed:
                CableStatus.Text = "Driver installed, but Windows still calls it “CABLE Output”. Set up renames it to “Yappest Input”.";
                CableBtn.Content = "Set up"; CableBtn.IsEnabled = true; break;
            default:
                if (VirtualCable.ListenEnabled())
                {
                    CableStatus.Text = "Windows' “Listen to this device” is on for Yappest Input, so you hear yourself all the time. Fix turns it off.";
                    CableBtn.Content = "Fix"; CableBtn.IsEnabled = true;
                }
                else
                {
                    CableStatus.Text = "Ready. Pick “Yappest Input” as your microphone in Discord, games and OBS.";
                    CableBtn.Content = "Ready"; CableBtn.IsEnabled = false;
                }
                break;
        }

        // model
        var model = Settings.Current.WhisperModel;
        if (Transcriber.ModelInstalled) { ModelText.Text = "Installed. Clips are transcribed on this PC, nothing is uploaded."; ModelBtn.Content = "Installed"; ModelBtn.IsEnabled = false; }
        else if (_downloading == null) { ModelText.Text = $"Needed to transcribe clips ({Transcriber.ModelSizeText(model)}, one-time download, then fully offline)."; ModelBtn.Content = "Download"; ModelBtn.IsEnabled = true; }

        // stream deck
        var sd = StreamDeckServer.Instance;
        var pluginDir = SdPluginInstallDir();
        bool installed = Directory.Exists(pluginDir);
        SdStatus.Text = sd.ClientCount > 0 ? "Connected. Find the Yappest actions in the Stream Deck app."
            : installed ? "Plugin installed. Open the Stream Deck app and drag Yappest actions onto keys."
            : Directory.Exists(Path.GetDirectoryName(pluginDir)!) ? "Plugin not installed yet." : "Stream Deck app not found on this PC.";
        SdBtn.Content = installed ? "Reinstall plugin" : "Install plugin";
        SdBtn.IsEnabled = Directory.Exists(Path.GetDirectoryName(pluginDir)!);


        // voicemod
        if (VoicemodImporter.IsInstalled()) { VmText.Text = "Voicemod found on this PC. Imports the sounds it has saved locally; no Voicemod login needed."; VmBtn.IsEnabled = true; }
        else { VmText.Text = "Voicemod wasn't found on this PC."; VmBtn.IsEnabled = false; }
    }

    void CheckHotkeyErrors()
    {
        void Show(TextBlock tb, string name, string combo)
        {
            var f = string.IsNullOrEmpty(combo) ? null : HotkeyService.Instance.Failure(name);
            tb.Text = f ?? ""; tb.Visibility = f == null ? Visibility.Collapsed : Visibility.Visible;
        }
        Show(ClipKeyError, "clip", Settings.Current.ClipHotkey);
        Show(MuteKeyError, "mute", Settings.Current.MuteHotkey);
        Show(StopKeyError, "stop", Settings.Current.StopSoundsHotkey);
    }

    // ---------- devices ----------
    void Mic_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicBox.SelectedItem is not DeviceInfo d) return;
        Settings.Current.InputDeviceId = d.Id;
        Settings.Commit();
        AudioEngine.Instance.Restart();
    }

    void Out_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutBox.SelectedItem is not DeviceInfo d) return;
        Settings.Current.OutputDeviceId = d.Id;
        Settings.Commit();
        AudioEngine.Instance.Restart();
        ClipRecorder.Instance.Start();
    }

    void Sys_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SysBox.SelectedItem is not DeviceInfo d) return;
        Settings.Current.SystemAudioDeviceId = string.IsNullOrEmpty(d.Id) ? null : d.Id;
        Settings.Commit();
        ClipRecorder.Instance.Start();
    }

    async void Cable_Click(object sender, RoutedEventArgs e)
    {
        var st = VirtualCable.State();
        var arg = "--elevated-setup";
        var what = st == VirtualCable.CableState.Missing
            ? "Yappest will install its virtual audio driver and name it “Yappest Input”. Windows will ask for admin permission, and your audio may cut out for a second."
            : "Yappest will rename the virtual microphone to “Yappest Input”. Windows will ask for admin permission, and your audio may cut out for a second.";
        if (!PromptDialog.Confirm(Window.GetWindow(this), "Set up Yappest Input", what, "Continue")) return;

        CableBtn.IsEnabled = false; CableBtn.Content = "Working…";
        bool ok = await VirtualCable.RunElevatedAsync(arg);
        await Task.Delay(1500);
        AudioEngine.Instance.Restart();
        ClipRecorder.Instance.Start();
        RefreshStatuses();
        if (!ok) PromptDialog.Info(Window.GetWindow(this), "Nothing changed", "The Windows permission prompt was declined, or the step failed. Details are in the log.");
    }

    // ---------- toggles ----------
    void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Settings.Current;
        s.ClipIncludeMic = ClipMicSwitch.IsChecked == true;
        s.ClipIncludeSystem = ClipSysSwitch.IsChecked == true;
        s.ClipToast = ToastSwitch.IsChecked == true;
        s.AutoCheckUpdates = AutoUpdateSwitch.IsChecked == true;
        s.ClipSoundToInput = ClipSoundSwitch.IsChecked == true;
        s.CloseToTray = TraySwitch.IsChecked == true;
        if (s.RunOnStartup != (StartupSwitch.IsChecked == true))
        {
            s.RunOnStartup = StartupSwitch.IsChecked == true;
            StartupService.Apply(s.RunOnStartup);
        }
        Settings.Commit();
    }

    void Len_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        Settings.Current.ClipSeconds = (int)LenSlider.Value;
        Settings.Commit();
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Where should clips be saved?", SelectedPath = Settings.Current.ClipsFolder, UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        Settings.Current.ClipsFolder = dlg.SelectedPath;
        FolderText.Text = dlg.SelectedPath;
        Settings.Commit();
    }

    // ---------- speech model ----------
    Task? _downloading;

    async void Model_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading != null) return;
        ModelBtn.IsEnabled = false;
        void P(double p) => Dispatcher.BeginInvoke(() => { ModelText.Text = $"Downloading… {p * 100:0}%"; ModelBtn.Content = $"{p * 100:0}%"; });
        Transcriber.DownloadProgress += P;
        try
        {
            _downloading = Transcriber.DownloadModelAsync(Settings.Current.WhisperModel);
            await _downloading;
        }
        catch (Exception ex)
        {
            Log.Error("model download", ex);
            ModelText.Text = "Download failed: " + ex.Message;
        }
        finally { Transcriber.DownloadProgress -= P; _downloading = null; RefreshStatuses(); }
    }

    // ---------- appearance ----------
    void BuildSwatches()
    {
        foreach (var hex in ThemeService.Presets)
        {
            ThemeService.TryParse(hex, out var c);
            var rb = new RadioButton { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), Tag = hex, GroupName = "swatch", ToolTip = hex };
            rb.Checked += (_, _) => { if (!_loading) SetAccent(hex); };
            Swatches.Children.Add(rb);
        }
    }

    void SyncSwatches()
    {
        foreach (RadioButton rb in Swatches.Children)
            rb.IsChecked = string.Equals((string)rb.Tag, Settings.Current.AccentColor, StringComparison.OrdinalIgnoreCase);
    }

    void SetAccent(string hex)
    {
        Settings.Current.AccentColor = hex;
        _loading = true; HexBox.Text = hex; SyncSwatches(); _loading = false;
        Settings.Commit();
    }

    void Hex_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var t = HexBox.Text.Trim();
        if (!t.StartsWith('#')) t = "#" + t;
        if (t.Length is 7 or 9 && ThemeService.TryParse(t, out _))
        {
            Settings.Current.AccentColor = t.ToUpperInvariant();
            _loading = true; SyncSwatches(); _loading = false;
            Settings.Commit();
        }
    }

    void PickColor_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.TryParse(Settings.Current.AccentColor, out var cur);
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B) };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        SetAccent($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
    }

    void ResetColor_Click(object sender, RoutedEventArgs e) => SetAccent(ThemeService.DefaultAccent);

    // ---------- stream deck ----------
    static string SdPluginInstallDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "Plugins", "com.yappest.sdPlugin");

    async void Sd_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        try
        {
            var src = AppPaths.StreamDeckPluginSource;
            if (!Directory.Exists(src)) { PromptDialog.Info(owner, "Plugin files missing", "The Stream Deck plugin folder wasn't found next to Yappest."); return; }
            var dest = SdPluginInstallDir();

            bool running = Process.GetProcessesByName("StreamDeck").Length > 0;
            if (running)
            {
                if (!PromptDialog.Confirm(owner, "Restart Stream Deck?", "Stream Deck has to restart to load the new plugin. Your keys will blank for a few seconds.", "Restart it")) return;
                foreach (var p in Process.GetProcessesByName("StreamDeck")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                await Task.Delay(800);
            }

            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            CopyDir(src, dest);

            if (running) await LaunchStreamDeckAsync();
            RefreshStatuses();
            PromptDialog.Info(owner, "Plugin installed", "In the Stream Deck app, look for the Yappest category on the right and drag actions onto your keys.");
        }
        catch (Exception ex)
        {
            Log.Error("sd install", ex);
            PromptDialog.Info(owner, "Couldn't install the plugin", ex.Message);
        }
    }

    static async Task LaunchStreamDeckAsync()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Elgato", "StreamDeck", "StreamDeck.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Elgato", "StreamDeck", "StreamDeck.exe"),
        };
        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        await Task.Delay(500);
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    // ---------- voicemod ----------
    void VmKey_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Settings.Current.VoicemodApiKey = VmKey.Text.Trim();
        Settings.Commit();
    }

    async void Vm_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        VmBtn.IsEnabled = false; VmBtn.Content = "Looking…";
        var plan = await VoicemodImporter.BuildPlanAsync(Settings.Current.VoicemodApiKey);
        VmBtn.Content = "Import…"; VmBtn.IsEnabled = true;

        if (plan.Total == 0) { PromptDialog.Info(owner, "Nothing to import", VoicemodImporter.Describe(plan)); return; }
        if (!PromptDialog.Confirm(owner, "Import from Voicemod", VoicemodImporter.Describe(plan) + " Your Voicemod install isn't touched.", "Import")) return;

        VmBtn.IsEnabled = false; VmBtn.Content = "Importing…";
        int n = await Task.Run(() => VoicemodImporter.Execute(plan));
        VmBtn.Content = "Import…"; VmBtn.IsEnabled = true;
        PromptDialog.Info(owner, "Done", $"Imported {n} sounds. Find them on the Sounds page.");
    }

    // ---------- setup / updates ----------
    void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        var wiz = new SetupWindow();
        wiz.Owner = Window.GetWindow(this);
        wiz.Show();
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false; UpdateBtn.Content = "Checking…";
        var r = await Updater.CheckAsync();
        UpdateBtn.Content = "Check for updates"; UpdateBtn.IsEnabled = true;
        switch (r.Status)
        {
            case UpdateStatus.Available when r.Info != null:
                UpdateNote.Text = $"Version {r.Info.Version.ToString(3)} is available.";
                UpdateWindow.ShowPrompt(r.Info);
                break;
            case UpdateStatus.UpToDate:
                UpdateNote.Text = "You're on the latest version.";
                break;
            default:
                UpdateNote.Text = "Couldn't check right now: " + r.Error;
                break;
        }
    }

    void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    void Logs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Root}\"") { UseShellExecute = true });
}
