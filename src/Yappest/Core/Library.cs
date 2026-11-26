using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Yappest.Core;

public class SoundItem : INotifyPropertyChanged
{
    string _name = "";
    string? _icon;
    bool _playing;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); Raise(nameof(Initial)); } }
    public string File { get; set; } = "";                       // file name inside SoundsDir
    public string? Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); Raise(nameof(IconPath)); Raise(nameof(HasIcon)); } }
    public double Volume { get; set; } = 1.0;
    public double DurationSeconds { get; set; }
    public string? Source { get; set; }                          // where it was imported from (to skip duplicates on re-import)

    [JsonIgnore] public string IconPath => ImageUtil.IconPath(Icon);
    [JsonIgnore] public bool HasIcon => !string.IsNullOrEmpty(Icon) && System.IO.File.Exists(IconPath);
    [JsonIgnore] public bool Playing { get => _playing; set { _playing = value; Raise(nameof(Playing)); } }
    [JsonIgnore] public string FullPath => Path.Combine(AppPaths.SoundsDir, File);
    [JsonIgnore] public bool FileMissing => !System.IO.File.Exists(FullPath);
    [JsonIgnore] public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class Board : INotifyPropertyChanged
{
    string _name = "Board";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
    public ObservableCollection<SoundItem> Sounds { get; set; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Soundboards + sounds, stored as JSON; audio is copied into the app's own sounds folder.</summary>
public sealed class SoundLibrary
{
    public static SoundLibrary Instance { get; } = new();

    public ObservableCollection<Board> Boards { get; private set; } = new();
    public event Action? Changed;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static readonly string[] AudioExtensions = { ".mp3", ".wav", ".ogg" };
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov" };

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.LibraryFile))
            {
                var boards = JsonSerializer.Deserialize<List<Board>>(File.ReadAllText(AppPaths.LibraryFile));
                if (boards != null) Boards = new ObservableCollection<Board>(boards);
            }
        }
        catch (Exception ex) { Log.Error("library load", ex); }

        if (Boards.Count == 0) Boards.Add(new Board { Name = "My Sounds" });
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.LibraryFile, JsonSerializer.Serialize(Boards.ToList(), Json)); }
        catch (Exception ex) { Log.Error("library save", ex); }
        Changed?.Invoke();
    }

    public Board AddBoard(string name)
    {
        var b = new Board { Name = name };
        Boards.Add(b);
        Save();
        return b;
    }

    public void RemoveBoard(Board b)
    {
        foreach (var s in b.Sounds.ToList()) DeleteFiles(s);
        Boards.Remove(b);
        if (Boards.Count == 0) Boards.Add(new Board { Name = "My Sounds" });
        Save();
    }

    public SoundItem? Find(string id) => Boards.SelectMany(b => b.Sounds).FirstOrDefault(s => s.Id == id);

    public static bool IsSupported(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return AudioExtensions.Contains(e) || VideoExtensions.Contains(e);
    }

    /// <summary>Every sound or video file under a folder, however deep. Unreadable folders and shortcuts/links are skipped.</summary>
    public static List<string> FindSounds(string folder)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,   // no system files, no symlink loops
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", opts)
                .Where(IsSupported)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) { Log.Warn($"scan {folder}: {ex.Message}"); return new(); }
    }

    /// <summary>Source files already imported (so importing the same folder twice doesn't duplicate everything).</summary>
    public HashSet<string> ImportedSources() =>
        Boards.SelectMany(b => b.Sounds).Select(s => s.Source).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copy an audio file into the library (video files get their audio track extracted to mp3).
    /// Returns null when the file can't be read as audio. Pass save:false when adding many and call Save() once at the end.
    /// </summary>
    public SoundItem? AddSound(Board board, string sourcePath, string? name = null, bool save = true)
    {
        string? written = null;
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            string fileName;
            if (VideoExtensions.Contains(ext))
            {
                fileName = Guid.NewGuid().ToString("N") + ".mp3";
                written = Path.Combine(AppPaths.SoundsDir, fileName);
                ExtractAudioToMp3(sourcePath, written);
            }
            else if (AudioExtensions.Contains(ext))
            {
                fileName = Guid.NewGuid().ToString("N") + ext;
                written = Path.Combine(AppPaths.SoundsDir, fileName);
                File.Copy(sourcePath, written, true);
            }
            else return null;

            var item = new SoundItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name!,
                File = fileName,
                Source = sourcePath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ? null : sourcePath,
            };
            item.DurationSeconds = Audio.SoundCache.ProbeDuration(item.FullPath);
            if (item.DurationSeconds <= 0.05)       // not decodable: don't leave a dead tile behind
            {
                TryDelete(written);
                return null;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) board.Sounds.Add(item);
            else dispatcher.Invoke(() => board.Sounds.Add(item));
            if (save) Save();
            return item;
        }
        catch (Exception ex)
        {
            Log.Error("add sound " + sourcePath, ex);
            if (written != null) TryDelete(written);
            return null;
        }
    }

    /// <summary>Point an existing sound at a new audio (or video) file, keeping its name, icon and board.</summary>
    public bool ReplaceFile(SoundItem s, string sourcePath)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            string fileName;
            if (VideoExtensions.Contains(ext))
            {
                fileName = Guid.NewGuid().ToString("N") + ".mp3";
                ExtractAudioToMp3(sourcePath, Path.Combine(AppPaths.SoundsDir, fileName));
            }
            else if (AudioExtensions.Contains(ext))
            {
                fileName = Guid.NewGuid().ToString("N") + ext;
                File.Copy(sourcePath, Path.Combine(AppPaths.SoundsDir, fileName), true);
            }
            else return false;

            var dur = Audio.SoundCache.ProbeDuration(Path.Combine(AppPaths.SoundsDir, fileName));
            if (dur <= 0) { TryDelete(Path.Combine(AppPaths.SoundsDir, fileName)); return false; }
            Audio.SoundCache.Invalidate(s.FullPath);
            TryDelete(s.FullPath);
            s.File = fileName;
            s.DurationSeconds = dur;
            Save();
            return true;
        }
        catch (Exception ex) { Log.Error("replace sound file", ex); return false; }
    }

    public void SetIcon(SoundItem s, string imagePath)
    {
        var icon = ImageUtil.ImportIcon(imagePath);
        if (icon == null) return;
        if (!string.IsNullOrEmpty(s.Icon)) TryDelete(ImageUtil.IconPath(s.Icon));
        s.Icon = icon;
        Save();
    }

    public void ClearIcon(SoundItem s)
    {
        if (!string.IsNullOrEmpty(s.Icon)) TryDelete(ImageUtil.IconPath(s.Icon));
        s.Icon = null;
        Save();
    }

    public void RemoveSound(Board b, SoundItem s)
    {
        b.Sounds.Remove(s);
        DeleteFiles(s);
        Save();
    }

    void DeleteFiles(SoundItem s)
    {
        TryDelete(s.FullPath);
        if (!string.IsNullOrEmpty(s.Icon)) TryDelete(ImageUtil.IconPath(s.Icon));
    }

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    /// <summary>Decode the audio track of a video/audio container with Media Foundation and write an mp3.</summary>
    public static void ExtractAudioToMp3(string source, string destMp3)
    {
        using var reader = new MediaFoundationReader(source);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels > 2)
            sp = new MultiplexingSampleProvider(new[] { sp }, 2);
        var resampled = new WdlResamplingSampleProvider(sp, 44100);
        var wave16 = new SampleToWaveProvider16(resampled);
        using var writer = new LameMP3FileWriter(destMp3, wave16.WaveFormat, 192);
        var buf = new byte[16384];
        int n;
        while ((n = wave16.Read(buf, 0, buf.Length)) > 0) writer.Write(buf, 0, n);
    }
}
