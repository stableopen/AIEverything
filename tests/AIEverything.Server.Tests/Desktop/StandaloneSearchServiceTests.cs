using AIEverything.Content.Contracts;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Desktop;
using AIEverything.Desktop.Mail;
using AIEverything.Desktop.Ranking;
using AIEverything.Server.Tests.TestDoubles;

namespace AIEverything.Server.Tests.Desktop;

public sealed class StandaloneSearchServiceTests
{
    [Fact]
    public async Task Mail_results_are_in_content_and_hybrid_but_never_filename_mode()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService();
        var mail = new FakeMailSearch(
        [
            new MailSearchHit(
                new MailIdentity("store", "entry"), "Mail subject", "Sender", "Recipient",
                DateTimeOffset.UtcNow, "Inbox", "needle in mail", "brief.pdf", 0.1)
        ]);
        var service = new StandaloneSearchService(
            everything, content, new HybridSearchService(everything, content), mail);

        var filename = await service.SearchAsync(
            new DesktopSearchRequest("needle", DesktopSearchMode.FileName));
        var body = await service.SearchAsync(
            new DesktopSearchRequest("needle", DesktopSearchMode.Content));
        var hybrid = await service.SearchAsync(
            new DesktopSearchRequest("needle", DesktopSearchMode.Hybrid));

