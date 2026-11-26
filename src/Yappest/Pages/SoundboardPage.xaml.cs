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
        if (dlg.ShowDialog() == true) _ = ImportPathsAsync(dlg.FileNames);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose folders to import (subfolders are searched too)", Multiselect = true };
        if (dlg.ShowDialog() == true) _ = ImportPathsAsync(dlg.FolderNames);
    }

    CancellationTokenSource? _cts;
    void CancelImport_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); BusyText.Text = "Stopping…"; }

    /// <summary>
    /// Imports files and/or folders. Folders are searched, including every subfolder, for mp3, wav, ogg, mp4, m4v and mov;
    /// files that were imported from the same place before are skipped.
    /// </summary>
    async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (_cts != null) return;                       // one import at a time
        var owner = Window.GetWindow(this);
        var board = _board ?? _lib.Boards.First();
        var cts = _cts = new CancellationTokenSource();

        Busy.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Collapsed;
        BusyText.Text = "Looking for sounds…";

        var direct = new List<string>();
        var found = new List<string>();
        int folderCount = 0, subfolders = 0;
        await Task.Run(() =>
        {
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    folderCount++;
                    found.AddRange(SoundLibrary.FindSounds(p));
                    try { subfolders += Directory.EnumerateDirectories(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Count(); } catch { }
                }
                else if (File.Exists(p) && SoundLibrary.IsSupported(p)) direct.Add(p);
            }
        });

        var already = _lib.ImportedSources();
        var fromFolders = found.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !direct.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var fresh = fromFolders.Where(f => !already.Contains(f)).ToList();
        int skippedDuplicates = fromFolders.Count - fresh.Count;
        var queue = direct.Concat(fresh).ToList();

        if (queue.Count == 0)
        {
            Busy.Visibility = Visibility.Collapsed; _cts = null;
            if (folderCount > 0)
                PromptDialog.Info(owner, "Nothing new to add",
                    fromFolders.Count == 0 ? "No mp3, wav, ogg, mp4, m4v or mov files were found in that folder or its subfolders."
                                           : $"All {fromFolders.Count} sounds in there are already in your soundboard.");
            return;
        }

        // folders can hold thousands of files: say what's about to happen before doing it
        if (folderCount > 0)
        {
            Busy.Visibility = Visibility.Collapsed;
            var where = folderCount == 1 ? "that folder" : $"those {folderCount} folders";
            var sub = subfolders > 0 ? $" (searched {subfolders} subfolder{(subfolders == 1 ? "" : "s")} too)" : "";
            var dup = skippedDuplicates > 0 ? $" {skippedDuplicates} that you've already imported will be skipped." : "";
            bool big = queue.Count > 200;
            if (!PromptDialog.Confirm(owner, $"Add {queue.Count} sound{(queue.Count == 1 ? "" : "s")}?",
                    $"Found {queue.Count} sounds in {where}{sub}. They'll be copied into “{board.Name}”, named after their files.{dup}" +
                    (big ? " That's a lot, so it may take a while. You can stop it at any time." : ""), "Add them"))
            { _cts = null; return; }
            Busy.Visibility = Visibility.Visible;
        }

        CancelBtn.Visibility = Visibility.Visible;
        int added = 0, failed = 0, n = 0;
        var failedNames = new List<string>();
        foreach (var f in queue)
        {
            if (cts.IsCancellationRequested) break;
            BusyText.Text = $"Adding {++n} of {queue.Count}…\n{Path.GetFileName(f)}";
            var item = await Task.Run(() => _lib.AddSound(board, f, null, save: false));
            if (item == null) { failed++; if (failedNames.Count < 5) failedNames.Add(Path.GetFileName(f)); } else added++;
        }
        if (added > 0) _lib.Save();                     // one save for the whole batch

        bool cancelled = cts.IsCancellationRequested;
        _cts = null;
        Busy.Visibility = Visibility.Collapsed;
        UpdateEmpty();

        if (failed > 0 || cancelled || folderCount > 0)
        {
            var lines = new List<string> { $"Added {added} sound{(added == 1 ? "" : "s")} to “{board.Name}”." };
            if (cancelled) lines.Add($"Stopped early, {queue.Count - n} not processed.");
            if (failed > 0) lines.Add($"{failed} couldn't be read as audio ({string.Join(", ", failedNames)}{(failed > failedNames.Count ? ", …" : "")}). For videos, check they have an audio track.");
            PromptDialog.Info(owner, cancelled ? "Import stopped" : "Import finished", string.Join(" ", lines));
        }
    }

    void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = ImportPathsAsync(files);
    }
}
