using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>
/// Yappest Input is VB-Audio's virtual cable under a new name. Windows has no user-mode way to create a
/// virtual microphone, so the driver is installed once (needs admin) and its recording endpoint is renamed.
/// </summary>
public static class VirtualCable
{
    public const string DisplayName = "Yappest Input";

    public enum CableState { Missing, Installed, Renamed }

    public static CableState State()
    {
        using var cap = Devices.FindCableCapture();
        if (cap == null) return CableState.Missing;
        return cap.FriendlyName.Contains(DisplayName, StringComparison.OrdinalIgnoreCase) ? CableState.Renamed : CableState.Installed;
    }

    public static string VendorZip => Path.Combine(AppPaths.VendorDir, "vbcable", "VBCABLE_Driver_Pack43.zip");

    // ------- elevated entry points (the app re-launches itself with --elevated-*) -------

    /// <summary>Starts an elevated copy of Yappest to do a driver task; the user approves the Windows prompt.</summary>
    public static async Task<bool> RunElevatedAsync(string arg)
    {
        try
        {
            // Process.Start blocks while the Windows prompt is on screen, so keep it off the UI thread
            return await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = true, Verb = "runas" };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit();
                return p.ExitCode == 0;
            });
        }
        catch (System.ComponentModel.Win32Exception) { return false; }   // user declined the prompt
    }

    /// <summary>Everything the virtual mic needs, in one admin prompt: install the driver if missing, name it, switch off "Listen".</summary>
    public static int ElevatedSetup()
    {
        using (var probe = Devices.FindCableCapture())
        {
            if (probe == null)
            {
                int rc = ElevatedInstall(configureAfter: false);
                if (rc != 0) return rc;
                for (int i = 0; i < 50; i++)
                {
                    using var d = Devices.FindCableCapture();
                    if (d != null) break;
                    Thread.Sleep(500);
                }
            }
        }
        return ElevatedConfigure();
    }

    /// <summary>True when Windows' "Listen to this device" is ticked on Yappest Input, which plays your mic straight into your headphones.</summary>
    public static bool ListenEnabled()
    {
        try
        {
            using var cap = Devices.FindCableCapture();
            if (cap == null) return false;
            var guid = cap.ID[cap.ID.LastIndexOf('{')..];
            using var k = Registry.LocalMachine.OpenSubKey($@"{MMDevicesCapture}\{guid}\Properties");
            return k?.GetValue(KeyListen) is byte[] b && b.Length >= 10 && (b[8] != 0 || b[9] != 0);
        }
        catch { return false; }
    }

    public static bool NeedsSetup() => State() != CableState.Renamed || ListenEnabled();

    public static int ElevatedInstall() => ElevatedInstall(configureAfter: true);

    /// <summary>Install the bundled VB-Cable driver. Runs elevated. Returns process exit code.</summary>
    static int ElevatedInstall(bool configureAfter)
    {
        try
        {
            // the driver pack is VB-Audio's: use the copy shipped with a private build if there is one,
            // otherwise fetch it from VB-Audio's own download page
            var zip = VendorZip;
            if (!File.Exists(zip))
            {
                zip = Path.Combine(Path.GetTempPath(), "VBCABLE_Driver_Pack43-" + Guid.NewGuid().ToString("N") + ".zip");
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                File.WriteAllBytes(zip, http.GetByteArrayAsync("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip").GetAwaiter().GetResult());
                Log.Info("downloaded the VB-Cable driver pack from vb-audio.com");
            }
            var dir = Path.Combine(Path.GetTempPath(), "yappest-vbcable-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            ZipFile.ExtractToDirectory(zip, dir);
            var setup = Directory.GetFiles(dir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (setup == null) return 3;
            var psi = new ProcessStartInfo(setup, "-i -h") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)! };
            using var p = Process.Start(psi)!;
            p.WaitForExit(120000);
            return configureAfter ? ElevatedConfigure() : 0;
        }
        catch (Exception ex) { Log.Error("vbcable install", ex); return 1; }
    }

    // ------- renaming through the MMDevices registry -------

    const string MMDevicesCapture = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
    const string KeyDesc = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";           // PKEY_Device_DeviceDesc
    const string KeyIfaceName = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";      // PKEY_DeviceInterface_FriendlyName
    const string KeyListen = "{24dbb0fc-9311-4b3d-9cf0-18ff155639d4},1";         // "Listen to this device" on/off (VT_BOOL)

    /// <summary>Rename the cable's recording endpoint to "Yappest Input" and turn off "Listen". Needs admin (HKLM, backup/restore semantics).</summary>
    public static int ElevatedConfigure() => ElevatedRename();

    public static int ElevatedRename()
    {
        try
        {
            EnablePrivilege("SeBackupPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            EnablePrivilege("SeTakeOwnershipPrivilege");

            using var cap = Devices.FindCableCapture();
            if (cap == null) { Log.Error("rename: cable capture endpoint not found"); return 4; }
            // endpoint id looks like {0.0.1.00000000}.{guid}
            var id = cap.ID;
            var guid = id[(id.LastIndexOf('{'))..];
            var path = $@"{MMDevicesCapture}\{guid}\Properties";

            if (!OpenForWrite(path, out var hKey)) { Log.Error("rename: cannot open " + path); return 5; }
            try
            {
                SetString(hKey, KeyDesc, DisplayName);
                // Windows shows "<desc> (<interface>)"; keep the suffix short and ours
                SetString(hKey, KeyIfaceName, "Yappest");
                // "Listen to this device" would play everything on Yappest Input into the headphones: you'd hear yourself all the time
                SetBinary(hKey, KeyListen, new byte[] { 11, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 });
            }
            finally { RegCloseKey(hKey); }

            RestartAudioService();
            Log.Info("cable renamed to " + DisplayName);
            return 0;
        }
        catch (Exception ex) { Log.Error("cable rename", ex); return 1; }
    }

    static void RestartAudioService()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("Audiosrv");
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            sc.Start();
            sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { Log.Warn("audiosrv restart: " + ex.Message); }
    }

    // ---- registry p/invoke with REG_OPTION_BACKUP_RESTORE so the protected MMDevices keys can be edited ----
    const uint REG_OPTION_BACKUP_RESTORE = 4;
    const uint KEY_ALL_ACCESS = 0xF003F;
    static readonly IntPtr HKLM = new(unchecked((int)0x80000002));

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegCreateKeyEx(IntPtr hKey, string subKey, int reserved, string? cls, uint options, uint sam, IntPtr sa, out IntPtr result, out uint disposition);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegSetValueEx(IntPtr hKey, string name, int reserved, uint type, byte[] data, int cb);
    [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr hKey);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES np, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    // Pack=4 matters: the native struct is 16 bytes, an 8-byte-aligned long would make it 24 and the privilege never enables
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

    static bool OpenForWrite(string subKey, out IntPtr hKey)
    {
        int rc = RegCreateKeyEx(HKLM, subKey, 0, null, REG_OPTION_BACKUP_RESTORE, KEY_ALL_ACCESS, IntPtr.Zero, out hKey, out _);
        if (rc != 0) Log.Error($"RegCreateKeyEx failed with Win32 error {rc}");
        return rc == 0;
    }

    static void SetBinary(IntPtr hKey, string name, byte[] data)
    {
        int rc = RegSetValueEx(hKey, name, 0, 3 /* REG_BINARY */, data, data.Length);
        if (rc != 0) throw new InvalidOperationException($"RegSetValueEx {name} failed: {rc}");
    }

    static void SetString(IntPtr hKey, string name, string value)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value + "\0");
        int rc = RegSetValueEx(hKey, name, 0, 1 /* REG_SZ */, bytes, bytes.Length);
        if (rc != 0) throw new InvalidOperationException($"RegSetValueEx {name} failed: {rc}");
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x28 /* ADJUST | QUERY */, out var token)) { Log.Warn("OpenProcessToken failed"); return; }
        if (!LookupPrivilegeValue(null, name, out var luid)) { Log.Warn("LookupPrivilegeValue " + name + " failed"); return; }
        var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* ENABLED */ };
        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        if (err != 0) Log.Warn($"AdjustTokenPrivileges {name}: error {err}");
    }
}
