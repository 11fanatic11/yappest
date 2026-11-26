using Microsoft.Win32;
using Yappest.Core;

namespace Yappest.Services;

public static class StartupService
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Yappest";

    public static void Apply(bool enabled)
    {
        // never register a development build (bin\Debug) or a test profile as a startup app
        if (Environment.GetEnvironmentVariable("YAPPEST_DATA") != null) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                k.SetValue(ValueName, $"\"{exe}\" --background");
            }
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Error("startup registry", ex); }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
