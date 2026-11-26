using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Yappest.UI;

/// <summary>Waveform with draggable trim handles and a playhead.</summary>
public sealed class WaveformView : FrameworkElement
{
    float[] _peaks = Array.Empty<float>();     // 0..1 per column bin

    public double Duration { get; private set; }
    public double SelStart { get; private set; }
    public double SelEnd { get; private set; }
    public double Playhead { get; private set; } = -1;

    public event Action? SelectionChanged;

    enum Drag { None, Start, End, New }
    Drag _drag;
    double _anchor;

    public WaveformView()
    {
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        Focusable = false;
    }

    /// <summary>Samples are 48k stereo interleaved.</summary>
    public void SetAudio(float[] stereo, int rate)
    {
        int frames = stereo.Length / 2;
        Duration = frames / (double)rate;
        const int bins = 1600;
        _peaks = new float[bins];
        for (int b = 0; b < bins; b++)
        {
            int s = (int)((long)b * frames / bins), e = (int)((long)(b + 1) * frames / bins);
            float p = 0;
            for (int i = s; i < e; i++)
            {
                float v = Math.Max(Math.Abs(stereo[i * 2]), Math.Abs(stereo[i * 2 + 1]));
                if (v > p) p = v;
            }
            _peaks[b] = p;
        }
        SelStart = 0; SelEnd = Duration; Playhead = -1;
        InvalidateVisual();
    }

    public void SetSelection(double start, double end, bool raise = true)
    {
        start = Math.Clamp(start, 0, Duration);
        end = Math.Clamp(end, 0, Duration);
        if (end < start) (start, end) = (end, start);
        SelStart = start; SelEnd = end;
        InvalidateVisual();
        if (raise) SelectionChanged?.Invoke();
    }

    public void SetPlayhead(double t) { Playhead = t; InvalidateVisual(); }

    double TimeAt(double x) => Duration <= 0 ? 0 : Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * Duration;
    double XAt(double t) => Duration <= 0 ? 0 : t / Duration * ActualWidth;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Duration <= 0) return;
        double x = e.GetPosition(this).X;
        if (e.ClickCount == 2) { SetSelection(0, Duration); return; }
        if (Math.Abs(x - XAt(SelStart)) < 10) _drag = Drag.Start;
        else if (Math.Abs(x - XAt(SelEnd)) < 10) _drag = Drag.End;
        else { _drag = Drag.New; _anchor = TimeAt(x); SetSelection(_anchor, _anchor); }
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        double x = e.GetPosition(this).X;
        if (!IsMouseCaptured)
        {
            Cursor = Math.Abs(x - XAt(SelStart)) < 10 || Math.Abs(x - XAt(SelEnd)) < 10 ? Cursors.SizeWE : Cursors.IBeam;
            return;
        }
        double t = TimeAt(x);
        switch (_drag)
        {
            case Drag.Start: SetSelection(Math.Min(t, SelEnd - 0.05), SelEnd); break;
            case Drag.End: SetSelection(SelStart, Math.Max(t, SelStart + 0.05)); break;
            case Drag.New: SetSelection(_anchor, t); break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.New && SelEnd - SelStart < 0.05) SetSelection(0, Duration);
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x15)), null, new Rect(0, 0, w, h), 10, 10);
        if (_peaks.Length == 0 || w < 4) return;

        var accent = (Color)System.Windows.Application.Current.Resources["AccentColor"];
        var dim = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x54));
        var on = new SolidColorBrush(accent);
        double mid = h / 2;
        double barW = 2, gap = 1;
        int cols = (int)(w / (barW + gap));
        double selX0 = XAt(SelStart), selX1 = XAt(SelEnd);

        // dim outside, tint inside
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x1E, accent.R, accent.G, accent.B)), null, new Rect(selX0, 0, Math.Max(0, selX1 - selX0), h));

        for (int c = 0; c < cols; c++)
        {
            double x = c * (barW + gap);
            int b0 = c * _peaks.Length / cols, b1 = Math.Max(b0 + 1, (c + 1) * _peaks.Length / cols);
            float p = 0;
            for (int i = b0; i < b1 && i < _peaks.Length; i++) p = Math.Max(p, _peaks[i]);
            double bh = Math.Max(2, Math.Pow(p, 0.7) * (h - 24));
            bool inside = x >= selX0 - 1 && x <= selX1;
            dc.DrawRoundedRectangle(inside ? on : dim, null, new Rect(x, mid - bh / 2, barW, bh), 1, 1);
        }

        // handles
        var handle = new SolidColorBrush(accent);
        foreach (var x in new[] { selX0, selX1 })
        {
            dc.DrawRectangle(handle, null, new Rect(x - 1.5, 0, 3, h));
            dc.DrawRoundedRectangle(handle, null, new Rect(x - 5, mid - 14, 10, 28), 4, 4);
        }

        if (Playhead >= 0)
        {
            double px = XAt(Playhead);
            dc.DrawRectangle(Brushes.White, null, new Rect(px - 1, 0, 2, h));
        }
    }
}
