using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Yappest.Audio;
using Yappest.Core;

namespace Yappest.UI;

public partial class SoundEditDialog : Window
{
    readonly SoundItem _item;
    readonly Board _board;
    string? _newIconPath;
    bool _clearIcon;

    public SoundEditDialog(SoundItem item, Board board)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _item = item; _board = board;
        NameBox.Text = item.Name;
        VolSlider.Value = item.Volume;
        VolText.Text = $"{item.Volume * 100:0}%";
        BoardBox.ItemsSource = SoundLibrary.Instance.Boards;
        BoardBox.SelectedItem = board;
        ShowIcon(item.HasIcon ? item.IconPath : null);
    }

    void ShowIcon(string? path)
    {
        IconImg.Source = path == null ? null : (System.Windows.Media.ImageSource?)new PathToImageConverter().Convert(path, typeof(System.Windows.Media.ImageSource), null, null!);
        Initial.Text = _item.Initial;
        Initial.Visibility = IconImg.Source == null ? Visibility.Visible : Visibility.Collapsed;
        RemoveIconBtn.IsEnabled = IconImg.Source != null;
    }

    void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose an icon",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.ico",
        };
        if (dlg.ShowDialog() != true) return;
        _newIconPath = dlg.FileName;
        _clearIcon = false;
        ShowIcon(dlg.FileName);
    }

    void RemoveIcon_Click(object sender, RoutedEventArgs e)
    {
        _newIconPath = null; _clearIcon = true;
        ShowIcon(null);
    }

    void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolText != null) VolText.Text = $"{VolSlider.Value * 100:0}%";
    }

    void Preview_Click(object sender, RoutedEventArgs e)
    {
        var temp = new SoundItem { Id = "__edit_preview__", File = _item.File, Volume = VolSlider.Value };
        Task.Run(() => AudioEngine.Instance.PlaySound(temp));
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var lib = SoundLibrary.Instance;
        if (!string.IsNullOrWhiteSpace(NameBox.Text)) _item.Name = NameBox.Text.Trim();
        _item.Volume = Math.Round(VolSlider.Value, 2);
        if (_newIconPath != null) lib.SetIcon(_item, _newIconPath);
        else if (_clearIcon) lib.ClearIcon(_item);

        if (BoardBox.SelectedItem is Board target && target != _board)
        {
            _board.Sounds.Remove(_item);
            target.Sounds.Add(_item);
        }
        lib.Save();
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
