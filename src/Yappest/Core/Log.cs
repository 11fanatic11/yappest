using System.IO;

namespace Yappest.Core;

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }
}
