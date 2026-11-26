using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Yappest.Audio;
using Yappest.Core;
using Yappest.Services;
using Yappest.UI;

namespace Yappest.Pages;

public partial class SoundboardPage : UserControl
{
    readonly SoundLibrary _lib = SoundLibrary.Instance;
    Board? _board;

    public SoundboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
        _lib.Changed += () => Dispatcher.BeginInvoke(UpdateEmpty);
    }

    // ---------- boards ----------
    void Rebuild()
    {
        _board = _lib.Boards.FirstOrDefault(b => b.Id == Settings.Current.LastBoardId) ?? _lib.Boards.FirstOrDefault();
        Chips.Children.Clear();
        foreach (var b in _lib.Boards)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Chip"),
                GroupName = "boards",
                Tag = b,
                IsChecked = b == _board,
            };
            var tb = new TextBlock { Text = b.Name };
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name") { Source = b });
            rb.Content = tb;
            rb.Checked += (_, _) => SelectBoard(b);
            Chips.Children.Add(rb);
        }
        var add = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "New board", Width = 32, Height = 32 };
        add.Click += (_, _) => NewBoard();
        Chips.Children.Add(add);
        SelectBoard(_board);
    }

    void SelectBoard(Board? b)
    {
        _board = b;
        Tiles.ItemsSource = b?.Sounds;
        if (b != null) { Settings.Current.LastBoardId = b.Id; Settings.Commit(); }
        UpdateEmpty();
    }

    void UpdateEmpty() => Empty.Visibility = _board == null || _board.Sounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void NewBoard()
    {
        var name = PromptDialog.Ask(Window.GetWindow(this), "New board", "Name", "Board " + (_lib.Boards.Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        var b = _lib.AddBoard(name.Trim());
        Settings.Current.LastBoardId = b.Id;
        Rebuild();
    }

    void BoardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_board == null) return;
        var menu = new ContextMenu { PlacementTarget = BoardMenuBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var rename = new MenuItem { Header = "Rename board…" };
        rename.Click += (_, _) =>
        {
            var n = PromptDialog.Ask(Window.GetWindow(this), "Rename board", "Name", _board.Name);
            if (!string.IsNullOrWhiteSpace(n)) { _board.Name = n.Trim(); _lib.Save(); }
        };
        var del = new MenuItem { Header = "Delete board" };
        del.Click += (_, _) =>
        {
            if (PromptDialog.Confirm(Window.GetWindow(this), "Delete board", $"Delete “{_board.Name}” and its {_board.Sounds.Count} sound(s)?", "Delete"))
            {
                _lib.RemoveBoard(_board);
                Settings.Current.LastBoardId = null;
                Rebuild();
            }
        };
        menu.Items.Add(rename);
        menu.Items.Add(del);
        menu.IsOpen = true;
    }

    // ---------- sounds ----------
    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not SoundItem s) return;
        if (!File.Exists(s.FullPath)) { RelinkMissing(s); return; }
        Controller.PlaySound(s);
    }

    /// <summary>The audio file behind a sound has disappeared from disk: say so and offer to pick a replacement.</summary>
    void RelinkMissing(SoundItem s)
    {
        var owner = Window.GetWindow(this);
        if (!PromptDialog.Confirm(owner, "This sound's file is missing",
                $"“{s.Name}” can't play because its audio file is no longer in Yappest's folder. Pick a new audio or video file for it?", "Choose file")) return;
        var dlg = new OpenFileDialog { Title = "Choose audio for " + s.Name, Filter = "Audio and video|*.mp3;*.wav;*.ogg;*.mp4;*.m4v;*.mov" };
        if (dlg.ShowDialog() != true) return;
        if (_lib.ReplaceFile(s, dlg.FileName)) Controller.PlaySound(s);
        else PromptDialog.Info(owner, "Couldn't use that file", "It couldn't be read as audio.");
    }

    void StopAll_Click(object sender, RoutedEventArgs e) => Controller.StopSounds();

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SoundItem s && _board != null)
            new SoundEditDialog(s, _board) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SoundItem s && _board != null)
            if (PromptDialog.Confirm(Window.GetWindow(this), "Remove sound", $"Remove “{s.Name}”?", "Remove"))
                _lib.RemoveSound(_board, s);
    }

    void AddSound_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add sounds",
            Multiselect = true,
            Filter = "Audio and video|*.mp3;*.wav;*.ogg;*.mp4;*.m4v;*.mov|Audio|*.mp3;*.wav;*.ogg|Video (audio is extracted)|*.mp4;*.m4v;*.mov",
        };
        if (dlg.ShowDialog() == true) _ = AddFilesAsync(dlg.FileNames);
    }

    async Task AddFilesAsync(IEnumerable<string> files)
    {
        var board = _board ?? _lib.Boards.First();
        var list = files.Where(SoundLibrary.IsSupported).ToList();
        if (list.Count == 0) return;
        Busy.Visibility = Visibility.Visible;
        int done = 0, failed = 0;
        foreach (var f in list)
        {
            BusyText.Text = list.Count > 1 ? $"Adding {++done} of {list.Count}…" : "Adding…";
            var item = await Task.Run(() => _lib.AddSound(board, f));
            if (item == null) failed++;
        }
        Busy.Visibility = Visibility.Collapsed;
        UpdateEmpty();
        if (failed > 0)
            PromptDialog.Info(Window.GetWindow(this), "Some files couldn't be added", $"{failed} file(s) couldn't be read. If it's a video, make sure it has an audio track.");
    }

    void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = AddFilesAsync(files);
    }
}
