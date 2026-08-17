using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Everything;
using AIEverything.Server.Tests.TestDoubles;

namespace AIEverything.Server.Tests.Content;

public sealed class HybridSearchServiceTests
{
    [Fact]
    public async Task Search_fuses_name_content_and_both_sources_with_rrf_boosts()
    {
        var modified = DateTimeOffset.UtcNow;
        var everything = new FakeEverythingSearchService
        {
            NextResponse = new SearchResponse(
                "alpha", 2, 2, 0, 50, 1,
                [
                    new SearchItem("alpha.txt", @"C:\docs\alpha.txt", @"C:\docs", "txt", SearchItemKind.File, 10, modified),
                    new SearchItem("both.txt", @"C:\docs\both.txt", @"C:\docs", "txt", SearchItemKind.File, 20, modified)
                ])
        };
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse(
                "alpha", 2, 2, 0, 50, 1,
                [
                    new ContentSearchItem("both.txt", @"C:\docs\both.txt", "txt", 20, modified, "alpha title", 2, true),
                    new ContentSearchItem("content.txt", @"C:\docs\content.txt", "txt", 30, modified, "alpha body", 1, false)
                ])
        };
        var service = new HybridSearchService(everything, content);

        var response = await service.SearchAsync(
            new HybridSearchRequest("alpha.txt", Path: @"C:\docs", Extensions: ["txt"]));

        Assert.Equal(3, response.TotalResults);
        Assert.Equal("both", response.Items[0].MatchSource);
        Assert.Contains(response.Items, item => item.MatchSource == "name" && item.Name == "alpha.txt");
        Assert.Contains(response.Items, item => item.MatchSource == "content" && item.Name == "content.txt");
        Assert.Equal(@"C:\docs", everything.LastStructuredRequest!.Path);
        Assert.Equal(@"C:\docs", content.LastSearch!.RootPath);
    }

    [Fact]
    public async Task Search_keeps_content_results_when_everything_is_unavailable()
    {
        var everything = new FakeEverythingSearchService
        {
            SearchException = new AIEverythingException(
                "EVERYTHING_NOT_RUNNING", "not running", "start it")
        };
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse(
                "needle", 1, 1, 0, 50, 1,
                [new ContentSearchItem(
                    "content.txt", @"C:\docs\content.txt", "txt", 1,
                    DateTimeOffset.UtcNow, "needle", 1, false)])
        };

        var response = await new HybridSearchService(everything, content)
            .SearchAsync(new HybridSearchRequest("needle"));

        Assert.Single(response.Items);
        Assert.Equal("content", response.Items[0].MatchSource);
    }

    [Fact]
    public async Task Search_reports_title_only_index_match_as_name_when_everything_is_unavailable()
    {
        var everything = new FakeEverythingSearchService
        {
            SearchException = new AIEverythingException(
                "EVERYTHING_NOT_RUNNING", "not running", "start it")
        };
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse(
                "needle", 1, 1, 0, 50, 1,
                [new ContentSearchItem(
                    "needle.txt", @"C:\docs\needle.txt", "txt", 1,
                    DateTimeOffset.UtcNow, "ordinary body", 1, true,
                    BodyMatched: false)])
        };

        var response = await new HybridSearchService(everything, content)
            .SearchAsync(new HybridSearchRequest("needle"));

        var item = Assert.Single(response.Items);
        Assert.Equal("name", item.MatchSource);
        Assert.Null(item.Snippet);
    }

    [Fact]
    public async Task Search_keeps_name_results_when_content_daemon_is_unavailable()
    {
        var everything = new FakeEverythingSearchService
        {
            NextResponse = new SearchResponse(
                "needle", 1, 1, 0, 50, 1,
                [new SearchItem(
                    "needle.txt", @"C:\docs\needle.txt", @"C:\docs", "txt",
                    SearchItemKind.File, 1, DateTimeOffset.UtcNow)])
        };
        var content = new FakeContentSearchService
        {
            Exception = new ContentIndexException(
                ContentErrorCodes.ServiceUnavailable, "missing", "start daemon")
        };

        var response = await new HybridSearchService(everything, content)
            .SearchAsync(new HybridSearchRequest("needle"));

        Assert.Single(response.Items);
        Assert.Equal("name", response.Items[0].MatchSource);
    }

    [Fact]
    public async Task Search_enforces_limit_offset_and_hard_cap()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService();
        var service = new HybridSearchService(everything, content);

        await Assert.ThrowsAsync<ContentIndexException>(() =>
            service.SearchAsync(new HybridSearchRequest("x", Limit: 101)));
        await Assert.ThrowsAsync<ContentIndexException>(() =>
            service.SearchAsync(new HybridSearchRequest("x", Offset: -1)));
    }

    [Fact]
    public async Task Search_paginates_past_hard_name_noise_and_keeps_body_evidence_ahead_of_soft_names()
    {
        var everything = new FakeEverythingSearchService();
        var hardPaths = Enumerable.Range(0, 100)
            .Select(index => Path.Combine(Path.GetTempPath(), $".tmpKoBLIm{index}"))
            .ToArray();
        everything.SearchResponses.Enqueue(new SearchResponse(
            "LLM", 102, 100, 0, 100, 2,
            hardPaths.Select(path => new SearchItem(
                Path.GetFileName(path), path, Path.GetDirectoryName(path)!, string.Empty,
                SearchItemKind.Folder, null, DateTimeOffset.UtcNow, FileAttributes.Directory)).ToArray()));
        everything.SearchResponses.Enqueue(new SearchResponse(
            "LLM", 102, 2, 100, 100, 2,
            [
                new SearchItem(
                    "llm", @"D:\docs\llm", @"D:\docs", string.Empty,
                    SearchItemKind.Folder, null, DateTimeOffset.UtcNow, FileAttributes.Directory),
                new SearchItem(
                    "llm-cache.dll", @"D:\work\node_modules\llm-cache.dll", @"D:\work\node_modules",
                    "dll", SearchItemKind.File, 1, DateTimeOffset.UtcNow, FileAttributes.Normal)
            ]));
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse(
                "LLM", 1, 1, 0, 50, 1,
                [new ContentSearchItem(
                    "llm-task.md", hardPaths[0], "md", 1, DateTimeOffset.UtcNow,
                    "LLM body evidence", 1, false, BodyMatched: true)])
        };

        var response = await new HybridSearchService(everything, content)
            .SearchAsync(new HybridSearchRequest("LLM", Limit: 3));

        Assert.Equal([0, 100], everything.StructuredRequests.Select(request => request.Offset));
        Assert.Equal(hardPaths[0], response.Items[0].FullPath);
        Assert.Equal("content", response.Items[0].MatchSource);
        Assert.Equal("LLM body evidence", response.Items[0].Snippet);
        Assert.Equal(SearchNoiseLevel.Normal, response.Items[0].NameNoise);
        Assert.Equal("llm", response.Items[1].Name);
        Assert.Equal(SearchNoiseLevel.Normal, response.Items[1].NameNoise);
        Assert.Equal("llm-cache.dll", response.Items[2].Name);
        Assert.Equal(SearchNoiseLevel.SoftRanked, response.Items[2].NameNoise);
        Assert.DoesNotContain(response.Items,
            item => item.MatchSource == "name" && item.FullPath.StartsWith(Path.GetTempPath(),
                StringComparison.OrdinalIgnoreCase));
    }
}
