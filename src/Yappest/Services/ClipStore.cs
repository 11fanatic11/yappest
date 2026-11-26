using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using Yappest.Core;

namespace Yappest.Services;

public sealed class WordStamp
{
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
}

public sealed class ClipItem : INotifyPropertyChanged
{
    string _transcript = "";
    string _status = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string File { get; set; } = "";                        // full path
    public DateTime Created { get; set; } = DateTime.Now;
    public string Game { get; set; } = "Desktop";
    string? _gameIcon;
    /// <summary>Local PNG path of the game's picture (the name is historical; early builds stored a web address).</summary>
    public string? GameIconUrl { get => _gameIcon; set { _gameIcon = value; Raise(nameof(GameIconUrl)); } }
    public double DurationSeconds { get; set; }
    public string Transcript { get => _transcript; set { _transcript = value; Raise(nameof(Transcript)); Raise(nameof(Preview)); } }
    public List<WordStamp> Words { get; set; } = new();
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); Raise(nameof(Preview)); } }   // "", transcribing, nomodel, failed

    bool _playing;
    [JsonIgnore] public bool IsPlaying { get => _playing; set { _playing = value; Raise(nameof(IsPlaying)); } }

    [JsonIgnore]
    public string Preview => !string.IsNullOrWhiteSpace(Transcript) ? Transcript
        : Status switch
        {
            "transcribing" => "Transcribing…",
            "nomodel" => "Transcript unavailable. Download the speech model in Settings.",
            "failed" => "Couldn't transcribe this clip.",
            _ => "[no words detected]",
        };

    [JsonIgnore] public string Title => $"{Created:MMM d, yyyy}  ·  {Created:h:mm:ss tt}";
    [JsonIgnore] public string DurationText => DurationSeconds >= 60 ? $"{(int)DurationSeconds / 60}:{(int)DurationSeconds % 60:00}" : $"{DurationSeconds:0.#}s";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ClipStore
{
    public static ClipStore Instance { get; } = new();

    public ObservableCollection<ClipItem> Clips { get; } = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ClipsDbFile)) return;
            var list = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(AppPaths.ClipsDbFile)) ?? new();
            foreach (var c in list.Where(c => File.Exists(c.File)).OrderByDescending(c => c.Created))
            {
                if (c.Status == "transcribing") c.Status = "";
                // clips transcribed by older builds can still carry "[no audio]"-style tags as words
                var clean = Transcriber.StripSoundTags(c.Words);
                if (clean.Count != c.Words.Count) { c.Words = clean; c.Transcript = string.Join(" ", clean.Select(w => w.Text)); }
                else if (c.Words.Count == 0 && c.Transcript.TrimStart().StartsWith('[')) c.Transcript = "";
                Clips.Add(c);
            }
        }
        catch (Exception ex) { Log.Error("clips load", ex); }
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.ClipsDbFile, JsonSerializer.Serialize(Clips.ToList(), Json)); }
        catch (Exception ex) { Log.Error("clips save", ex); }
    }

    public void Add(ClipItem c)
    {
        Clips.Insert(0, c);
        Save();
    }

    public void Delete(ClipItem c)
    {
        Clips.Remove(c);
        try { if (File.Exists(c.File)) File.Delete(c.File); } catch { }
        Save();
    }
}
