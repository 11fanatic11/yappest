using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yappest.Services;

namespace Yappest.UI;

/// <summary>
/// Click it, then press the key you want, on its own or with Ctrl, Alt, Shift or Win held first, and let go to set it.
/// The combo shows live while you hold. Esc cancels, Backspace/Delete clears.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    string _value = "";
    string? _candidate;
    public event Action<string>? ValueChanged;

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));   // derived controls don't pick up the implicit TextBox style
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        Width = 190;
        TextAlignment = TextAlignment.Center;
        FontFamily = new System.Windows.Media.FontFamily("Consolas");
        Padding = new Thickness(10, 8, 10, 8);
        ContextMenu = null;
        // a plain click must always be able to start capturing, even if the box already had focus
        PreviewMouseLeftButtonDown += (_, _) => { if (!IsKeyboardFocused) Focus(); };
    }

    public string Value
    {
        get => _value;
        set { _value = value; if (!IsKeyboardFocused) ShowValue(); }
    }

    void ShowValue() => Text = string.IsNullOrEmpty(_value) ? "Not set" : Pretty(_value);

    static string Pretty(string combo) => combo.Replace("+", " + ");

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _candidate = null;
        Text = "Hold your keys…";
        // registered global shortcuts swallow their keys before the app sees them, so pause them while capturing
        HotkeyService.Instance.Rebind(Array.Empty<(string, string, Action)>());
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _candidate = null;
        ShowValue();
        Dispatcher.BeginInvoke(Controller.RebindHotkeys);
    }

    static string ModifierText(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (e.IsRepeat) return;

        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _value = ""; ValueChanged?.Invoke(""); Keyboard.ClearFocus(); return;
        }

        var combo = HotkeyService.Format(key, Keyboard.Modifiers);
        if (combo == null)
        {
            // only modifiers so far: show what's held and wait for the main key
            var held = ModifierText(Keyboard.Modifiers);
            Text = held.Length == 0 ? "Hold your keys…" : held + " + …";
            return;
        }

        // a single key on its own is fine too (F8, a letter, a symbol...); it's committed when you let go
        _candidate = combo;
        Text = Pretty(combo);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        e.Handled = true;
        // the combo is committed when the keys are let go
        if (_candidate != null)
        {
            _value = _candidate;
            var v = _candidate;
            _candidate = null;
            ValueChanged?.Invoke(v);
            Keyboard.ClearFocus();
            return;
        }
        var held = ModifierText(Keyboard.Modifiers);
        if (IsKeyboardFocused) Text = held.Length == 0 ? "Hold your keys…" : held + " + …";
    }
}
