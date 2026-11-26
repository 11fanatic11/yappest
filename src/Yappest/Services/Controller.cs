using System.Windows;
using Yappest.Audio;
using Yappest.Core;
using Yappest.UI;

namespace Yappest.Services;

/// <summary>Everything the UI, hotkeys, tray and Stream Deck can ask Yappest to do.</summary>
public static class Controller
{
    static DateTime _lastClip = DateTime.MinValue;

    public static event Action<ClipItem>? ClipCreated;
    public static event Action? ClipFailed;

    public static void PlaySound(string id)
    {
        var item = SoundLibrary.Instance.Find(id);
        if (item != null) PlaySound(item);
    }

    public static void PlaySound(SoundItem item) => Task.Run(() =>
    {
        // a second press while it is playing stops it
        if (AudioEngine.Instance.IsSoundPlaying(item.Id))
        {
            AudioEngine.Instance.StopSound(item.Id);
            return;
        }
        AudioEngine.Instance.PlaySound(item);
    });

    public static void StopSounds() => AudioEngine.Instance.StopAllSounds();

    public static void ToggleMute() => AudioEngine.Instance.ToggleMute();

    public static void SetMute(bool m) => AudioEngine.Instance.SetMuted(m);

    public static void ToggleVoiceChanger()
    {
        var s = Settings.Current;
        s.VoiceChangerEnabled = !s.VoiceChangerEnabled;
        Settings.Commit();
    }

    public static void SelectEffect(string id)
    {
        var s = Settings.Current;
        if (!EffectCatalog.All.Any(e => e.Id == id)) return;
        s.ActiveEffect = id;
        s.VoiceChangerEnabled = true;
        Settings.Commit();
    }

    public static void ToggleHearMyself()
    {
        var s = Settings.Current;
        s.HearMyself = !s.HearMyself;
        Settings.Commit();
    }

    public static string ClipLengthText(int seconds) => seconds >= 60 ? "Clipped the last minute" : $"Clipped the last {seconds}s";

    public static void Clip()
    {
        if ((DateTime.UtcNow - _lastClip).TotalMilliseconds < 800) return;
        _lastClip = DateTime.UtcNow;
        var s = Settings.Current;
        int secs = Math.Clamp(s.ClipSeconds, 5, 60);

        // instant feedback: sound goes to your output only (unless you opted in to sending it on the input)
        AudioEngine.Instance.PlayAppSound("audclip.wav", s.ClipSoundToInput, s.ClipSoundVolume);
        if (s.ClipToast) ToastWindow.ShowClipToast(ClipLengthText(secs));

        Task.Run(async () =>
        {
            var clip = await ClipRecorder.Instance.SaveClipAsync(secs);
            if (clip == null) { ClipFailed?.Invoke(); return; }
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ClipStore.Instance.Add(clip);
                ClipCreated?.Invoke(clip);
            });
        });
    }

    public static void RebindHotkeys()
    {
        var s = Settings.Current;
        HotkeyService.Instance.Rebind(new (string, string, Action)[]
        {
            ("clip", s.ClipHotkey, Clip),
            ("mute", s.MuteHotkey, ToggleMute),
            ("stop", s.StopSoundsHotkey, StopSounds),
        });
    }

    public static void ShowMainWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var app = (App)System.Windows.Application.Current;
            app.ShowMain();
        });
    }

    public static void ShowClips(ClipItem? clip = null)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var app = (App)System.Windows.Application.Current;
            app.ShowMain("clips", clip);
        });
    }
}
