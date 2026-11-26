using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>Global hotkeys that work while a game has focus.</summary>
public sealed class HotkeyService : IDisposable
{
    public static HotkeyService Instance { get; } = new();

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CTRL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    HwndSource? _source;
    readonly Dictionary<int, Action> _actions = new();
    readonly Dictionary<string, string> _failures = new();
    int _nextId = 1;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    public void Init()
    {
        var p = new HwndSourceParameters("YappestHotkeys") { Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch (Exception ex) { Log.Error("hotkey action", ex); }
        }
        return IntPtr.Zero;
    }

    /// <summary>Returns error text for the given binding name or null when it registered fine.</summary>
    public string? Failure(string name) => _failures.TryGetValue(name, out var f) ? f : null;

    public void Rebind(IEnumerable<(string name, string combo, Action action)> bindings)
    {
        if (_source == null) return;
        foreach (var id in _actions.Keys.ToList()) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _failures.Clear();
        foreach (var (name, combo, action) in bindings)
        {
            if (string.IsNullOrWhiteSpace(combo)) continue;
            if (!TryParse(combo, out uint mods, out uint vk)) { _failures[name] = "Not a valid shortcut"; continue; }
            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk)) _actions[id] = action;
            else _failures[name] = "Another app is already using this shortcut";
        }
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CTRL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Key>(raw, true, out var key) && !Enum.TryParse(NormalizeDigit(raw), true, out key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    static string NormalizeDigit(string s) => s.Length == 1 && char.IsDigit(s[0]) ? "D" + s : s;

    /// <summary>Build "Ctrl+Alt+C" from a key event; null if only modifiers are held.</summary>
    public static string? Format(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var k = key.ToString();
        if (k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1])) k = k[1].ToString();
        parts.Add(k);
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        if (_source != null)
        {
            foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
            _source.Dispose();
        }
    }
}
