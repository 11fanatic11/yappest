using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Yappest.Core;

namespace Yappest.Services;

public sealed record GameInfo(string Name, string? IconUrl, string ProcessName, string? Id = null, string? ExePath = null);

/// <summary>
/// Works out which game is in the foreground. Uses Discord's public "detectable applications" list
/// (the same database Discord uses for its own game detection), plus a fullscreen-window fallback.
/// </summary>
public sealed class GameDetector : IDisposable
{
    public static GameDetector Instance { get; } = new();

    sealed class Entry { public string Id = ""; public string Name = ""; public string? Icon; }

    readonly Dictionary<string, Entry> _byExe = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string suffix, Entry e)> _bySuffix = new();
    readonly object _gate = new();
    Timer? _timer;
    GameInfo? _current;
    DateTime _currentSeen;
    string _lastKey = "";

    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "yappest", "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "lockapp", "systemsettings", "taskmgr", "devenv", "code", "cmd", "powershell", "windowsterminal",
        "chrome", "msedge", "firefox", "brave", "opera", "discord", "spotify", "claude", "notepad", "streamdeck", "voicemod",
    };

    public event Action<GameInfo?>? GameChanged;

    /// <summary>The game the user is in right now (or was in a few seconds ago).</summary>
    public GameInfo? Current
    {
        get
        {
            lock (_gate)
            {
                if (_current != null && (DateTime.UtcNow - _currentSeen).TotalSeconds < 20) return _current;
                return null;
            }
        }
    }

    public async Task InitAsync()
    {
        var cache = Path.Combine(AppPaths.CacheDir, "detectable.json");
        string? json = null;
        try
        {
            var fi = new FileInfo(cache);
            if (!fi.Exists || (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays > 7)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Yappest/0.1");
                json = await http.GetStringAsync("https://discord.com/api/v10/applications/detectable");
                File.WriteAllText(cache, json);
            }
        }
        catch (Exception ex) { Log.Warn("detectable download: " + ex.Message); }

        try
        {
            if (json == null && File.Exists(cache)) json = File.ReadAllText(cache);
            if (json != null) Parse(json);
        }
        catch (Exception ex) { Log.Error("detectable parse", ex); }

        _timer = new Timer(_ => Poll(), null, 1000, 2000);
    }

    void Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        lock (_gate)
        {
            _byExe.Clear(); _bySuffix.Clear();
            foreach (var app in doc.RootElement.EnumerateArray())
            {
                if (!app.TryGetProperty("executables", out var exes)) continue;
                var entry = new Entry
                {
                    Id = app.GetProperty("id").GetString() ?? "",
                    Name = app.GetProperty("name").GetString() ?? "",
                    Icon = app.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null,
                };
                foreach (var ex in exes.EnumerateArray())
                {
                    if (ex.TryGetProperty("os", out var os) && os.GetString() != "win32") continue;
                    if (ex.TryGetProperty("is_launcher", out var l) && l.ValueKind == JsonValueKind.True) continue;
                    var n = (ex.GetProperty("name").GetString() ?? "").Replace('\\', '/').ToLowerInvariant();
                    if (n.StartsWith(">")) n = n[1..];
                    if (n.Length == 0) continue;
                    if (n.Contains('/')) _bySuffix.Add(("/" + n, entry));
                    else _byExe.TryAdd(n, entry);
                }
            }
        }
        Log.Info($"game list loaded: {_byExe.Count} exes");
    }

    public string? IconUrl(GameInfo g) => g.IconUrl;

    void Poll()
    {
        try
        {
            var info = Probe();
            lock (_gate)
            {
                if (info != null) { _current = info; _currentSeen = DateTime.UtcNow; }
            }
            var key = (info ?? _current)?.Name ?? "";
            var cur = Current;
            var k = cur?.Name ?? "";
            if (k != _lastKey)
            {
                _lastKey = k;
                GameChanged?.Invoke(cur);
            }
        }
        catch (Exception ex) { Log.Warn("game poll: " + ex.Message); }
    }

    /// <summary>Look at the foreground window right now.</summary>
    public GameInfo? Probe()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        string? path = QueryPath(pid);
        if (path == null) return null;

        var exe = Path.GetFileName(path).ToLowerInvariant();
        var procName = Path.GetFileNameWithoutExtension(path);
        var norm = path.Replace('\\', '/').ToLowerInvariant();

        lock (_gate)
        {
            if (_byExe.TryGetValue(exe, out var e) && !Ignore.Contains(procName))
                return ToInfo(e, procName, path);
            foreach (var (suffix, entry) in _bySuffix)
                if (norm.EndsWith(suffix)) return ToInfo(entry, procName, path);
        }

        if (Ignore.Contains(procName)) return null;

        // Fallback: a borderless/fullscreen window from a process we don't know -> treat as a game
        if (IsFullscreen(hwnd))
        {
            string name = procName;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(vi.ProductName)) name = vi.ProductName!.Trim();
                else if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription!.Trim();
            }
            catch { }
            return new GameInfo(name, null, procName, null, path);
        }
        return null;
    }

    static GameInfo ToInfo(Entry e, string proc, string path) =>
        new(e.Name, string.IsNullOrEmpty(e.Icon) ? null : $"https://cdn.discordapp.com/app-icons/{e.Id}/{e.Icon}.png?size=256", proc, e.Id, path);

    /// <summary>Look a game up by its display name (used to give older clips an icon).</summary>
    public GameInfo? FindByName(string name)
    {
        lock (_gate)
        {
            var e = _byExe.Values.FirstOrDefault(x => x.Name == name) ?? _bySuffix.Select(x => x.e).FirstOrDefault(x => x.Name == name);
            return e == null ? null : new GameInfo(e.Name, null, "", e.Id, null);
        }
    }

    // ---------- win32 ----------
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint flags; }

    static string? QueryPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2 /* NEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.L <= mi.rcMonitor.L && r.T <= mi.rcMonitor.T && r.R >= mi.rcMonitor.R && r.B >= mi.rcMonitor.B;
    }

    public void Dispose() => _timer?.Dispose();
}
