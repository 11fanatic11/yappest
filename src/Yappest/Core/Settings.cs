using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yappest.Core;

public sealed class Settings
{
    // Appearance
    public string AccentColor { get; set; } = "#FCC603";

    // Devices (MMDevice IDs; null = Windows default)
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? SystemAudioDeviceId { get; set; }

    // Mic / voice
    public bool MicMuted { get; set; }
    public double MicVolume { get; set; } = 1.0;
    public bool VoiceChangerEnabled { get; set; }
    public string ActiveEffect { get; set; } = "pitch";          // pitch | barrier
    public bool HearMyself { get; set; }

    /// <summary>Per-voice settings, keyed "voiceId.param" (see EffectCatalog). Missing keys use the voice's default.</summary>
    public Dictionary<string, double> FxParams { get; set; } = new();

    // legacy fields from the first beta, migrated into FxParams on load
    public double PitchSemitones { get; set; } = 4;

    // Sound Barrier effect (all 0..100)
    public double BarrierDrive { get; set; } = 60;
    public double BarrierGrit { get; set; } = 45;
    public double BarrierBreakPoint { get; set; } = 55;
    public double BarrierCutout { get; set; } = 60;

    // Soundboard
    public double SoundboardVolume { get; set; } = 1.0;          // into Yappest Input
    public double MonitorVolume { get; set; } = 0.8;             // into your headphones
    public string? LastBoardId { get; set; }

    // Clipping
    public int ClipSeconds { get; set; } = 30;                   // 5..60
    public bool ClipIncludeMic { get; set; } = true;
    public bool ClipIncludeSystem { get; set; } = true;
    public bool ClipSoundToInput { get; set; } = false;
    public double ClipSoundVolume { get; set; } = 0.8;
    public bool ClipToast { get; set; } = true;
    public string ClipsFolder { get; set; } = AppPaths.DefaultClipsDir;

    // Hotkeys ("Ctrl+Alt+C"); empty = unbound
    public string ClipHotkey { get; set; } = "Ctrl+Alt+C";
    public string MuteHotkey { get; set; } = "Ctrl+Alt+M";
    public string StopSoundsHotkey { get; set; } = "Ctrl+Alt+S";

    // App behaviour
    public bool RunOnStartup { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool SetupComplete { get; set; }
    public int SetupVersion { get; set; }          // bumped once the admin step (virtual mic) has succeeded
    public int SetupAttempts { get; set; }

    // Integrations
    public bool DiscordRpc { get; set; } = true;
    public string DiscordClientId { get; set; } = "";   // Discord Rich Presence is parked for now (DiscordService.cs is not started)
    public string VoicemodApiKey { get; set; } = "";
    public bool AutoCheckUpdates { get; set; } = true;   // look for a newer version when Yappest starts (always optional to install)
    public string SkippedUpdateVersion { get; set; } = "";
    public bool StreamDeckEnabled { get; set; } = true;
    public string WhisperModel { get; set; } = "base";

    // ---- persistence ----
    [JsonIgnore] public static Settings Current { get; private set; } = new();
    public static event Action? Changed;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static CancellationTokenSource? _saveCts;

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
            var s = Current;
            s.FxParams.TryAdd("pitch.semitones", s.PitchSemitones);
            s.FxParams.TryAdd("barrier.drive", s.BarrierDrive);
            s.FxParams.TryAdd("barrier.grit", s.BarrierGrit);
            s.FxParams.TryAdd("barrier.breakpoint", s.BarrierBreakPoint);
            s.FxParams.TryAdd("barrier.cutout", s.BarrierCutout);
        }
        catch (Exception ex) { Log.Error("settings load", ex); Current = new(); }
    }

    /// <summary>Raise Changed and persist shortly after (debounced).</summary>
    public static void Commit()
    {
        Changed?.Invoke();
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            SaveNow();
        });
    }

    public static void SaveNow()
    {
        try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, Json)); }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }
}
