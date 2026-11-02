using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace App;

static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "DownloadsOrganiser";

    public static bool IsOn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(Name, false);
        }
    }
}

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\DownloadsOrganiser", out var first);
        if (!first) { System.Windows.MessageBox.Show("Downloads Organiser is already running (check the tray)."); return; }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var o = new Organiser();
        var win = new MainWindow(o);
        MoveRecord? lastAuto = null;

        using var tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Downloads Organiser" };
        void UpdateTray() => tray.Text = $"Downloads Organiser – {win.Pending} waiting";
        void Show() { win.Show(); win.WindowState = WindowState.Normal; win.Activate(); }
        win.RowsChanged += UpdateTray;
        UpdateTray();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Show());
        menu.Items.Add("Undo last move", null, (_, _) => win.UndoLast());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Show();
        tray.BalloonTipClicked += (_, _) =>
        {
            // Clicking the "moved" notification undoes that move.
            if (lastAuto != null && o.Memory.History.Contains(lastAuto)) { o.Undo(lastAuto); win.AddPending(lastAuto.From); lastAuto = null; }
            else Show();
        };

        // Closing the window keeps the app watching from the tray.
        win.Closing += (_, e) => { e.Cancel = true; win.Hide(); };

        using var watcher = new FileSystemWatcher(o.Root) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
        void OnFile(string path) => Task.Run(async () =>
        {
            if (!Organiser.IsCandidate(path) || !await WaitUntilDownloaded(path)) return;
            app.Dispatcher.Invoke(() =>
            {
                if (!File.Exists(path)) return;
                var s = o.Suggest(Path.GetFileName(path));
                if (s.Count > 0 && o.ShouldAutoMove(s[0]))
                {
                    try
                    {
                        var (res, rec) = o.Move(path, s[0].Folder, "auto");
                        if (res == MoveResult.Moved)
                        {
                            lastAuto = rec;
                            tray.ShowBalloonTip(5000, "Moved " + Path.GetFileName(path), $"→ {s[0].Folder}\nClick to undo.", Forms.ToolTipIcon.Info);
                            return;
                        }
                    }
                    catch (IOException) { } // locked or vanished: fall through to the queue
                }
                win.AddPending(path);
                tray.ShowBalloonTip(3000, "New download to sort", Path.GetFileName(path), Forms.ToolTipIcon.None);
            });
        });
        watcher.Created += (_, e) => OnFile(e.FullPath);
        watcher.Renamed += (_, e) => OnFile(e.FullPath); // browsers rename .crdownload -> final name when done
        watcher.EnableRaisingEvents = true;

        if (!args.Contains("--tray")) Show();
        app.Run();
        tray.Visible = false;
    }

    /// A download is finished when its size stops changing and nobody else has it open.
    static async Task<bool> WaitUntilDownloaded(string path)
    {
        long last = -1;
        for (int i = 0; i < 300; i++) // ~10 minutes max
        {
            await Task.Delay(2000);
            if (!File.Exists(path)) return false;
            long size;
            try { size = new FileInfo(path).Length; } catch (IOException) { continue; }
            if (size == last)
            {
                try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
                catch (IOException) { }
            }
            last = size;
        }
        return false;
    }
}
