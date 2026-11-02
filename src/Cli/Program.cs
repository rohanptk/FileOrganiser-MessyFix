// Read-only dry run: prints where each stray file in Downloads would go. Moves nothing.
using Core;

var root = args.Length > 0 ? args[0] : new Settings().Root;
var index = new FolderIndex(root);
index.Build(new Settings().Excludes);
Console.WriteLine($"Indexed {index.Folders.Count} folders under {root}. Auto-ignored words: {string.Join(", ", index.Ignored.Order())}\n");

int none = 0, files = 0, sure = 0;
foreach (var f in Directory.EnumerateFiles(root).Where(Organiser.IsCandidate).Order())
{
    files++;
    var name = Path.GetFileName(f);
    var s = index.Suggest(name);
    if (s.Count == 0) { none++; Console.WriteLine($"??    {name}"); continue; }
    if (s[0].Confidence >= 0.9) sure++;
    Console.WriteLine($"{s[0].Confidence,4:P0} {name}\n      -> {s[0].Folder}" +
        (s.Count > 1 ? $"   (alt: {string.Join(" | ", s.Skip(1).Select(x => x.Folder))})" : ""));
}
Console.WriteLine($"\n{files} files, {files - none} with a suggestion ({sure} at >=90%), {none} with none.");
