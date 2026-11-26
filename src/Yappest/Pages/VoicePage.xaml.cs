using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;

namespace Yappest.Pages;

public partial class VoicePage : UserControl
{
    bool _loading;
    readonly DispatcherTimer _meter = new() { Interval = TimeSpan.FromMilliseconds(45) };

    sealed class Row
    {
        public required EffectInfo Info;
        public required Border Card, IconBox;
        public required TextBlock Icon, Dot;
        public required StackPanel Params;
        public Dictionary<string, (Slider slider, TextBlock value, ParamSpec spec)> Sliders = new();
    }

    readonly List<Row> _rows = new();

    public VoicePage()
    {
        InitializeComponent();
        BuildRows();
        Loaded += (_, _) => { Sync(); _meter.Start(); };
        Unloaded += (_, _) => _meter.Stop();
        Settings.Changed += () => Dispatcher.BeginInvoke(Sync);
        AudioEngine.Instance.StatusChanged += () => Dispatcher.BeginInvoke(UpdateMicName);
        _meter.Tick += (_, _) => UpdateMeters();
    }

    // ---------- effect list ----------
    void BuildRows()
    {
        foreach (var info in EffectCatalog.All)
        {
            var icon = new TextBlock { Text = info.Glyph, Style = (Style)FindResource("Glyph"), FontSize = 20 };
            var iconBox = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Child = icon };

            var title = new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 };
            var desc = new TextBlock { Text = info.Description, Style = (Style)FindResource("Dim"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            text.Children.Add(title); text.Children.Add(desc);

            var dot = new TextBlock { Style = (Style)FindResource("Glyph"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center };

            var head = new Grid { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(text, 1); Grid.SetColumn(dot, 2);
            head.Children.Add(iconBox); head.Children.Add(text); head.Children.Add(dot);

            var paramsPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 2), Visibility = Visibility.Collapsed };

            var stack = new StackPanel();
            stack.Children.Add(head); stack.Children.Add(paramsPanel);
            var card = new Border
            {
                Child = stack, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(1.5),
            };

            var row = new Row { Info = info, Card = card, IconBox = iconBox, Icon = icon, Dot = dot, Params = paramsPanel };
            foreach (var p in info.Params) AddSlider(row, p);

            var captured = info;
            head.MouseLeftButtonDown += (_, _) => SelectEffect(captured.Id);
            _rows.Add(row);
            FxList.Children.Add(card);
        }
    }

    void AddSlider(Row row, ParamSpec p)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var label = new TextBlock { Text = p.Label, Style = (Style)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = p.Min, Maximum = p.Max, TickFrequency = p.Step, IsSnapToTickEnabled = p.Step >= 1 };
        var value = new TextBlock { TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
        Grid.SetColumn(slider, 1); Grid.SetColumn(value, 2);
        g.Children.Add(label); g.Children.Add(slider); g.Children.Add(value);
        row.Params.Children.Add(g);
        row.Sliders[p.Key] = (slider, value, p);

        slider.ValueChanged += (_, _) =>
        {
            if (_loading || !IsLoaded) return;
            Settings.Current.FxParams[$"{row.Info.Id}.{p.Key}"] = Math.Round(slider.Value, 2);
            value.Text = Format(p, slider.Value);
            Settings.Commit();
        };
    }

    static string Format(ParamSpec p, double v) => p.Unit switch
    {
        "st" => v == 0 ? "off" : $"{v:+0;−0} st",
        "Hz" => $"{v:0} Hz",
        "%" => $"{v:0}%",
        _ => $"{v:0.##}",
    };

    void SelectEffect(string id)
    {
        Settings.Current.ActiveEffect = id;
        Settings.Commit();
    }

    // ---------- state ----------
    void Sync()
    {
        _loading = true;
        var s = Settings.Current;
        VcSwitch.IsChecked = s.VoiceChangerEnabled;

        var accent = (Brush)FindResource("AccentBrush");
        var soft = (Brush)FindResource("AccentSoftBrush");
        var accentFg = (Brush)FindResource("AccentFgBrush");
        foreach (var r in _rows)
        {
            bool sel = r.Info.Id == s.ActiveEffect;
            r.Card.BorderBrush = sel ? accent : (Brush)FindResource("Line");
            r.Card.Background = sel ? soft : (Brush)FindResource("Bg1");
            r.IconBox.Background = sel ? accent : (Brush)FindResource("Bg3");
            r.Icon.Foreground = sel ? accentFg : (Brush)FindResource("TextDim");
            r.Dot.Text = sel ? "" : "";
            r.Dot.Foreground = sel ? accent : (Brush)FindResource("TextFaint");
            r.Params.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (key, (slider, value, spec)) in r.Sliders)
            {
                double v = EffectCatalog.Value(s, r.Info.Id, spec);
                if (Math.Abs(slider.Value - v) > 0.001) slider.Value = v;
                value.Text = Format(spec, v);
            }
        }

        MicVol.Value = s.MicVolume; MicVolValue.Text = $"{s.MicVolume * 100:0}%";
        HearSwitch.IsChecked = s.HearMyself;

        // round mute button
        var red = (Brush)FindResource("Danger");
        MuteCircle.Background = s.MicMuted ? red : (Brush)FindResource("Bg3");
        MuteCircle.BorderBrush = s.MicMuted ? red : (Brush)FindResource("Bg4");
        MuteCircleGlyph.Text = s.MicMuted ? "" : "";
        MuteCircleGlyph.Foreground = s.MicMuted ? Brushes.White : (Brush)FindResource("Text");
        MuteCircleText.Text = s.MicMuted ? "Muted" : "Mic live";
        UpdateMicName();
        _loading = false;
    }

    void UpdateMicName()
    {
        var id = Settings.Current.InputDeviceId;
        using var d = Devices.Get(NAudio.CoreAudioApi.DataFlow.Capture, id);
        MicName.Text = d?.FriendlyName ?? "No microphone found";
    }

    void UpdateMeters()
    {
        if (!IsVisible) return;
        double w = ((FrameworkElement)InFill.Parent).ActualWidth;
        InFill.Width = w * Math.Clamp(Math.Pow(AudioEngine.Instance.InputLevel, 0.5), 0, 1);
        OutFill.Width = w * Math.Clamp(Math.Pow(AudioEngine.Instance.OutputLevel, 0.5), 0, 1);
    }

    void Vc_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Current.VoiceChangerEnabled = VcSwitch.IsChecked == true;
        Settings.Commit();
    }

    void MicVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        Settings.Current.MicVolume = Math.Round(MicVol.Value, 2);
        MicVolValue.Text = $"{Settings.Current.MicVolume * 100:0}%";
        Settings.Commit();
    }

    void Hear_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.Current.HearMyself = HearSwitch.IsChecked == true;
        Settings.Commit();
    }

    void MuteCircle_Click(object sender, RoutedEventArgs e) => Controller.ToggleMute();
}
