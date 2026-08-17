using System.Reflection;
using AIEverything.Content.Contracts;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Everything;
using AIEverything.Mcp;
using AIEverything.Server.Tests.TestDoubles;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AIEverything.Server.Tests.Mcp;

public sealed class AIEverythingToolsTests
{
    [Fact]
    public void Search_local_files_forwards_all_filters()
    {
        var service = new FakeEverythingSearchService();
        var tools = new AIEverythingTools(service);

        var response = tools.SearchLocalFiles(
            query: "benchmark",
            path: @"C:\Users\TestUser\Documents",
            extensions: ["csv"],
            kind: "file",
            modifiedAfter: DateTimeOffset.Parse("2026-07-01T00:00:00+08:00"),
            modifiedBefore: DateTimeOffset.Parse("2026-07-15T23:59:59+08:00"),
            sortBy: "modified",
            sortDirection: "desc",
            limit: 5,
            offset: 2);

        Assert.Equal("benchmark", service.LastStructuredRequest!.Query);
        Assert.Equal(@"C:\Users\TestUser\Documents", service.LastStructuredRequest.Path);
        Assert.Equal(["csv"], service.LastStructuredRequest.Extensions);
        Assert.Equal(SearchItemKind.File, service.LastStructuredRequest.Kind);
        Assert.Equal(SearchSortBy.Modified, service.LastStructuredRequest.SortBy);
        Assert.Equal(SearchSortDirection.Desc, service.LastStructuredRequest.SortDirection);
        Assert.Equal(5, response.Limit);
        Assert.Equal(2, response.Offset);
    }

    [Fact]
    public void Search_everything_query_preserves_native_syntax()
    {
        var service = new FakeEverythingSearchService();
        var tools = new AIEverythingTools(service);

        tools.SearchEverythingQuery(
            "ext:pdf dm:thisweek",
            sortBy: "modified",
            sortDirection: "desc",
            limit: 8,
            offset: 1);

        Assert.Equal("ext:pdf dm:thisweek", service.LastRawQuery);
    }

    [Theory]
    [InlineData("device")]
    [InlineData(null)]
    public void Invalid_enum_text_returns_stable_mcp_error(string? kind)
    {
        var tools = new AIEverythingTools(new FakeEverythingSearchService());

        var exception = Assert.Throws<McpException>(() => tools.SearchLocalFiles(
            query: "benchmark",
            kind: kind!));

        Assert.Contains("INVALID_QUERY", exception.Message, StringComparison.Ordinal);
        Assert.Contains("kind", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Service_failure_returns_stable_mcp_error()
    {
        var service = new FakeEverythingSearchService
        {
            SearchException = new AIEverythingException(
                "EVERYTHING_NOT_RUNNING",
                "Everything IPC is unavailable.",
                "Start Everything and retry.",
                2)
        };
        var tools = new AIEverythingTools(service);

        var exception = Assert.Throws<McpException>(() =>
            tools.SearchEverythingQuery("report"));

        Assert.Contains("EVERYTHING_NOT_RUNNING", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Start Everything and retry.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_is_forwarded_without_searching()
    {
        var service = new FakeEverythingSearchService();
        var tools = new AIEverythingTools(service);

        var status = tools.GetStatus();

        Assert.True(status.Ready);
        Assert.Null(service.LastStructuredRequest);
        Assert.Null(service.LastRawQuery);
    }

    [Theory]
    [InlineData(nameof(AIEverythingTools.SearchLocalFiles), "search_local_files")]
    [InlineData(nameof(AIEverythingTools.SearchEverythingQuery), "search_everything_query")]
    [InlineData(nameof(AIEverythingTools.GetStatus), "aieverything_status")]
    public void Tools_declare_exact_names_and_read_only_annotations(string methodName, string expectedName)
    {
        var method = typeof(AIEverythingTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!;
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(expectedName, attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.True(attribute.UseStructuredContent);
    }

    [Fact]
    public async Task Content_and_hybrid_tools_forward_filters()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse("needle", 0, 0, 0, 7, 1, [])
        };
        var tools = new AIEverythingTools(
            everything, content, new HybridSearchService(everything, content));

        var response = await tools.SearchLocalContent(
            "needle",
            path: @"C:\docs",
            extensions: ["txt"],
            limit: 7,
            offset: 1);
        var contentField = content.LastSearch!.Field;
        await tools.SearchLocalHybrid(
            "needle", path: @"C:\docs", extensions: ["txt"], limit: 6);

        Assert.Equal(7, response.Limit);
        Assert.Equal(@"C:\docs", content.LastSearch!.RootPath);
        Assert.Equal(["txt"], content.LastSearch.Extensions);
        Assert.Equal(ContentSearchField.Body, contentField);
        Assert.Equal(ContentSearchField.All, content.LastSearch.Field);
    }

    [Fact]
    public async Task Index_status_forwards_to_content_service()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService();
        var tools = new AIEverythingTools(
            everything, content, new HybridSearchService(everything, content));

        var status = await tools.GetIndexStatus();

        Assert.True(status.Ready);
    }

    [Theory]
    [InlineData(nameof(AIEverythingTools.SearchLocalContent), "search_local_content", true, false)]
    [InlineData(nameof(AIEverythingTools.SearchLocalHybrid), "search_local_hybrid", true, false)]
    [InlineData(nameof(AIEverythingTools.GetIndexStatus), "aieverything_index_status", true, false)]
    public void Content_tools_declare_exact_names_and_mutation_annotations(
        string methodName,
        string expectedName,
        bool readOnly,
        bool destructive)
    {
        var method = typeof(AIEverythingTools).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)!;
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Equal(expectedName, attribute.Name);
        Assert.Equal(readOnly, attribute.ReadOnly);
        Assert.Equal(destructive, attribute.Destructive);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.True(attribute.UseStructuredContent);
    }
}
