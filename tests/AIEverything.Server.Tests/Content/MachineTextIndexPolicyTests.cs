using AIEverything.Content.MachineIndex;

namespace AIEverything.Server.Tests.Content;

public sealed class MachineTextIndexPolicyTests
{
    private static readonly IReadOnlyList<MachineDrive> Drives =
    [
        new(@"C:\", true, DriveType.Fixed, "NTFS"),
        new(@"D:\", true, DriveType.Fixed, "ReFS"),
        new(@"E:\", true, DriveType.Removable, "NTFS"),
        new(@"Z:\", true, DriveType.Network, "NTFS"),
        new(@"F:\", false, DriveType.Fixed, "NTFS"),
        new(@"G:\", true, DriveType.Fixed, "FAT32")
    ];

    [Fact]
    public void Plan_includes_only_ready_fixed_ntfs_or_refs_drives()
    {
        var plan = MachineTextIndexPolicy.Build(
            Drives,
            @"C:\Users\current",
            @"C:\Windows",
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\ProgramData");

        Assert.Equal([@"C:\", @"D:\"], plan.DriveRoots);
    }

    [Fact]
    public void Policy_accepts_text_and_docx_with_format_specific_limits()
    {
        var plan = CreatePlan();
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = 5 * 1024 * 1024,
            [".md"] = 5 * 1024 * 1024,
            [".markdown"] = 5 * 1024 * 1024,
            [".docx"] = 10 * 1024 * 1024
        };

        Assert.Equal(expected.Keys.Order(), plan.SupportedExtensions.Order());
        foreach (var pair in expected)
        {
            var decision = plan.Evaluate(Candidate($@"D:\docs\sample{pair.Key}", pair.Value));
            Assert.True(decision.Accepted, pair.Key);
            Assert.Equal(pair.Value, decision.MaxBytes);
        }

        Assert.False(plan.Evaluate(Candidate(@"D:\docs\oversize.docx", 10 * 1024 * 1024 + 1)).Accepted);

        foreach (var extension in new[] { ".rst", ".json", ".cs", ".pdf", ".xlsx", ".pptx", ".eml", ".msg" })
        {
            Assert.False(plan.Evaluate(Candidate($@"D:\docs\sample{extension}", 100)).Accepted);
        }
    }

    [Fact]
    public void Repository_marker_excludes_its_entire_repository_but_not_filename_search()
    {
        var plan = CreatePlan();
        var prefixes = plan.BuildDynamicExclusionPrefixes(
        [
            Candidate(@"D:\work\repo\.git", 0, FileAttributes.Directory),
            Candidate(@"D:\work\linked\.git", 8, FileAttributes.Normal),
            Candidate(@"D:\packages\node_modules", 0, FileAttributes.Directory)
        ]);

        Assert.Contains(@"D:\work\repo", prefixes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"D:\work\linked", prefixes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"D:\packages\node_modules", prefixes, StringComparer.OrdinalIgnoreCase);
        Assert.False(plan.Evaluate(Candidate(@"D:\work\repo\README.md", 100), prefixes).Accepted);
        Assert.False(plan.Evaluate(Candidate(@"D:\work\linked\manual.pdf", 100), prefixes).Accepted);
        Assert.True(plan.Evaluate(Candidate(@"D:\work\ordinary\README.md", 100), prefixes).Accepted);
    }

    [Fact]
    public void Policy_rejects_system_other_user_unsafe_package_and_generated_paths()
    {
        var plan = CreatePlan();
        var rejected = new[]
        {
            Candidate(@"C:\Windows\notes.txt", 100),
            Candidate(@"C:\Program Files\App\readme.md", 100),
            Candidate(@"C:\ProgramData\Vendor\notes.txt", 100),
            Candidate(@"C:\Users\other\Documents\secret.txt", 100),
            Candidate(@"C:\Users\current\AppData\Local\notes.txt", 100),
            Candidate(@"D:\Recovery\notes.txt", 100),
            Candidate(@"D:\$Recycle.Bin\notes.txt", 100),
            Candidate(@"D:\work\node_modules\readme.md", 100),
            Candidate(@"D:\downloads\packages\readme.txt", 100),
            Candidate(@"D:\docs\hidden.txt", 100, FileAttributes.Hidden),
            Candidate(@"D:\docs\system.txt", 100, FileAttributes.System),
            Candidate(@"D:\docs\offline.txt", 100, FileAttributes.Offline),
            Candidate(@"D:\docs\temporary.txt", 100, FileAttributes.Temporary),
            Candidate(@"D:\docs\link.txt", 100, FileAttributes.ReparsePoint)
        };

        Assert.All(rejected, item => Assert.False(plan.Evaluate(item).Accepted, item.FullPath));
        Assert.True(plan.Evaluate(Candidate(@"C:\Users\current\Documents\note.txt", 100)).Accepted);
        Assert.True(plan.Evaluate(Candidate(@"D:\knowledge\note.md", 100)).Accepted);
    }

    [Fact]
    public void Priority_prefers_current_user_visible_folders_then_recent_files()
    {
        var plan = CreatePlan();
        var recent = DateTimeOffset.UtcNow.AddDays(-1);
        var old = DateTimeOffset.UtcNow.AddYears(-2);

        var documents = plan.Evaluate(Candidate(
            @"C:\Users\current\Documents\now.md", 100, modifiedAt: recent));
        var otherRecent = plan.Evaluate(Candidate(@"D:\archive\now.md", 100, modifiedAt: recent));
        var otherOld = plan.Evaluate(Candidate(@"D:\archive\old.md", 100, modifiedAt: old));

        Assert.True(documents.Priority < otherRecent.Priority);
        Assert.True(otherRecent.Priority < otherOld.Priority);
    }

    private static MachineTextIndexPlan CreatePlan() => MachineTextIndexPolicy.Build(
        Drives,
        @"C:\Users\current",
        @"C:\Windows",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\ProgramData");

    private static CatalogEntry Candidate(
        string path,
        long size,
        FileAttributes attributes = FileAttributes.Normal,
        DateTimeOffset? modifiedAt = null) => new(
            path,
            Path.GetFileName(path),
            Path.GetExtension(path),
            size,
            modifiedAt ?? DateTimeOffset.UtcNow,
            attributes);
}
