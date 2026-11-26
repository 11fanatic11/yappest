using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;

namespace Yappest.UI;

public partial class SetupWindow : Window
{
    readonly List<FrameworkElement> _steps;
    readonly List<int> _order = new();
    int _pos;
    bool _finished;
    Task? _download;

    public SetupWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _steps = new() { S0, S1, S2, S3, S4, S5 };
        _order.AddRange(new[] { 0, 1, 2 });
        if (VoicemodImporter.IsInstalled()) _order.Add(3);
        _order.AddRange(new[] { 4, 5 });

        FillCombo(MicBox, Devices.List(DataFlow.Capture, true));
        FillCombo(OutBox, Devices.List(DataFlow.Render, true));
        MicBox.SelectionChanged += (_, _) => { if (MicBox.SelectedItem is DeviceInfo d) Settings.Current.InputDeviceId = d.Id; };
        OutBox.SelectionChanged += (_, _) => { if (OutBox.SelectedItem is DeviceInfo d) Settings.Current.OutputDeviceId = d.Id; };
        Settings.Current.InputDeviceId ??= (MicBox.SelectedItem as DeviceInfo)?.Id;
        Settings.Current.OutputDeviceId ??= (OutBox.SelectedItem as DeviceInfo)?.Id;

        var scan = VoicemodImporter.IsInstalled() ? VoicemodImporter.Scan() : new(new(), 0);
        var prot = scan.Unreadable > 0 ? $" ({scan.Unreadable} others are in a protected Voicemod format and can't be read, so they're skipped.)" : "";
        VmText.Text = scan.Usable.Count > 0
            ? $"Voicemod is installed here, with {scan.Usable.Count} readable saved sounds on this PC. Want them in Yappest?{prot}"
            : $"Voicemod is installed here, but none of its saved sounds can be imported.{prot}";
        VmBtn.IsEnabled = scan.Usable.Count > 0;

        Show_();
        Closing += (_, _) =>
        {
            if (!_finished) { Settings.Current.SetupComplete = true; Settings.Commit(); }
        };
    }

    static void FillCombo(ComboBox box, List<DeviceInfo> items)
    {
        foreach (var d in items) box.Items.Add(d);
        box.SelectedItem = Devices.PickDefault(items);
    }

    void Show_()
    {
        for (int i = 0; i < _steps.Count; i++) _steps[i].Visibility = i == _order[_pos] ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = _pos == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextBtn.Content = _pos == _order.Count - 1 ? "Finish" : _order[_pos] == 0 ? "Get started" : _order[_pos] is 3 or 4 ? "Continue" : "Next";

        Dots.Children.Clear();
        for (int i = 0; i < _order.Count; i++)
            Dots.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0),
                Fill = i == _pos ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("Bg4"),
            });

        switch (_order[_pos])
        {
            case 1: break;
            case 2: RefreshCable(); break;
            case 4: RefreshModel(); break;
            case 5:
                var s = Settings.Current;
                DoneText.Text = $"Pick “Yappest Input” as your microphone in Discord, your games and OBS. Press {s.ClipHotkey} any time to clip the last {s.ClipSeconds} seconds, and {s.MuteHotkey} to mute. You'll find Yappest in the tray (the hidden icons arrow) when it's running in the background.";
                break;
        }
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_order[_pos] == 1)
        {
            Settings.Commit();
            AudioEngine.Instance.Restart();
            ClipRecorder.Instance.Start();
        }
        if (_pos == _order.Count - 1) { Finish(); return; }
        _pos++;
        Show_();
    }

    void Back_Click(object sender, RoutedEventArgs e) { if (_pos > 0) { _pos--; Show_(); } }

    void Finish()
    {
        _finished = true;
        Settings.Current.SetupComplete = true;
        Settings.Current.RunOnStartup = StartupSwitch.IsChecked == true;
        StartupService.Apply(Settings.Current.RunOnStartup);
        Settings.Commit();
        Settings.SaveNow();
        Close();
        ((App)System.Windows.Application.Current).ShowMain();
    }

    // ---- cable ----
    void RefreshCable()
    {
        var st = VirtualCable.State();
        switch (st)
        {
            case VirtualCable.CableState.Missing:
                CableDot.Fill = (Brush)FindResource("Danger"); CableTitle.Text = "Not installed";
                CableDetail.Text = "The virtual microphone driver isn't on this PC yet.";
                CableBtn.Content = "Install"; CableBtn.IsEnabled = true; break;
            case VirtualCable.CableState.Installed:
                CableDot.Fill = (Brush)FindResource("AccentBrush"); CableTitle.Text = "Driver found";
                CableDetail.Text = "It's installed but still named “CABLE Output”. Rename it so apps show “Yappest Input”.";
                CableBtn.Content = "Rename"; CableBtn.IsEnabled = true; break;
            default:
                if (VirtualCable.ListenEnabled())
                {
                    CableDot.Fill = (Brush)FindResource("AccentBrush"); CableTitle.Text = "You'd hear yourself all the time";
                    CableDetail.Text = "Windows' “Listen to this device” is switched on for Yappest Input. Fix turns it off.";
                    CableBtn.Content = "Fix"; CableBtn.IsEnabled = true;
                }
                else
                {
                    CableDot.Fill = (Brush)FindResource("Ok"); CableTitle.Text = "Yappest Input is ready";
                    CableDetail.Text = "You'll see it in Discord, games and OBS as a microphone.";
                    CableBtn.Content = "Ready"; CableBtn.IsEnabled = false;
                }
                break;
        }
    }

    async void Cable_Click(object sender, RoutedEventArgs e)
    {
        var st = VirtualCable.State();
        CableBtn.IsEnabled = false; CableBtn.Content = "Working…";
        bool ok = await VirtualCable.RunElevatedAsync("--elevated-setup");
        await Task.Delay(1500);
        AudioEngine.Instance.Restart();
        ClipRecorder.Instance.Start();
        RefreshCable();
        if (!ok) CableDetail.Text = "That didn't go through (the Windows prompt was declined or the step failed). You can try again, or skip and do it later in Settings.";
    }

    // ---- voicemod ----
    async void Vm_Click(object sender, RoutedEventArgs e)
    {
        VmBtn.IsEnabled = false; VmBtn.Content = "Importing…";
        var plan = await VoicemodImporter.BuildPlanAsync(Settings.Current.VoicemodApiKey);
        int n = await Task.Run(() => VoicemodImporter.Execute(plan));
        VmBtn.Content = "Imported";
        VmDone.Text = $"{n} sounds added to “Imported from Voicemod”.";
    }

    // ---- model ----
    void RefreshModel()
    {
        if (Transcriber.ModelInstalled) { ModelBtn.Content = "Installed"; ModelBtn.IsEnabled = false; ModelText.Text = "Ready."; }
        else if (_download == null) { ModelBtn.Content = $"Download ({Transcriber.ModelSizeText(Settings.Current.WhisperModel)})"; ModelBtn.IsEnabled = true; ModelText.Text = ""; }
    }

    async void Model_Click(object sender, RoutedEventArgs e)
    {
        ModelBtn.IsEnabled = false;
        void P(double p) => Dispatcher.BeginInvoke(() => { ModelText.Text = $"Downloading… {p * 100:0}%"; });
        Transcriber.DownloadProgress += P;
        try { _download = Transcriber.DownloadModelAsync(Settings.Current.WhisperModel); await _download; }
        catch (Exception ex) { Log.Error("model download", ex); ModelText.Text = "Download failed. Check your connection, or do it later in Settings."; }
        finally { Transcriber.DownloadProgress -= P; _download = null; RefreshModel(); }
    }
}
