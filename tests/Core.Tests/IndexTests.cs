using Core;

public class IndexTests
{
    [Fact]
    public void Tokenizes_camel_digits_and_separators() =>
        Assert.Equal(["term", "schedule"], Tokenizer.Tokens("Term5_Schedule_v2"));

    [Fact]
    public void Ranks_by_folder_names_and_learned_files()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mba", "TERM 4", "SCHEDULE"));
            Directory.CreateDirectory(Path.Combine(root, "mba", "TERM 5", "Time table"));
            Directory.CreateDirectory(Path.Combine(root, "mba", "TERM 5", "MARKETING ANALYTICS"));
            Directory.CreateDirectory(Path.Combine(root, "mba", "resumes"));
            File.WriteAllText(Path.Combine(root, "mba", "resumes", "Rohan_CV_2026.pdf"), "");

            var idx = new FolderIndex(root);
            idx.Build();

            Assert.Equal(Path.Combine("mba", "TERM 5", "MARKETING ANALYTICS"), idx.Suggest("Marketing Analytics Session 3.pdf")[0].Folder);
            Assert.Equal(Path.Combine("mba", "resumes"), idx.Suggest("rohan cv updated.docx")[0].Folder);

            // Learning: after one correction, "timetable" maps to Time table.
            idx.Learn("timetable week 1", Path.Combine("mba", "TERM 5", "Time table"));
            Assert.Equal(Path.Combine("mba", "TERM 5", "Time table"), idx.Suggest("Timetable week 7.xlsx")[0].Folder);
        }
        finally { Directory.Delete(root, true); }
    }
}
