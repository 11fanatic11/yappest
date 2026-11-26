using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;
using Yappest.UI;

namespace Yappest.Pages;

public partial class ClipsPage : UserControl
{
    bool _loading;
    ClipItem? _playing;

    public ClipsPage()
    {
        InitializeComponent();
        List.ItemsSource = ClipStore.Instance.Clips;
        ClipStore.Instance.Clips.CollectionChanged += (_, _) => UpdateEmpty();
        Loaded += (_, _) => { Sync(); UpdateEmpty(); };
        Settings.Changed += () => Dispatcher.BeginInvoke(Sync);
        ClipKey.ValueChanged += v => { Settings.Current.ClipHotkey = v; Settings.Commit(); };
        // the highlight and pause icon follow the audio: reset when the preview ends or is stopped
        AudioEngine.Instance.PreviewEnded += () => Dispatcher.BeginInvoke(() => { if (_playing != null) { _playing.IsPlaying = false; _playing = null; } });
    }

    void Sync()
    {
        _loading = true;
        var s = Settings.Current;
        LenSlider.Value = s.ClipSeconds;
        LenText.Text = s.ClipSeconds >= 60 ? "1 minute" : $"{s.ClipSeconds} seconds";
        ClipKey.Value = s.ClipHotkey;
        EmptyHint.Text = string.IsNullOrEmpty(s.ClipHotkey)
            ? "Use Clip now, or the Stream Deck, to save the last few seconds."
            : $"Press {s.ClipHotkey} in any game to save the last {(s.ClipSeconds >= 60 ? "minute" : s.ClipSeconds + " seconds")}.";
        _loading = false;
    }

    void UpdateEmpty() => Empty.Visibility = ClipStore.Instance.Clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Len_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        Settings.Current.ClipSeconds = (int)LenSlider.Value;
        Settings.Commit();
    }

    void ClipNow_Click(object sender, RoutedEventArgs e) => Controller.Clip();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Current.ClipsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Current.ClipsFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("open folder", ex); }
    }

    public void OpenEditor(ClipItem clip) => new ClipEditorWindow(clip) { Owner = Window.GetWindow(this) }.ShowDialog();

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ClipItem c) OpenEditor(c);
    }

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ClipItem c) return;
        if (_playing == c && AudioEngine.Instance.IsPreviewPlaying) { AudioEngine.Instance.StopPreview(); return; }
        try
        {
            var data = await Task.Run(() => Transcriber.DecodeFile(c.File));
            if (_playing != null) _playing.IsPlaying = false;
            AudioEngine.Instance.PlayPreview(data);
            _playing = c;
            c.IsPlaying = true;
        }
        catch (Exception ex) { Log.Error("clip preview", ex); }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ClipItem c &&
            PromptDialog.Confirm(Window.GetWindow(this), "Delete clip", "Delete this clip and its file?", "Delete"))
            ClipStore.Instance.Delete(c);
    }
}
