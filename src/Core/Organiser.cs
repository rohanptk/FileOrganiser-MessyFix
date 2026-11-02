using System.Text.RegularExpressions;

namespace Core;

public static partial class Tokenizer
{
    static readonly HashSet<string> Stop = ["the", "and", "of", "for", "to", "in", "on", "a", "an", "copy", "final", "new", "v", "pdf", "docx", "doc", "xlsx", "pptx"];

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")] private static partial Regex CamelSplit();
    [GeneratedRegex(@"[^a-z]+")] private static partial Regex NonLetters();

    // Digits are dropped: "Term5" and "TERM 5" both become "term", and term recency is handled separately.
    public static List<string> Tokens(string name) =>
        NonLetters().Split(CamelSplit().Replace(name, " ").ToLowerInvariant())
            .Where(t => t.Length > 1 && !Stop.Contains(t))
            .Distinct().ToList();
}

public record Suggestion(string Folder, double Score, double Confidence);

public class FolderIndex
{
    // folder (relative path) -> token -> count
    readonly Dictionary<string, Dictionary<string, int>> _counts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _folderNameTokens = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _df = [];
    public string Root { get; }

    public static readonly string[] DefaultExcludes = ["CODE", "setups", "recovery", "node_modules", ".git", "bin", "obj", "__MACOSX"];

    public FolderIndex(string root) => Root = Path.GetFullPath(root);

    public IReadOnlyCollection<string> Folders => _counts.Keys;

    /// Seed from the folders the user already organised. Only directories below root are indexed.
    public void Build(IEnumerable<string>? excludes = null, int maxDepth = 4)
    {
        var ex = new HashSet<string>(excludes ?? DefaultExcludes, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(Root))
            Walk(dir, 1);

        // Words found in files across many unrelated top-level folders (your own name, "final", "report")
        // say nothing about where a file belongs.
        var top = _counts.Keys.GroupBy(f => f.Split(Path.DirectorySeparatorChar)[0]).ToList();
        var spread = top.SelectMany(g => g.SelectMany(f => _counts[f].Keys).Distinct()).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var limit = Math.Max(3, top.Count / 5);
        foreach (var (t, n) in spread) if (n >= limit) Ignored.Add(t);

        void Walk(string dir, int depth)
        {
            var name = Path.GetFileName(dir);
            if (ex.Contains(name) || name.StartsWith('.') || depth > maxDepth) return;
            var rel = Path.GetRelativePath(Root, dir);
            // Machine-made folders (pdf-to-jpg exports, "Page_files") are never a real destination.
            if (IsJunk(name)) return;
            AddFolder(rel);
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir)) Learn(Path.GetFileName(f), rel);
                foreach (var d in Directory.EnumerateDirectories(dir)) Walk(d, depth + 1);
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void AddFolder(string rel)
    {
        if (_counts.ContainsKey(rel)) return;
        _counts[rel] = [];
        // Every folder name on the path counts: "TERM 5\MARKETING ANALYTICS" -> term, marketing, analytics.
        _folderNameTokens[rel] = [.. rel.Split(Path.DirectorySeparatorChar).SelectMany(Tokenizer.Tokens)];
    }

    public static bool IsJunk(string folderName)
    {
        var n = folderName.ToLowerInvariant();
        return n.Contains("ilovepdf") || n.Contains("pages-to-jpg") || n.EndsWith("_files") || n == "_archive"
            || Tokenizer.Tokens(folderName).Count >= 7; // a folder named like a whole file title is an extracted export
    }

    readonly Dictionary<string, HashSet<string>> _exts = new(StringComparer.OrdinalIgnoreCase);

    /// Feedback loop: called for seeding (weight 1) and for every move the user makes (higher weight).
    public void Learn(string fileName, string folderRel, int weight = 1)
    {
        AddFolder(folderRel);
        var c = _counts[folderRel];
        foreach (var t in Tokenizer.Tokens(Path.GetFileNameWithoutExtension(fileName)))
        {
            if (!c.ContainsKey(t)) _df[t] = _df.GetValueOrDefault(t) + 1;
            c[t] = c.GetValueOrDefault(t) + weight;
        }
        var ext = Path.GetExtension(fileName);
        if (ext.Length > 0) (_exts.TryGetValue(folderRel, out var e) ? e : _exts[folderRel] = new(StringComparer.OrdinalIgnoreCase)).Add(ext);
    }

    public HashSet<string> Ignored { get; } = [];

    public List<Suggestion> Suggest(string fileName, int top = 3)
    {
        var tokens = Tokenizer.Tokens(Path.GetFileNameWithoutExtension(fileName)).Where(t => !Ignored.Contains(t)).ToList();
        if (tokens.Count == 0 || _counts.Count == 0) return [];
        var ext = Path.GetExtension(fileName);
        double n = _counts.Count;
        var scored = new List<(string f, double s)>();
        foreach (var (folder, counts) in _counts)
        {
            double s = 0;
            foreach (var t in tokens)
            {
                var idf = Math.Log(1 + n / (1 + _df.GetValueOrDefault(t)));
                if (counts.TryGetValue(t, out var k)) s += idf * (1 + Math.Log(k));
                if (_folderNameTokens[folder].Contains(t)) s += 3 * idf; // folder name match is the strongest signal
            }
            if (s <= 0) continue;
            if (ext.Length > 0 && _exts.TryGetValue(folder, out var e) && e.Contains(ext)) s += 0.5;
            scored.Add((folder, s + TermBonus(folder)));
        }
        var ranked = scored.OrderByDescending(x => x.s).Take(top).ToList();
        // Confidence = how clearly #1 beats #2. 0.9 means #1 scored 9x the runner-up.
        double Conf(int i) => ranked.Count == 1 ? 1 : ranked[i].s / (ranked[i].s + ranked[i == 0 ? 1 : 0].s);
        return [.. ranked.Select((x, i) => new Suggestion(x.f, x.s, i == 0 ? Conf(0) : x.s / (x.s + ranked[0].s)))];
    }

    // Tie-breaker: newer "TERM n" folders win, so current-class handouts land in the current term.
    static double TermBonus(string folder)
    {
        var m = Regex.Match(folder, @"TERM\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) * 0.01 : 0;
    }
}
