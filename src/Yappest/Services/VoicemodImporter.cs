using System.IO;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>
/// Brings sounds over from a Voicemod install on the same PC.
/// Audio: only plain files in Voicemod's local cache can be copied (no login involved). Many cached files use a protected
/// format, and Voicemod's API has no way to export audio.
/// Names, boards and icons: only available from Voicemod's Control API, which needs the user's own free API key.
/// </summary>
public static class VoicemodImporter
{
    public sealed record Found(string Path, long Bytes);
    public sealed record ScanResult(List<Found> Usable, int Unreadable);

    public sealed record PlannedSound(string Name, string File, byte[]? Icon);
    public sealed record PlannedBoard(string Name, List<PlannedSound> Sounds);
    public sealed record Plan(List<PlannedBoard> Boards, int Unreadable, int NamedFromApi, int ApiSoundsWithoutAudio, string ApiNote)
    {
        public int Total => Boards.Sum(b => b.Sounds.Count);
    }

    static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemodV3", "cache");

    public static bool IsInstalled()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Directory.Exists(Path.Combine(pf, "Voicemod V3")) || Directory.Exists(Path.GetDirectoryName(CacheDir)!);
    }

    /// <summary>Cached sounds that are plain audio files. Protected ones are counted but never decoded.</summary>
    public static ScanResult Scan()
    {
        var usable = new List<Found>();
        int unreadable = 0;
        try
        {
            if (Directory.Exists(CacheDir))
                foreach (var f in Directory.EnumerateFiles(CacheDir, "*.ogg"))
                {
                    var fi = new FileInfo(f);
                    if (fi.Length < 2_000) continue;
                    if (SoundCache.ProbeDuration(f) > 0.05) usable.Add(new Found(f, fi.Length));
                    else unreadable++;
                }
        }
        catch (Exception ex) { Log.Error("voicemod scan", ex); }
        return new ScanResult(usable.OrderBy(f => File.GetLastWriteTimeUtc(f.Path)).ToList(), unreadable);
    }

    public static async Task<Plan> BuildPlanAsync(string? apiKey)
    {
        var scan = await Task.Run(Scan);
        var boards = new List<PlannedBoard>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int named = 0, noAudio = 0;
        string note = "";

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            using var api = new VoicemodApi();
            if (await api.ConnectAsync(apiKey.Trim()))
            {
                var vmBoards = await api.GetSoundboardsAsync();
                foreach (var vb in vmBoards)
                {
                    var planned = new List<PlannedSound>();
                    foreach (var s in vb.Sounds)
                    {
                        // cached files carry the sound id in their name when Voicemod stored them as plain audio
                        var hit = s.Id.Length >= 8 ? scan.Usable.FirstOrDefault(f => System.IO.Path.GetFileName(f.Path).Contains(s.Id, StringComparison.OrdinalIgnoreCase)) : null;
                        if (hit == null) { noAudio++; continue; }
                        var icon = await api.GetBitmapAsync(s.Id);
                        planned.Add(new PlannedSound(s.Name, hit.Path, icon));
                        used.Add(hit.Path);
                        named++;
                    }
                    if (planned.Count > 0) boards.Add(new PlannedBoard(vb.Name, planned));
                }
                note = $"Voicemod listed {vmBoards.Sum(b => b.Sounds.Count)} sounds in {vmBoards.Count} soundboards.";
            }
            else note = api.Error ?? "Couldn't reach Voicemod's API.";
        }

        // plain cached files the API couldn't name (or no API key): generic names
        var rest = scan.Usable.Where(f => !used.Contains(f.Path)).ToList();
        if (rest.Count > 0)
        {
            int i = 0;
            boards.Add(new PlannedBoard("Imported from Voicemod", rest.Select(f => new PlannedSound($"Voicemod sound {++i}", f.Path, null)).ToList()));
        }
        return new Plan(boards, scan.Unreadable, named, noAudio, note);
    }

    public static int Execute(Plan plan)
    {
        int n = 0;
        foreach (var pb in plan.Boards)
        {
            var board = SoundLibrary.Instance.AddBoard(pb.Name);
            foreach (var ps in pb.Sounds)
            {
                var item = SoundLibrary.Instance.AddSound(board, ps.File, ps.Name);
                if (item == null) continue;
                n++;
                if (ps.Icon != null)
                {
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yappest-icon-" + Guid.NewGuid().ToString("N"));
                    try { File.WriteAllBytes(tmp, ps.Icon); SoundLibrary.Instance.SetIcon(item, tmp); }
                    finally { try { File.Delete(tmp); } catch { } }
                }
            }
        }
        return n;
    }

    public static string Describe(Plan p)
    {
        var parts = new List<string>();
        if (p.Total == 0) parts.Add("There's nothing Yappest can copy: every cached Voicemod sound is in a protected format.");
        else parts.Add($"Yappest can copy {p.Total} sounds into {p.Boards.Count} board(s).");
        if (p.NamedFromApi > 0) parts.Add($"{p.NamedFromApi} keep their Voicemod names and icons.");
        if (p.ApiSoundsWithoutAudio > 0) parts.Add($"{p.ApiSoundsWithoutAudio} more are named in Voicemod but their audio isn't available as a file: Voicemod doesn't let other apps export audio.");
        if (p.Unreadable > 0) parts.Add($"{p.Unreadable} cached files use a protected format and are skipped.");
        if (p.ApiNote.Length > 0) parts.Add(p.ApiNote);
        return string.Join(" ", parts);
    }
}
