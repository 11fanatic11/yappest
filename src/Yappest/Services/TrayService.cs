using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.Services;

/// <summary>Notification-area icon: lives in the hidden icons flyout when Yappest runs in the background.</summary>
public sealed class TrayService : IDisposable
{
    public static TrayService Instance { get; } = new();

    NotifyIcon? _icon;
    ToolStripMenuItem? _mute, _vc, _clip;

    public void Init()
    {
        var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/yappest.ico"));
        Icon icon;
        using (var st = res.Stream) icon = new Icon(st);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new DarkRenderer() };
        menu.Items.Add(new ToolStripMenuItem("Open Yappest", null, (_, _) => Controller.ShowMainWindow()) { Font = new Font(SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        _mute = new ToolStripMenuItem("Mute microphone", null, (_, _) => Controller.ToggleMute());
        _vc = new ToolStripMenuItem("Voice changer", null, (_, _) => Controller.ToggleVoiceChanger());
        _clip = new ToolStripMenuItem("Clip last seconds", null, (_, _) => Controller.Clip());
        menu.Items.Add(_mute);
        menu.Items.Add(_vc);
        menu.Items.Add(_clip);
        menu.Items.Add(new ToolStripMenuItem("Stop all sounds", null, (_, _) => Controller.StopSounds()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit Yappest", null, (_, _) => ((App)System.Windows.Application.Current).Quit()));
        menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon { Icon = icon, Text = "Yappest", Visible = true, ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Controller.ShowMainWindow(); };
        Settings.Changed += Refresh;
    }

    void Refresh()
    {
        var s = Settings.Current;
        if (_mute != null) _mute.Text = s.MicMuted ? "Unmute microphone" : "Mute microphone";
        if (_vc != null) _vc.Checked = s.VoiceChangerEnabled;
        if (_clip != null) _clip.Text = s.ClipSeconds >= 60 ? "Clip last minute" : $"Clip last {s.ClipSeconds}s";
        if (_icon != null) _icon.Text = s.MicMuted ? "Yappest (mic muted)" : "Yappest";
    }

    public void Notify(string title, string text)
    {
        _icon?.ShowBalloonTip(3000, title, text, ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
    }

    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = System.Drawing.Color.FromArgb(0xEE, 0xEE, 0xF0);
            base.OnRenderItemText(e);
        }
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B);
        public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(0x2C, 0x2C, 0x31);
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(0x2C, 0x2C, 0x31);
        public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x40);
        public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B);
        public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B);
        public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(0x18, 0x18, 0x1B);
        public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x40);
        public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(0x3A, 0x3A, 0x40);
    }
}
