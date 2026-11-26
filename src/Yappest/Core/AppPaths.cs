using System.IO;

namespace Yappest.Core;

/// <summary>All on-disk locations used by Yappest.</summary>
public static class AppPaths
{
    /// <summary>Set YAPPEST_DATA to keep a separate profile (used for development and testing).</summary>
    public static readonly string Root = Environment.GetEnvironmentVariable("YAPPEST_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yappest");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string ClipsDbFile => Path.Combine(Root, "clips.json");
    public static string SoundsDir => Path.Combine(Root, "sounds");
    public static string IconsDir => Path.Combine(Root, "icons");
    public static string ModelsDir => Path.Combine(Root, "models");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string LogFile => Path.Combine(Root, "yappest.log");

    public static string DefaultClipsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Yappest", "Clips");

    public static string AppDir => AppContext.BaseDirectory;
    public static string AppSound(string name) => Path.Combine(AppDir, "appsounds", name);
    public static string VendorDir => Path.Combine(AppDir, "vendor");
    public static string StreamDeckPluginSource => Path.Combine(AppDir, "streamdeck", "com.yappest.sdPlugin");

    static AppPaths()
    {
        foreach (var d in new[] { Root, SoundsDir, IconsDir, ModelsDir, CacheDir })
            Directory.CreateDirectory(d);
    }

    public static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
