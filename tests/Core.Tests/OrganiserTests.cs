using Core;

public class OrganiserTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory().FullName;
    readonly string _mem;

    public OrganiserTests()
    {
        _mem = Path.Combine(_root, "_mem", "memory.json");
        Directory.CreateDirectory(Path.Combine(_root, "mba", "TERM 5", "Time table"));
        Directory.CreateDirectory(Path.Combine(_root, "mba", "resumes"));
        new Memory { Settings = { Root = _root, Excludes = ["_mem"] } }.Save(_mem);
    }

    public void Dispose() => Directory.Delete(_root, true);

    string Stray(string name, string content = "x")
    {
        var p = Path.Combine(_root, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Move_renames_on_clash_and_detects_duplicates()
    {
        var o = new Organiser(_mem);
        var dest = Path.Combine("mba", "resumes");
        File.WriteAllText(Path.Combine(_root, dest, "cv.pdf"), "old");

        var (r1, rec) = o.Move(Stray("cv.pdf", "new"), dest, "test");
        Assert.Equal(MoveResult.Moved, r1);
        Assert.EndsWith("cv (2).pdf", rec!.To);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, dest, "cv.pdf"))); // never overwritten

        var (r2, _) = o.Move(Stray("cv.pdf", "old"), dest, "test");
        Assert.Equal(MoveResult.Duplicate, r2);
        Assert.True(File.Exists(Path.Combine(_root, "cv.pdf"))); // duplicate stays put for the user to decide
    }

    [Fact]
    public void Undo_restores_file_and_forgets_learning()
    {
        var o = new Organiser(_mem);
        var src = Stray("zebra notes.pdf");
        var (_, rec) = o.Move(src, Path.Combine("mba", "resumes"), "test");
        Assert.False(File.Exists(src));
        Assert.NotEmpty(o.Suggest("zebra other.pdf"));

        Assert.True(o.Undo(rec!));
        Assert.True(File.Exists(src));
        Assert.Empty(o.Suggest("zebra other.pdf"));
    }

    [Fact]
    public void Rules_win_and_memory_persists()
    {
        var o = new Organiser(_mem);
        o.AddRule(["term", "schedule"], Path.Combine("mba", "TERM 5", "Time table"));
        o.Move(Stray("quokka.pdf"), Path.Combine("mba", "resumes"), "test");

        var o2 = new Organiser(_mem); // fresh load from disk
        var s = o2.Suggest("Term 5 Schedule v3.xlsx")[0];
        Assert.Equal(Path.Combine("mba", "TERM 5", "Time table"), s.Folder);
        Assert.True(o2.ShouldAutoMove(s) == false); // auto-move is off by default
        Assert.Equal(Path.Combine("mba", "resumes"), o2.Suggest("quokka 2.pdf")[0].Folder);
    }

    [Fact]
    public void Move_refuses_destinations_outside_root()
    {
        var o = new Organiser(_mem);
        Assert.Throws<ArgumentException>(() => o.Move(Stray("a.pdf"), @"..\..\Windows", "test"));
    }

    [Fact]
    public void Archive_moves_term_under_archive_and_skips_partials()
    {
        var o = new Organiser(_mem);
        var dest = o.ArchiveFolder(Path.Combine("mba", "TERM 5"));
        Assert.Equal(Path.Combine(_root, "mba", "_ARCHIVE", "TERM 5"), dest);
        Assert.DoesNotContain(o.Index.Folders, f => f.Contains("_ARCHIVE"));

        Stray("big.zip.crdownload");
        Stray("real.pdf");
        Assert.Equal(["real.pdf"], o.StrayFiles().Select(Path.GetFileName));
    }
}
