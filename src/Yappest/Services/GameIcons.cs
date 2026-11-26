using System.IO;
using System.Net.Http;
using System.Text.Json;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>
/// Finds a picture for a game and keeps it on disk. Discord's detectable-games list no longer carries icon hashes,
/// but its public per-application endpoint (/applications/{id}/rpc) still returns one, so that's tried first.
/// If the game isn't in Discord's list, the program's own icon is used.
/// </summary>
public static class GameIcons
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static string Dir => Path.Combine(AppPaths.CacheDir, "gameicons");

    static GameIcons() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Yappest/0.2");

    /// <summary>Local PNG path for the game, or null when nothing could be found.</summary>
    public static async Task<string?> ResolveAsync(GameInfo g)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var key = AppPaths.SafeFileName(g.Id ?? g.ProcessName);
            if (key.Length == 0) return null;
            var path = Path.Combine(Dir, key + ".png");
            if (File.Exists(path)) return path;

            if (g.Id != null)
            {
                try
                {
                    var json = await Http.GetStringAsync($"https://discord.com/api/v10/applications/{g.Id}/rpc");
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String && ic.GetString() is { Length: > 0 } hash)
                    {
                        var bytes = await Http.GetByteArrayAsync($"https://cdn.discordapp.com/app-icons/{g.Id}/{hash}.png?size=256");
                        await File.WriteAllBytesAsync(path, bytes);
                        return path;
                    }
                }
                catch (Exception ex) { Log.Warn($"game icon for {g.Name}: {ex.Message}"); }
            }

            if (g.ExePath != null && File.Exists(g.ExePath))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(g.ExePath);
                if (icon != null)
                {
                    using var bmp = icon.ToBitmap();
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return path;
                }
            }
        }
        catch (Exception ex) { Log.Warn("game icon: " + ex.Message); }
        return null;
    }

    /// <summary>Give clips saved before this existed (or while offline) their game icon.</summary>
    public static async Task BackfillAsync(IEnumerable<ClipItem> clips)
    {
        foreach (var c in clips.ToList())
        {
            if (!string.IsNullOrEmpty(c.GameIconUrl) && (c.GameIconUrl.StartsWith("http") ? false : File.Exists(c.GameIconUrl))) continue;
            if (c.Game is "" or "Desktop") continue;
            var info = GameDetector.Instance.FindByName(c.Game);
            if (info == null) continue;
            var p = await ResolveAsync(info);
            if (p != null) System.Windows.Application.Current.Dispatcher.Invoke(() => { c.GameIconUrl = p; ClipStore.Instance.Save(); });
        }
    }
}
