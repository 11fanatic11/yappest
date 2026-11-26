using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Yappest.Core;

namespace Yappest.Services;

public sealed record UpdateInfo(Version Version, string Notes, string Url, string Sha256, long Size);

public enum UpdateStatus { UpToDate, Available, Failed }

public sealed record UpdateCheck(UpdateStatus Status, UpdateInfo? Info = null, string? Error = null);

/// <summary>
/// Optional updates. Yappest reads a small update.json (served from yappest.fanaticdev.xyz, with the repo's raw file as a backup).
/// A newer version only ever produces a prompt: nothing downloads or installs until the user says so.
/// </summary>
public static class Updater
{
    /// <summary>Set YAPPEST_UPDATE_URL to test against another feed (development only).</summary>
    static string[] FeedUrls => Environment.GetEnvironmentVariable("YAPPEST_UPDATE_URL") is { Length: > 0 } u
        ? new[] { u }
        : new[]
        {
            "https://yappest.fanaticdev.xyz/update.json",
            "https://raw.githubusercontent.com/11fanatic11/yappest.fanaticdev.xyz/main/update.json",
        };

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentText => Current.ToString(3);

    static bool DevFeed => Environment.GetEnvironmentVariable("YAPPEST_UPDATE_URL") != null;

    /// <summary>Only our own hosts may supply an installer to download.</summary>
    static bool TrustedDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (DevFeed && u.IsLoopback) return true;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        var h = u.Host.ToLowerInvariant();
        return h == "github.com" || h.EndsWith(".githubusercontent.com") || h == "yappest.fanaticdev.xyz" || h.EndsWith(".fanaticdev.xyz");
    }

    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Yappest/" + CurrentText);
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        string? lastError = null;
        foreach (var feed in FeedUrls)
        {
            try
            {
                var json = await http.GetStringAsync(feed + (feed.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks, ct);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var ver = r.GetProperty("version").GetString() ?? "";
                if (!Version.TryParse(ver, out var latest)) { lastError = "The update feed has an unreadable version."; continue; }
                if (latest <= Current) return new UpdateCheck(UpdateStatus.UpToDate);

                var url = r.GetProperty("url").GetString() ?? "";
                if (!TrustedDownload(url)) { lastError = "The update feed points somewhere Yappest doesn't trust."; continue; }
                var notes = r.TryGetProperty("notes", out var n)
                    ? n.ValueKind == JsonValueKind.Array ? string.Join("\n", n.EnumerateArray().Select(x => "• " + x.GetString())) : n.GetString() ?? ""
                    : "";
                var sha = r.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
                long size = r.TryGetProperty("size", out var z) && z.TryGetInt64(out var zz) ? zz : 0;
                return new UpdateCheck(UpdateStatus.Available, new UpdateInfo(latest, notes, url, sha, size));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { lastError = ex.Message; }
        }
        return new UpdateCheck(UpdateStatus.Failed, null, lastError ?? "No update feed answered.");
    }

    /// <summary>Downloads and verifies the installer. Returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Yappest-Setup-{info.Version}.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Yappest/" + CurrentText);
        using var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? info.Size;

        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        }

        var actual = Convert.ToHexString(sha.Hash!);
        if (info.Sha256.Length > 0 && !actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException("The download didn't match its checksum, so it was thrown away.");
        }
        return path;
    }

    /// <summary>Runs the installer after this app has exited (so it can replace files), then starts the new version.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        if (Environment.GetEnvironmentVariable("YAPPEST_UPDATE_DRYRUN") != null) { Log.Info("update dry run: would launch " + installerPath); return; }
        // wait a moment for this process to release its single-instance lock, then install quietly and relaunch
        var args = $"/c ping -n 3 127.0.0.1 >nul & \"{installerPath}\" /SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1";
        Process.Start(new ProcessStartInfo("cmd.exe", args) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }
}
