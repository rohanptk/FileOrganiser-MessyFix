using System.Security.Cryptography;
using System.Text.Json;

namespace Core;

public class Settings
{
    public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public bool AutoMove { get; set; } = false; // opt-in: nothing moves on its own until you switch this on
    public double AutoMoveThreshold { get; set; } = 0.9;
    /// Words that never decide a folder, e.g. your own name. Widely spread words are also ignored automatically.
    public List<string> IgnoreWords { get; set; } = [];
    public List<string> Excludes { get; set; } = [.. FolderIndex.DefaultExcludes, "Project Hail Mary 2026 UHD BluRay IMAX 2160p HDR10 DV HEVC TrueHD Atmos 7.1 x265-E", "vismay usb backup"];
}

/// A pinned rule: a file whose name contains all of these words goes to Folder.
public record Rule(List<string> Words, string Folder);
public record Learned(string Name, string Folder);
public record MoveRecord(string From, string To, DateTime At, string Reason);

/// Everything the app remembers, saved as one readable JSON file.
public class Memory
{
    public Settings Settings { get; set; } = new();
    public List<Rule> Rules { get; set; } = [];
    public List<Learned> Learned { get; set; } = [];
    public List<MoveRecord> History { get; set; } = [];

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadsOrganiser", "memory.json");

    public static Memory Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Memory>(File.ReadAllText(path)) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, path, true); // write-then-swap so a crash never leaves a half-written memory file
    }
}

public enum MoveResult { Moved, Duplicate, Missing }

public static class Mover
{
    /// Moves src into destDir. Never overwrites: clashes get " (2)", identical content is reported as Duplicate.
    public static (MoveResult, string?) Move(string src, string destDir)
    {
        if (!File.Exists(src)) return (MoveResult.Missing, null);
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, Path.GetFileName(src));
        if (File.Exists(dest) && SameContent(src, dest)) return (MoveResult.Duplicate, dest);
        dest = Unique(dest);
        File.Move(src, dest);
        return (MoveResult.Moved, dest);
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}

/// The app's brain: suggestions (rules first, then the learned index), moves, undo, and term archiving.
public class Organiser
{
    public Memory Memory { get; }
    public FolderIndex Index { get; private set; } = null!;
    readonly string _path;
    public string Root => Memory.Settings.Root;

    public Organiser(string? memoryPath = null)
    {
        _path = memoryPath ?? Memory.DefaultPath;
        Memory = Memory.Load(_path);
        Rebuild();
    }

    public void Rebuild()
    {
        Index = new FolderIndex(Root);
        Index.Build(Memory.Settings.Excludes);
        Index.Ignored.UnionWith(Memory.Settings.IgnoreWords.Select(w => w.ToLowerInvariant()));
        foreach (var l in Memory.Learned) Index.Learn(l.Name, l.Folder, 3); // your own moves outweigh what was already in folders
    }

    public void Save() => Memory.Save(_path);

    public static readonly string[] PartialExts = [".crdownload", ".part", ".tmp", ".download", ".partial"];

    /// Stray files: top-level files in Downloads only, skipping downloads still in progress and hidden/system files.
    public IEnumerable<string> StrayFiles() =>
        Directory.EnumerateFiles(Root).Where(IsCandidate).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    public static bool IsCandidate(string f)
    {
        var name = Path.GetFileName(f);
        if (PartialExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) return false;
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$")) return false;
        try { return (File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) == 0; }
        catch (IOException) { return false; }
    }

    public List<Suggestion> Suggest(string fileName)
    {
        var words = Tokenizer.Tokens(Path.GetFileNameWithoutExtension(fileName)).ToHashSet();
        var rule = Memory.Rules.LastOrDefault(r => r.Words.Count > 0 && r.Words.All(words.Contains));
        var s = Index.Suggest(fileName);
        if (rule == null) return s;
        return [new Suggestion(rule.Folder, double.MaxValue, 1), .. s.Where(x => !x.Folder.Equals(rule.Folder, StringComparison.OrdinalIgnoreCase)).Take(2)];
    }

    public bool ShouldAutoMove(Suggestion s) => Memory.Settings.AutoMove && s.Confidence >= Memory.Settings.AutoMoveThreshold;

    /// folder may be relative to Root or absolute (must be inside Root).
    public (MoveResult result, MoveRecord? record) Move(string src, string folder, string reason)
    {
        var abs = Path.GetFullPath(Path.Combine(Root, folder));
        var rel = Path.GetRelativePath(Root, abs);
        if (rel.StartsWith("..") || Path.IsPathRooted(rel) || rel == ".")
            throw new ArgumentException($"Destination must be a folder inside {Root}");
        var (r, dest) = Mover.Move(src, abs);
        if (r != MoveResult.Moved) return (r, null);
        var rec = new MoveRecord(src, dest!, DateTime.Now, reason);
        Memory.History.Add(rec);
        Memory.Learned.Add(new Learned(Path.GetFileName(src), rel));
        Index.Learn(Path.GetFileName(src), rel, 3);
        Save();
        return (r, rec);
    }

    public bool Undo(MoveRecord rec)
    {
        if (!File.Exists(rec.To)) return false;
        File.Move(rec.To, Mover.Unique(rec.From));
        Memory.History.Remove(rec);
        var l = Memory.Learned.FindLastIndex(x => x.Name == Path.GetFileName(rec.From));
        if (l >= 0) Memory.Learned.RemoveAt(l);
        Save();
        Rebuild(); // forget what this move taught
        return true;
    }

    public void AddRule(IEnumerable<string> words, string folder)
    {
        var w = words.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        if (w.Count == 0) return;
        var abs = Path.GetFullPath(Path.Combine(Root, folder));
        Memory.Rules.Add(new Rule(w, Path.GetRelativePath(Root, abs)));
        Save();
    }

    /// End of term: moves e.g. "mba@glim\TERM 5" to "mba@glim\_ARCHIVE\TERM 5". Nothing is deleted.
    public string ArchiveFolder(string folder)
    {
        var abs = Path.GetFullPath(Path.Combine(Root, folder)).TrimEnd('\\');
        var parent = Path.GetDirectoryName(abs)!;
        if (!abs.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || abs.Length <= Root.TrimEnd('\\').Length)
            throw new ArgumentException("Pick a folder inside Downloads");
        var archive = Path.Combine(parent, "_ARCHIVE");
        Directory.CreateDirectory(archive);
        var dest = Path.Combine(archive, Path.GetFileName(abs));
        if (Directory.Exists(dest)) throw new IOException($"{dest} already exists");
        Directory.Move(abs, dest);
        Rebuild();
        return dest;
    }
}
