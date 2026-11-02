// Milestone 1: read-only dry run. Prints where each stray file in Downloads would go. Moves nothing.
using Core;

var root = args.Length > 0 ? args[0] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
var index = new FolderIndex(root);
index.Build();
Console.WriteLine($"Indexed {index.Folders.Count} folders under {root}\n");

int none = 0, files = 0;
foreach (var f in Directory.EnumerateFiles(root).OrderBy(x => x))
{
    files++;
    var name = Path.GetFileName(f);
    var s = index.Suggest(name);
    if (s.Count == 0) { none++; Console.WriteLine($"??    {name}"); continue; }
    Console.WriteLine($"{s[0].Confidence,4:P0} {name}\n      -> {s[0].Folder}" +
        (s.Count > 1 ? $"   (alt: {string.Join(" | ", s.Skip(1).Select(x => x.Folder))})" : ""));
}
Console.WriteLine($"\n{files} files, {files - none} with a suggestion, {none} with none.");
