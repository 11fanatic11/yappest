using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;

namespace Yappest.UI;

public partial class ClipEditorWindow : Window
{
    const int Rate = AudioEngine.Rate;
    readonly ClipItem _clip;
    float[] _audio = Array.Empty<float>();
    readonly List<(WordStamp w, Border box, TextBlock tb)> _wordViews = new();
    int _anchorWord = -1;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly Stopwatch _playClock = new();
    double _playFrom;
    bool _playing;

    public ClipEditorWindow(ClipItem clip)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _clip = clip;
        Title = "Edit clip";
        Heading.Text = clip.Game;
        Sub.Text = $"{clip.Created:dddd, MMMM d, yyyy 'at' h:mm:ss tt}  ·  {clip.DurationText}";

        Wave.SelectionChanged += OnSelectionChanged;
        _tick.Tick += (_, _) => TickPlayhead();
        clip.PropertyChanged += OnClipChanged;
        Closed += (_, _) => { clip.PropertyChanged -= OnClipChanged; StopPlayback(); };
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        ShowBusy("Opening clip…");
        try
        {
            _audio = await Task.Run(() => Transcriber.DecodeFile(_clip.File));
            Wave.SetAudio(_audio, Rate);
            UpdateTime();
        }
        catch (Exception ex)
        {
            Log.Error("open clip", ex);
            PromptDialog.Info(this, "Couldn't open this clip", ex.Message);
            Close();
            return;
        }
        HideBusy();
        BuildWords();
    }

    void ShowBusy(string text) { BusyText.Text = text; Busy.Visibility = Visibility.Visible; }
    void HideBusy() => Busy.Visibility = Visibility.Collapsed;

    // ---------- transcript ----------
    void OnClipChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipItem.Transcript) or nameof(ClipItem.Status))
            Dispatcher.BeginInvoke(BuildWords);
    }

    void BuildWords()
    {
        Words.Children.Clear();
        _wordViews.Clear();
        _anchorWord = -1;

        if (_clip.Words.Count == 0)
        {
            Notice.Visibility = Visibility.Visible;
            NoticeBtn.Visibility = Visibility.Collapsed;
            switch (_clip.Status)
            {
                case "transcribing": NoticeText.Text = "Transcribing…"; break;
                case "nomodel":
                    NoticeText.Text = $"Transcripts need a small speech model ({Transcriber.ModelSizeText(Settings.Current.WhisperModel)}, downloaded once, runs offline).";
                    NoticeBtn.Content = "Download speech model"; NoticeBtn.Visibility = Visibility.Visible; break;
                case "failed":
                    NoticeText.Text = "Couldn't transcribe this clip.";
                    NoticeBtn.Content = "Try again"; NoticeBtn.Visibility = Visibility.Visible; break;
                default:
                    NoticeText.Text = Transcriber.ModelInstalled ? "[no words detected]\nYou can still trim on the waveform." : "No transcript yet.";
                    if (!Transcriber.ModelInstalled) { NoticeBtn.Content = "Download speech model"; NoticeBtn.Visibility = Visibility.Visible; }
                    break;
            }
            return;
        }

        Notice.Visibility = Visibility.Collapsed;
        foreach (var w in _clip.Words)
        {
            var tb = new TextBlock { Text = w.Text, FontSize = 15, Foreground = (Brush)FindResource("Text") };
            var box = new Border { Child = tb, Padding = new Thickness(5, 3, 5, 3), CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 1, 1, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var word = w;
            box.MouseLeftButtonDown += (_, _) => WordClicked(word);
            Words.Children.Add(box);
            _wordViews.Add((w, box, tb));
        }
        HighlightWords();
    }

    async void Notice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Transcriber.ModelInstalled)
            {
                NoticeBtn.IsEnabled = false;
                void P(double p) => Dispatcher.BeginInvoke(() => NoticeText.Text = $"Downloading speech model… {p * 100:0}%");
                Transcriber.DownloadProgress += P;
                try { await Transcriber.DownloadModelAsync(Settings.Current.WhisperModel); }
                finally { Transcriber.DownloadProgress -= P; NoticeBtn.IsEnabled = true; }
            }
            _ = ClipRecorder.TranscribeInBackground(_clip, _audio.Length > 0 ? _audio : null);
        }
        catch (Exception ex)
        {
            Log.Error("model download", ex);
            NoticeText.Text = "Download failed: " + ex.Message;
            NoticeBtn.IsEnabled = true;
        }
    }

    void WordClicked(WordStamp w)
    {
        int idx = _clip.Words.IndexOf(w);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchorWord >= 0)
        {
            var a = _clip.Words[_anchorWord];
            double s = Math.Min(a.Start, w.Start), e2 = Math.Max(a.End, w.End);
            Wave.SetSelection(s, e2);
        }
        else
        {
            _anchorWord = idx;
            Wave.SetSelection(Math.Max(0, w.Start - 0.03), Math.Min(Wave.Duration, w.End + 0.05));
        }
    }

    void HighlightWords()
    {
        var soft = (Brush)FindResource("AccentSoftBrush");
        var accent = (Brush)FindResource("AccentBrush");
        foreach (var (w, box, tb) in _wordViews)
        {
            double mid = (w.Start + w.End) / 2;
            bool on = mid >= Wave.SelStart && mid <= Wave.SelEnd && !(Wave.SelStart <= 0.001 && Wave.SelEnd >= Wave.Duration - 0.001);
            box.Background = on ? soft : Brushes.Transparent;
            tb.Foreground = on ? accent : (Brush)FindResource("Text");
        }
    }

    // ---------- selection / playback ----------
    void OnSelectionChanged()
    {
        UpdateTime();
        HighlightWords();
        if (_playing) StopPlayback();
    }

    void UpdateTime()
    {
        TimeText.Text = $"{Fmt(Wave.SelStart)}  →  {Fmt(Wave.SelEnd)}      length {Wave.SelEnd - Wave.SelStart:0.0}s";
    }

    static string Fmt(double t) => $"{(int)t / 60}:{t % 60:00.0}";

    void SelectAll_Click(object sender, RoutedEventArgs e) => Wave.SetSelection(0, Wave.Duration);

    float[] Slice()
    {
        int s = (int)(Wave.SelStart * Rate) * 2, e = (int)(Wave.SelEnd * Rate) * 2;
        s = Math.Clamp(s, 0, _audio.Length); e = Math.Clamp(e, s, _audio.Length);
        var data = new float[e - s];
        Array.Copy(_audio, s, data, 0, data.Length);
        // 5 ms fades so cuts don't click
        int fade = Math.Min(Rate * 5 / 1000, data.Length / 4);
        for (int i = 0; i < fade; i++)
        {
            float g = i / (float)fade;
            data[i * 2] *= g; data[i * 2 + 1] *= g;
            int j = data.Length / 2 - 1 - i;
            data[j * 2] *= g; data[j * 2 + 1] *= g;
        }
        return data;
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) { StopPlayback(); return; }
        var data = Slice();
        if (data.Length == 0) return;
        AudioEngine.Instance.PlayPreview(data);
        _playing = true;
        _playFrom = Wave.SelStart;
        _playClock.Restart();
        _tick.Start();
        PlayText.Text = "Stop"; PlayGlyph.Text = "";
    }

    void StopPlayback()
    {
        if (!_playing && !AudioEngine.Instance.IsPreviewPlaying) return;
        AudioEngine.Instance.StopPreview();
        _playing = false;
        _tick.Stop();
        Wave.SetPlayhead(-1);
        PlayText.Text = "Play"; PlayGlyph.Text = "";
    }

    void TickPlayhead()
    {
        double t = _playFrom + _playClock.Elapsed.TotalSeconds;
        if (t >= Wave.SelEnd || !AudioEngine.Instance.IsPreviewPlaying) { StopPlayback(); return; }
        Wave.SetPlayhead(t);
    }

    // ---------- save ----------
    List<WordStamp> SlicedWords()
    {
        double s = Wave.SelStart, e = Wave.SelEnd;
        return _clip.Words.Where(w => (w.Start + w.End) / 2 >= s && (w.Start + w.End) / 2 <= e)
            .Select(w => new WordStamp { Text = w.Text, Start = Math.Max(0, w.Start - s), End = Math.Max(0, w.End - s) }).ToList();
    }

    async Task<string?> WriteTrimAsync(string path)
    {
        var data = Slice();
        if (data.Length < Rate / 10) { PromptDialog.Info(this, "Selection too short", "Select at least a tenth of a second."); return null; }
        ShowBusy("Saving…");
        try { await Task.Run(() => ClipRecorder.WriteMp3(path, data)); return path; }
        catch (Exception ex) { Log.Error("trim save", ex); PromptDialog.Info(this, "Couldn't save", ex.Message); return null; }
        finally { HideBusy(); }
    }

    async void SaveNew_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_clip.File)!;
        var name = Path.GetFileNameWithoutExtension(_clip.File) + " (trim)";
        var path = Path.Combine(dir, name + ".mp3");
        int n = 2;
        while (File.Exists(path)) path = Path.Combine(dir, $"{name} {n++}.mp3");
        if (await WriteTrimAsync(path) == null) return;
        var words = SlicedWords();
        var nc = new ClipItem
        {
            File = path, Created = _clip.Created, Game = _clip.Game, GameIconUrl = _clip.GameIconUrl,
            DurationSeconds = Math.Round(Wave.SelEnd - Wave.SelStart, 1), Words = words,
            Transcript = string.Join(" ", words.Select(w => w.Text)),
        };
        ClipStore.Instance.Add(nc);
        PromptDialog.Info(this, "Saved", "The trimmed copy is in your clips list.");
    }

    async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (!PromptDialog.Confirm(this, "Replace original?", "The original clip will be overwritten with your selection. This can't be undone.", "Replace")) return;
        StopPlayback();
        var tmp = _clip.File + ".tmp";
        if (await WriteTrimAsync(tmp) == null) return;
        var words = SlicedWords();
        File.Move(tmp, _clip.File, true);
        _clip.Words = words;
        _clip.DurationSeconds = Math.Round(Wave.SelEnd - Wave.SelStart, 1);
        _clip.Transcript = string.Join(" ", words.Select(w => w.Text));
        ClipStore.Instance.Save();
        await LoadAsync();
    }

    async void ToBoard_Click(object sender, RoutedEventArgs e)
    {
        var lib = SoundLibrary.Instance;
        var guess = string.Join(" ", SlicedWords().Take(4).Select(w => w.Text)).Trim(' ', '.', ',', '!', '?');
        var name = PromptDialog.Ask(this, "Add to soundboard", "Sound name", string.IsNullOrWhiteSpace(guess) ? "New clip" : guess);
        if (string.IsNullOrWhiteSpace(name)) return;

        var tmp = Path.Combine(Path.GetTempPath(), "yappest-" + Guid.NewGuid().ToString("N") + ".mp3");
        if (await WriteTrimAsync(tmp) == null) return;
        var board = lib.Boards.FirstOrDefault(b => b.Id == Settings.Current.LastBoardId) ?? lib.Boards.First();
        var item = await Task.Run(() => lib.AddSound(board, tmp, name.Trim()));
        try { File.Delete(tmp); } catch { }
        if (item != null) PromptDialog.Info(this, "Added to your soundboard", $"“{item.Name}” is now on “{board.Name}”. Give it an icon from the Sounds page.");
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!PromptDialog.Confirm(this, "Delete clip", "Delete this clip and its file?", "Delete")) return;
        StopPlayback();
        ClipStore.Instance.Delete(_clip);
        Close();
    }
}
