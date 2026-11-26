using System.Windows;
using Yappest.Core;
using Yappest.Services;

namespace Yappest.UI;

/// <summary>The optional "a newer Yappest is available" prompt.</summary>
public partial class UpdateWindow : Window
{
    readonly UpdateInfo _info;
    readonly CancellationTokenSource _cts = new();
    bool _busy;

    public UpdateWindow(UpdateInfo info)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _info = info;
        Heading.Text = $"Yappest {info.Version.ToString(3)} is available";
        Sub.Text = $"You're on {Updater.CurrentText}. Updating is up to you.";
        Notes.Text = string.IsNullOrWhiteSpace(info.Notes) ? "Improvements and fixes." : info.Notes;
        if (info.Size > 0) UpdateBtn.Content = $"Update now ({info.Size / 1048576.0:0} MB)";
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    public static void ShowPrompt(UpdateInfo info)
    {
        var w = new UpdateWindow(info);
        w.Show();
        w.Activate();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        _busy = true;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = SkipBtn.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        Status.Text = "Downloading…";
        var barWidth = ((FrameworkElement)Bar.Parent).ActualWidth;
        var progress = new Progress<double>(p => { Bar.Width = barWidth * p; Status.Text = $"Downloading… {p * 100:0}%"; });
        try
        {
            var path = await Updater.DownloadAsync(_info, progress, _cts.Token);
            Status.Text = "Installing. Yappest will close and reopen by itself.";
            Bar.Width = barWidth;
            await Task.Delay(700);
            Updater.LaunchInstaller(path);
            _busy = false;
            ((App)System.Windows.Application.Current).Quit();
        }
        catch (Exception ex)
        {
            Log.Error("update download", ex);
            _busy = false;
            Status.Text = "Couldn't update: " + ex.Message;
            UpdateBtn.Content = "Try again";
            UpdateBtn.IsEnabled = LaterBtn.IsEnabled = SkipBtn.IsEnabled = true;
        }
    }

    void Later_Click(object sender, RoutedEventArgs e) => Close();

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.SkippedUpdateVersion = _info.Version.ToString(3);
        Settings.Commit();
        Close();
    }
}