        Assert.Empty(filename.Items);
        Assert.Contains(body.Items, item => item.MailIdentity is not null && item.MatchSource == "mail");
        Assert.Contains(hybrid.Items, item => item.MailIdentity is not null && item.MatchSource == "mail");
        Assert.Equal(2, mail.SearchCount);
    }

    [Fact]
    public async Task Filename_mode_keeps_full_machine_files_and_folders_with_soft_results_as_fill()
    {
        var everything = new FakeEverythingSearchService
        {
            NextResponse = new SearchResponse("all", 2, 2, 0, 100, 8,
            [
                new SearchItem("setup.exe", @"C:\Windows\setup.exe", @"C:\Windows", "exe", SearchItemKind.File, 10, DateTimeOffset.UtcNow),
                new SearchItem("repo", @"D:\work\repo", @"D:\work", "", SearchItemKind.Folder, null, DateTimeOffset.UtcNow)
            ])
        };
        var content = new FakeContentSearchService();
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest("all", DesktopSearchMode.FileName));

        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, item => item.Extension == "exe");
        Assert.Contains(result.Items, item => item.Kind == SearchItemKind.Folder);
        Assert.Null(everything.LastStructuredRequest!.Extensions);
        Assert.Null(everything.LastStructuredRequest.Path);
    }

    [Fact]
    public async Task Content_mode_maps_exact_location_and_snippet()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse("needle", 1, 1, 0, 100, 5,
            [
                new ContentSearchItem("note.md", @"D:\note.md", "md", 100, DateTimeOffset.UtcNow,
                    "needle text", 1, false, true, 4, 4, "Project > Decision", LocationLabel: "Project > Decision · lines 4-4")
            ])
        };
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest("needle", DesktopSearchMode.Content));

        var item = Assert.Single(result.Items);
        Assert.Equal(4, item.StartLine);
        Assert.Equal("Project > Decision", item.HeadingPath);
        Assert.Equal("needle text", item.Snippet);
    }

    [Fact]
    public async Task Hybrid_mode_combines_everything_name_and_text_content()
    {
        var everything = new FakeEverythingSearchService
        {
            NextResponse = new SearchResponse("needle", 1, 1, 0, 50, 1,
                [new SearchItem("needle.zip", @"D:\repo\needle.zip", @"D:\repo", "zip", SearchItemKind.File, 1, DateTimeOffset.UtcNow)])
        };
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse("needle", 1, 1, 0, 50, 1,
                [new ContentSearchItem("note.txt", @"D:\note.txt", "txt", 1, DateTimeOffset.UtcNow, "needle", 1, false)])
        };
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest("needle", DesktopSearchMode.Hybrid));

        Assert.Contains(result.Items, item => item.FullPath.EndsWith("needle.zip", StringComparison.Ordinal));
        Assert.Contains(result.Items, item => item.FullPath.EndsWith("note.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hybrid_mode_collapses_multiple_passages_from_the_same_file_into_one_row()
    {
        const string fullPath = @"C:\Users\TestUser\Documents\Knowledge\ModelConcepts.txt";
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse("大模型", 3, 3, 0, 50, 1,
            [
                new ContentSearchItem("ModelConcepts.txt", fullPath, "txt", 100, DateTimeOffset.UtcNow,
                    "第一处命中", 1, false, true, 21, 21, LocationLabel: "lines 21-21"),
                new ContentSearchItem("ModelConcepts.txt", fullPath, "txt", 100, DateTimeOffset.UtcNow,
                    "第二处命中", 1, false, true, 24, 24, LocationLabel: "lines 24-24"),
                new ContentSearchItem("ModelConcepts.txt", fullPath, "txt", 100, DateTimeOffset.UtcNow,
                    "第三处命中", 1, false, true, 42, 42, LocationLabel: "lines 42-42")
            ])
        };
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest(
            "大模型", DesktopSearchMode.Hybrid));

        var item = Assert.Single(result.Items);
        Assert.Equal(fullPath, item.FullPath);
        Assert.Equal("第一处命中", item.Snippet);
        Assert.Equal("3 处命中 · lines 21-21", item.LocationLabel);
        Assert.Equal(1, result.TotalResults);
        Assert.Equal(1, result.ReturnedResults);
    }

    [Fact]
    public async Task Filename_mode_paginates_past_hard_noise_and_uses_soft_results_only_as_fill()
    {
        var everything = new FakeEverythingSearchService();
        everything.SearchResponses.Enqueue(new SearchResponse(
            "LLM", 102, 100, 0, 100, 2,
            Enumerable.Range(0, 100).Select(TemporaryDirectory).ToArray()));
        everything.SearchResponses.Enqueue(new SearchResponse(
            "LLM", 102, 2, 100, 100, 3,
            [
                Item("llm-cache.dll", @"D:\work\node_modules\llm-cache.dll"),
                Item("llm-task.md", @"C:\Users\current\Documents\llm-task.md")
            ]));
        var content = new FakeContentSearchService();
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest(
            "LLM", DesktopSearchMode.FileName, Limit: 2));

        Assert.Equal(2, result.TotalResults);
        Assert.Equal(2, result.ReturnedResults);
        Assert.Equal("llm-task.md", result.Items[0].Name);
        Assert.Equal("llm-cache.dll", result.Items[1].Name);
        Assert.Equal(RankingProtectedTier.Eligible, result.Items[0].RankingTier);
        Assert.Equal(RankingProtectedTier.Soft, result.Items[1].RankingTier);
        Assert.Equal([0, 1], result.Items.Select(item => item.BaselineIndex));
        Assert.Equal([0, 100], everything.StructuredRequests.Select(request => request.Offset));
        Assert.All(everything.StructuredRequests, request => Assert.Equal(100, request.Limit));
    }

    [Fact]
    public async Task Filename_mode_never_inspects_more_than_five_everything_pages()
    {
        var everything = new FakeEverythingSearchService();
        for (var page = 0; page < 6; page++)
        {
            everything.SearchResponses.Enqueue(new SearchResponse(
                "LLM", 600, 100, page * 100, 100, 1,
                Enumerable.Range(page * 100, 100).Select(TemporaryDirectory).ToArray()));
        }
        var content = new FakeContentSearchService();
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest(
            "LLM", DesktopSearchMode.FileName, Limit: 10));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalResults);
        Assert.Equal([0, 100, 200, 300, 400],
            everything.StructuredRequests.Select(request => request.Offset));
        Assert.Single(everything.SearchResponses);
    }

    [Theory]
    [InlineData(".gitignore", @"D:\work\repo\.gitignore", false, RankingProtectedTier.Exact)]
    [InlineData(".env", @"D:\work\repo\.env", false, RankingProtectedTier.Exact)]
    [InlineData("kernel32.dll", @"C:\Windows\System32\kernel32.dll", false, RankingProtectedTier.Exact)]
    [InlineData("node_modules", @"D:\work\node_modules", true, RankingProtectedTier.Exact)]
    [InlineData("attempt", @"D:\Templates\attempt.md", false, RankingProtectedTier.Eligible)]
    public async Task Filename_mode_keeps_exact_or_boundary_conflict_results_searchable(
        string query,
        string fullPath,
        bool folder,
        RankingProtectedTier expectedTier)
    {
        var item = new SearchItem(
            Path.GetFileName(fullPath), fullPath, Path.GetDirectoryName(fullPath) ?? string.Empty,
            folder ? string.Empty : Path.GetExtension(fullPath).TrimStart('.'),
            folder ? SearchItemKind.Folder : SearchItemKind.File,
            folder ? null : 1,
            DateTimeOffset.UtcNow,
            folder ? FileAttributes.Directory : FileAttributes.Normal);
        var everything = new FakeEverythingSearchService
        {
            NextResponse = new SearchResponse(query, 1, 1, 0, 100, 1, [item])
        };
        var content = new FakeContentSearchService();
        var service = new StandaloneSearchService(everything, content,
            new HybridSearchService(everything, content));

        var result = await service.SearchAsync(new DesktopSearchRequest(
            query, DesktopSearchMode.FileName));

        var resultItem = Assert.Single(result.Items);
        Assert.Equal(fullPath, resultItem.FullPath);
        Assert.Equal(expectedTier, resultItem.RankingTier);
    }

    private static SearchItem TemporaryDirectory(int index)
    {
        var fullPath = Path.Combine(Path.GetTempPath(), $".tmpKoBLIm{index}");
        return new SearchItem(
            Path.GetFileName(fullPath), fullPath, Path.GetDirectoryName(fullPath)!, string.Empty,
            SearchItemKind.Folder, null, DateTimeOffset.UtcNow, FileAttributes.Directory);
    }

    private static SearchItem Item(string name, string fullPath) => new(
        name,
        fullPath,
        Path.GetDirectoryName(fullPath) ?? string.Empty,
        Path.GetExtension(name).TrimStart('.'),
        SearchItemKind.File,
        1,
        DateTimeOffset.UtcNow,
        FileAttributes.Normal);

    private sealed class FakeMailSearch(IReadOnlyList<MailSearchHit> hits) : IMailSearch
    {
        public int SearchCount { get; private set; }

        public Task<IReadOnlyList<MailSearchHit>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken)
        {
            SearchCount++;
            return Task.FromResult(hits);
        }
    }
}
