using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Yappest.UI;

/// <summary>
/// Click-through, never-activating, always-on-top toast in the top right corner.
/// Works over windowed, borderless and "fullscreen optimized" games. Games that take true exclusive
/// fullscreen own the display and nothing but an in-game overlay can draw over them.
/// </summary>
public partial class ToastWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint flags; }

    static ToastWindow? _open;

    readonly string _text;
    DispatcherTimer? _keepOnTop;
    IntPtr _hwnd;

    public ToastWindow(string text)
    {
        InitializeComponent();
        _text = text;
        Line1.Text = text;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    public static void ShowClipToast(string text)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _open?.Close();
            var t = new ToastWindow(text);
            _open = t;
            t.Show();
        });
    }

    /// <summary>Dev helper: renders both text states of the toast to PNG files without showing a window.</summary>
    public static void RenderPreview(string text, string pathPrefix)
    {
        var t = new ToastWindow(text);
        var root = (FrameworkElement)t.Content;
        root.Measure(new Size(600, 200));
        root.Arrange(new Rect(0, 0, root.DesiredSize.Width, root.DesiredSize.Height));
        root.UpdateLayout();
        void Snap(string suffix)
        {
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.DesiredSize.Width * 2, (int)root.DesiredSize.Height * 2, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var f = System.IO.File.Create(pathPrefix + suffix + ".png");
            enc.Save(f);
        }
        Snap("_1");
        t.Line1.Opacity = 0; t.Line2.Opacity = 1;
        root.UpdateLayout();
        Snap("_2");
    }

    void OnSourceInitialized(object? s, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
        Reposition();
    }

    /// <summary>Top right of the monitor the foreground window (usually the game) is on.</summary>
    void Reposition()
    {
        var fg = GetForegroundWindow();
        var mon = MonitorFromWindow(fg != IntPtr.Zero ? fg : _hwnd, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        UpdateLayout();
        Left = mi.rcMonitor.R / scale - ActualWidth - 10;
        Top = mi.rcMonitor.T / scale + 24;
    }

    void OnLoaded(object? s, RoutedEventArgs e)
    {
        Reposition();
        Left += 0;
        AssertTopmost();

        // keep winning the z-order fight against borderless-fullscreen games
        _keepOnTop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _keepOnTop.Tick += (_, _) => AssertTopmost();
        _keepOnTop.Start();

        double w = ActualWidth + 40;
        Slide.X = w;
        Opacity = 1;

        var slideIn = new DoubleAnimation(w, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Slide.BeginAnimation(TranslateTransform.XProperty, slideIn);

        // after 3 s: crossfade the text to "Edit in Yappest"
        var crossTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3420) };
        crossTimer.Tick += (_, _) =>
        {
            crossTimer.Stop();
            Line1.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280)));
            Line2.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = TimeSpan.FromMilliseconds(180) });
        };
        crossTimer.Start();

        // then fade the whole toast out
        var outTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3420 + 2300) };
        outTimer.Tick += (_, _) =>
        {
            outTimer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        outTimer.Start();
    }

    void AssertTopmost()
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        _keepOnTop?.Stop();
        if (_open == this) _open = null;
        base.OnClosed(e);
    }
}
