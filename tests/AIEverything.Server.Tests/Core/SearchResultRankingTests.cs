using AIEverything.Core;

namespace AIEverything.Server.Tests.Core;

public sealed class SearchResultRankingTests
{
    [Fact]
    public void Noise_policy_hard_filters_only_explicit_one_time_noise()
    {
        var temporaryDirectory = Item(
            Path.Combine(Path.GetTempPath(), ".tmpKoBLIm"),
            kind: SearchItemKind.Folder,
            attributes: FileAttributes.Directory);
        var items = new[]
        {
            temporaryDirectory,
            Item(@"D:\$Recycle.Bin\deleted.txt"),
            Item(@"D:\System Volume Information\tracking.log"),
            Item(@"D:\docs\scratch.tmp"),
            Item(@"D:\docs\scratch.temp"),
            Item(@"D:\docs\desktop.ini"),
            Item(@"D:\docs\~$proposal.docx"),
            Item(@"D:\docs\.~lock.notes.md#"),
            Item(@"D:\docs\transient.txt", attributes: FileAttributes.Temporary)
        };

        Assert.All(items, item =>
            Assert.Equal(SearchNoiseLevel.HardFiltered,
                SearchResultRanking.ClassifyNoise("LLM", item)));
        Assert.Equal(SearchNoiseLevel.HardFiltered,
            SearchResultRanking.ClassifyNoise("scratch.tmp", Item(@"D:\docs\scratch.tmp")));
    }

    [Fact]
    public void Noise_policy_uses_component_boundaries_and_exact_queries_restore_soft_results()
    {
        Assert.Equal(SearchNoiseLevel.Normal,
            SearchResultRanking.ClassifyNoise("LLM", Item(@"D:\Templates\attempt.md")));
        Assert.Equal(SearchNoiseLevel.Normal,
            SearchResultRanking.ClassifyNoise("gitignore", Item(@"D:\work\repo\.gitignore")));
        Assert.Equal(SearchNoiseLevel.Normal,
            SearchResultRanking.ClassifyNoise("env", Item(@"D:\work\repo\.env")));

        var dependency = Item(@"D:\work\node_modules\kernel32.dll");
        Assert.Equal(SearchNoiseLevel.SoftRanked,
            SearchResultRanking.ClassifyNoise("kernel", dependency));
        Assert.Equal(SearchNoiseLevel.Normal,
            SearchResultRanking.ClassifyNoise("kernel32.dll", dependency));
        Assert.Equal(SearchNoiseLevel.Normal,
            SearchResultRanking.ClassifyNoise(@"D:\work\node_modules\kernel32.dll", dependency));

        Assert.Equal(SearchNoiseLevel.SoftRanked,
            SearchResultRanking.ClassifyNoise("draft", Item(@"D:\docs\.tmpDraft")));
        Assert.Equal(SearchNoiseLevel.SoftRanked,
            SearchResultRanking.ClassifyNoise("conf", Item(@"D:\work\.git\config")));
        Assert.Equal(SearchNoiseLevel.SoftRanked,
            SearchResultRanking.ClassifyNoise("artifact", Item(@"D:\work\build\artifact.dll")));
        Assert.Equal(SearchNoiseLevel.SoftRanked,
            SearchResultRanking.ClassifyNoise("notes", Item(
                @"D:\docs\notes.md", attributes: FileAttributes.Hidden)));
    }

    private static SearchItem Item(
        string fullPath,
        SearchItemKind kind = SearchItemKind.File,
        FileAttributes attributes = FileAttributes.Normal) => new(
            Path.GetFileName(fullPath),
            fullPath,
            Path.GetDirectoryName(fullPath) ?? string.Empty,
            Path.GetExtension(fullPath).TrimStart('.'),
            kind,
            kind == SearchItemKind.Folder ? null : 1,
            DateTimeOffset.UtcNow,
            attributes);
}
