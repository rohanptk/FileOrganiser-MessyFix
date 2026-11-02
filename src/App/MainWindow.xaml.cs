using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Core;
using Microsoft.VisualBasic.FileIO;
using MessageBox = System.Windows.MessageBox;

namespace App;

public class Row(string path, List<Suggestion> s) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public List<Suggestion> Suggestions { get; } = s;
    public List<string> Options => [.. Suggestions.Select((x, i) => x.Folder)];
    public string Sure => Suggestions.Count == 0 ? "?" : $"{Suggestions[0].Confidence:P0}";
    string _target = s.FirstOrDefault()?.Folder ?? "";
    public string Target { get => _target; set { _target = value; PropertyChanged?.Invoke(this, new(nameof(Target))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly Organiser _o;
    readonly ObservableCollection<Row> _rows = [];
    public event Action? RowsChanged;
    public int Pending => _rows.Count;

    public MainWindow(Organiser o)
    {
        _o = o;
        InitializeComponent();
        Grid.ItemsSource = _rows;
        AutoMoveBox.IsChecked = o.Memory.Settings.AutoMove;
        StartupBox.IsChecked = Startup.IsOn;
        AcceptAllButton.Content = $"Move all ≥ {o.Memory.Settings.AutoMoveThreshold:P0}";
        _rows.CollectionChanged += (_, _) => { RowsChanged?.Invoke(); SetStatus(); };
        Reload();
    }

    void SetStatus(string? msg = null) =>
        Status.Text = (msg != null ? msg + "   ·   " : "") + $"{_rows.Count} stray file(s) left";

    public void Reload()
    {
        _rows.Clear();
        foreach (var f in _o.StrayFiles()) _rows.Add(new Row(f, _o.Suggest(Path.GetFileName(f))));
        if (_rows.Count > 0) Grid.SelectedIndex = 0;
    }

    public void AddPending(string path)
    {
        if (_rows.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        _rows.Add(new Row(path, _o.Suggest(Path.GetFileName(path))));
    }

    List<Row> Selected() => [.. Grid.SelectedItems.Cast<Row>()];

    void SelectNext(int index)
    {
        if (_rows.Count == 0) return;
        Grid.SelectedIndex = Math.Min(index, _rows.Count - 1);
        Grid.ScrollIntoView(Grid.SelectedItem);
        Grid.Focus();
    }

    /// Moves one row. Returns false if the user cancelled or it failed.
    bool MoveRow(Row r, string reason)
    {
        var target = r.Target.Trim();
        if (target.Length == 0) { SetStatus($"Pick a folder for {r.Name}"); return false; }
        try
        {
            var (res, rec) = _o.Move(r.Path, target, reason);
            switch (res)
            {
                case MoveResult.Moved:
                    _rows.Remove(r);
                    SetStatus($"Moved {r.Name} → {Path.GetRelativePath(_o.Root, Path.GetDirectoryName(rec!.To)!)}");
                    return true;
                case MoveResult.Missing:
                    _rows.Remove(r);
                    return true;
                default: // Duplicate
                    if (MessageBox.Show(this, $"An identical copy of {r.Name} is already in {target}.\n\nSend this copy to the Recycle Bin?",
                            "Duplicate", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        return Recycle(r);
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Could not move {r.Name}: {ex.Message}");
            return false;
        }
    }

    bool Recycle(Row r)
    {
        try
        {
            FileSystem.DeleteFile(r.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            _rows.Remove(r);
            SetStatus($"Recycled {r.Name}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            SetStatus($"Could not recycle {r.Name}: {ex.Message}");
            return false;
        }
    }

    void Move_Click(object? s, RoutedEventArgs? e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var idx = Grid.SelectedIndex;
        foreach (var r in sel) MoveRow(r, "manual");
        SelectNext(idx);
    }

    void AcceptAll_Click(object s, RoutedEventArgs e)
    {
        var sure = _rows.Where(r => r.Suggestions.Count > 0 && r.Target == r.Suggestions[0].Folder
                                    && r.Suggestions[0].Confidence >= _o.Memory.Settings.AutoMoveThreshold).ToList();
        if (sure.Count == 0) { SetStatus("No files are above the threshold yet"); return; }
        var preview = string.Join("\n", sure.Take(25).Select(r => $"{r.Name}  →  {r.Target}")) + (sure.Count > 25 ? $"\n… and {sure.Count - 25} more" : "");
        if (MessageBox.Show(this, $"Move {sure.Count} file(s)?\n\n{preview}", "Confirm", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        int n = sure.Count(r => MoveRow(r, "bulk"));
        SetStatus($"Moved {n} file(s)");
    }

    void Browse_Click(object? s, RoutedEventArgs? e)
    {
        var r = Grid.SelectedItem as Row;
        if (r == null) return;
        var start = Path.Combine(_o.Root, r.Target);
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(start) ? start : _o.Root, Title = $"Where should {r.Name} go?" };
        if (dlg.ShowDialog(this) != true) return;
        var rel = Path.GetRelativePath(_o.Root, dlg.FolderName);
        if (rel.StartsWith("..") || Path.IsPathRooted(rel)) { SetStatus("Pick a folder inside Downloads"); return; }
        foreach (var x in Selected()) x.Target = rel;
    }

    void Rule_Click(object? s, RoutedEventArgs? e)
    {
        var r = Grid.SelectedItem as Row;
        if (r == null || r.Target.Trim().Length == 0) { SetStatus("Pick a destination first"); return; }
        var words = string.Join(" ", Tokenizer.Tokens(Path.GetFileNameWithoutExtension(r.Name)));
        var answer = Microsoft.VisualBasic.Interaction.InputBox(
            $"Files whose name contains ALL these words will always go to:\n{r.Target}\n\nRemove the words that change from file to file:",
            "Always send here", words);
        if (answer.Trim().Length == 0) return;
        _o.AddRule(answer.Split(' ', StringSplitOptions.RemoveEmptyEntries), r.Target);
        var idx = Grid.SelectedIndex;
        MoveRow(r, "rule");
        foreach (var x in _rows) { var fresh = _o.Suggest(x.Name); x.Suggestions.Clear(); x.Suggestions.AddRange(fresh); x.Target = fresh.FirstOrDefault()?.Folder ?? x.Target; }
        Grid.Items.Refresh();
        SelectNext(idx);
    }

    void Skip_Click(object? s, RoutedEventArgs? e)
    {
        var idx = Grid.SelectedIndex;
        foreach (var r in Selected()) _rows.Remove(r);
        SelectNext(idx);
    }

    void Recycle_Click(object? s, RoutedEventArgs? e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"Send {sel.Count} file(s) to the Recycle Bin?", "Recycle", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var idx = Grid.SelectedIndex;
        foreach (var r in sel) Recycle(r);
        SelectNext(idx);
    }

    public void UndoLast()
    {
        var rec = _o.Memory.History.LastOrDefault();
        if (rec == null) { SetStatus("Nothing to undo"); return; }
        if (_o.Undo(rec)) { AddPending(rec.From); SetStatus($"Undid move of {Path.GetFileName(rec.From)}"); }
        else SetStatus($"Can't undo: {rec.To} no longer exists");
    }

    void Undo_Click(object s, RoutedEventArgs e) => UndoLast();

    void Archive_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _o.Root, Title = "Pick the finished term folder (e.g. mba@glim\\TERM 3)" };
        if (dlg.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, $"Move\n{dlg.FolderName}\ninto an _ARCHIVE folder next to it?\n\nNothing is deleted; new files will stop being suggested there.",
                "Archive term", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try { SetStatus($"Archived to {_o.ArchiveFolder(dlg.FolderName)}"); Reload(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { SetStatus($"Archive failed: {ex.Message}"); }
    }

    void OpenMemory_Click(object s, RoutedEventArgs e)
    {
        _o.Save();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Memory.DefaultPath}\"") { UseShellExecute = true });
        SetStatus("Edit settings/rules in Notepad, save, then restart the app");
    }

    void Refresh_Click(object s, RoutedEventArgs e) { _o.Rebuild(); Reload(); }

    void AutoMove_Click(object s, RoutedEventArgs e) { _o.Memory.Settings.AutoMove = AutoMoveBox.IsChecked == true; _o.Save(); }

    void Startup_Click(object s, RoutedEventArgs e) => Startup.IsOn = StartupBox.IsChecked == true;

    void Grid_DoubleClick(object s, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is Row r) Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
    }

    void Grid_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBox) { if (e.Key == Key.Enter) { Move_Click(null, null); e.Handled = true; } return; }
        var r = Grid.SelectedItem as Row;
        switch (e.Key)
        {
            case Key.Enter: Move_Click(null, null); break;
            case Key.F: Browse_Click(null, null); break;
            case Key.A: Rule_Click(null, null); break;
            case Key.S: Skip_Click(null, null); break;
            case Key.Delete: Recycle_Click(null, null); break;
            case Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3:
                var i = (e.Key is Key.D1 or Key.NumPad1) ? 0 : (e.Key is Key.D2 or Key.NumPad2) ? 1 : 2;
                if (r != null && i < r.Suggestions.Count) r.Target = r.Suggestions[i].Folder;
                break;
            default: return;
        }
        e.Handled = true;
    }
}
