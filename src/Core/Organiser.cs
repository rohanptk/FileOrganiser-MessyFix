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

        void Walk(string dir, int depth)
        {
            var name = Path.GetFileName(dir);
            if (ex.Contains(name) || name.StartsWith('.') || depth > maxDepth) return;
            var rel = Path.GetRelativePath(Root, dir);
            AddFolder(rel);
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir)) Learn(Path.GetFileNameWithoutExtension(f), rel);
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

    /// Feedback loop: called for seeding and for every move the user accepts or corrects.
    public void Learn(string fileName, string folderRel)
    {
        AddFolder(folderRel);
        var c = _counts[folderRel];
        foreach (var t in Tokenizer.Tokens(fileName))
        {
            if (!c.ContainsKey(t)) _df[t] = _df.GetValueOrDefault(t) + 1;
            c[t] = c.GetValueOrDefault(t) + 1;
        }
    }

    public List<Suggestion> Suggest(string fileName, int top = 3)
    {
        var tokens = Tokenizer.Tokens(Path.GetFileNameWithoutExtension(fileName));
        if (tokens.Count == 0 || _counts.Count == 0) return [];
        double n = _counts.Count;
        var scored = new List<(string f, double s)>();
        foreach (var (folder, counts) in _counts)
        {
            double s = 0;
            foreach (var t in tokens)
            {
                var idf = Math.Log(1 + n / (1 + _df.GetValueOrDefault(t)));
                if (counts.TryGetValue(t, out var k)) s += idf * (1 + Math.Log(k));
                if (_folderNameTokens[folder].Contains(t)) s += 2 * idf; // folder name match is a strong signal
            }
            if (s > 0) scored.Add((folder, s + TermBonus(folder)));
        }
        var ranked = scored.OrderByDescending(x => x.s).Take(top).ToList();
        var sum = ranked.Sum(x => x.s);
        return [.. ranked.Select(x => new Suggestion(x.f, x.s, x.s / sum))];
    }

    // Tie-breaker: newer "TERM n" folders win, so current-class handouts land in the current term.
    static double TermBonus(string folder)
    {
        var m = Regex.Match(folder, @"TERM\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) * 0.01 : 0;
    }
}
