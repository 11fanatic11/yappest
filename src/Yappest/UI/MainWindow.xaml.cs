using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;

namespace Yappest.UI;

public partial class MainWindow : Window
{
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(45) };
    double _meterWidth = 96;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        Loaded += (_, _) => RefreshState();
        Settings.Changed += OnSettingsChanged;
        AudioEngine.Instance.StatusChanged += () => Dispatcher.BeginInvoke(RefreshState);
        _tick.Tick += (_, _) => UpdateMeter();
        _tick.Start();
        IsVisibleChanged += (_, _) => { if (IsVisible) _tick.Start(); else _tick.Stop(); };
    }

    void OnSettingsChanged() => Dispatcher.BeginInvoke(RefreshState);

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        if (!app.IsQuitting && Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        Settings.Changed -= OnSettingsChanged;
        base.OnClosing(e);
    }

    void UpdateMeter()
    {
        var level = AudioEngine.Instance.InputLevel;
        // perceptual-ish curve
        double v = Math.Clamp(Math.Pow(level, 0.5), 0, 1);
        MeterFill.Width = _meterWidth * v;
        AudioEngine.Instance.InputLevel = level * 0.7f;
    }

    void RefreshState()
    {
        var s = Settings.Current;
        MuteText.Text = s.MicMuted ? "Muted" : "Mic live";
        MuteGlyph.Text = s.MicMuted ? "" : "";
        MuteGlyph.Foreground = s.MicMuted ? (Brush)FindResource("Danger") : (Brush)FindResource("Text");
        MuteBtn.Foreground = s.MicMuted ? (Brush)FindResource("Danger") : (Brush)FindResource("Text");

        var state = VirtualCable.State();
        if (!AudioEngine.Instance.IsRunning && AudioEngine.Instance.LastError != null)
        {
            CableDot.Fill = (Brush)FindResource("Danger");
            CableText.Text = "Audio error";
            CableChip.ToolTip = AudioEngine.Instance.LastError;
        }
        else if (state == VirtualCable.CableState.Missing)
        {
            CableDot.Fill = (Brush)FindResource("Danger");
            CableText.Text = "Yappest Input not installed";
            CableChip.ToolTip = "Open Settings, then Audio, to install it.";
        }
        else if (state == VirtualCable.CableState.Installed)
        {
            CableDot.Fill = (Brush)FindResource("AccentBrush");
            CableText.Text = "Active · not renamed yet";
            CableChip.ToolTip = "Windows still shows the virtual mic as 'CABLE Output'. Rename it in Settings.";
        }
        else
        {
            CableDot.Fill = (Brush)FindResource("Ok");
            CableText.Text = "Yappest Input ready";
            CableChip.ToolTip = AudioEngine.Instance.LastError;
        }
    }

    void Mute_Click(object sender, RoutedEventArgs e) => Controller.ToggleMute();

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (VoiceView == null) return;
        VoiceView.Visibility = NavVoice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SoundsView.Visibility = NavSounds.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ClipsView.Visibility = NavClips.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = NavVoice.IsChecked == true ? "Voice" : NavSounds.IsChecked == true ? "Soundboard" : NavClips.IsChecked == true ? "Clips" : "Settings";
    }

    public void Navigate(string page, ClipItem? clip = null)
    {
        switch (page)
        {
            case "voice": NavVoice.IsChecked = true; break;
            case "sounds": NavSounds.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
            case "clips":
                NavClips.IsChecked = true;
                if (clip != null) ClipsView.OpenEditor(clip);
                break;
        }
    }
}

/// <summary>Dark title bar + caption colour so the native frame matches the app (Windows 10 20H1+, Windows 11).</summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        int dark = 1;
        DwmSetWindowAttribute(h, 20, ref dark, sizeof(int));      // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x0E0C0C;                                    // BGR of #0C0C0E
        DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));    // DWMWA_CAPTION_COLOR (Win11)
        int text = 0xF3F1F1;
        DwmSetWindowAttribute(h, 36, ref text, sizeof(int));       // DWMWA_TEXT_COLOR
    }
}
