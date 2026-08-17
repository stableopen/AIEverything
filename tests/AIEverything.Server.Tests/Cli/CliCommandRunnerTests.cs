using System.Text.Json;
using AIEverything.Cli;
using AIEverything.Content.Contracts;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Everything;
using AIEverything.Server.Tests.TestDoubles;

namespace AIEverything.Server.Tests.Cli;

public sealed class CliCommandRunnerTests
{
    [Fact]
    public async Task Doctor_writes_status_as_camel_case_json()
    {
        var output = new StringWriter();
        var runner = new CliCommandRunner(new FakeEverythingSearchService());

        var exitCode = await runner.RunAsync(["doctor"], output, TextWriter.Null, default);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.True(json.RootElement.GetProperty("databaseLoaded").GetBoolean());
        Assert.Equal("1.4.1.1028", json.RootElement.GetProperty("everythingVersion").GetString());
    }

    [Fact]
    public async Task Search_parses_structured_filters_and_writes_results()
    {
        var service = new FakeEverythingSearchService();
        var output = new StringWriter();
        var runner = new CliCommandRunner(service);

        var exitCode = await runner.RunAsync(
            [
                "search", "quarterly report",
                "--path", @"C:\docs",
                "--ext", "pdf",
                "--ext", ".docx",
                "--kind", "file",
                "--sort", "modified",
                "--desc",
                "--limit", "5",
                "--offset", "2"
            ],
            output,
            TextWriter.Null,
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal("quarterly report", service.LastStructuredRequest!.Query);
        Assert.Equal(@"C:\docs", service.LastStructuredRequest.Path);
        Assert.Equal(["pdf", ".docx"], service.LastStructuredRequest.Extensions);
        Assert.Equal(SearchItemKind.File, service.LastStructuredRequest.Kind);
        Assert.Equal(SearchSortBy.Modified, service.LastStructuredRequest.SortBy);
        Assert.Equal(SearchSortDirection.Desc, service.LastStructuredRequest.SortDirection);
        Assert.Equal(5, service.LastStructuredRequest.Limit);
        Assert.Equal(2, service.LastStructuredRequest.Offset);
        JsonDocument.Parse(output.ToString()).Dispose();
    }

    [Fact]
    public async Task Invalid_arguments_return_exit_code_two_and_json_error()
    {
        var error = new StringWriter();
        var runner = new CliCommandRunner(new FakeEverythingSearchService());

        var exitCode = await runner.RunAsync(
            ["search", "report", "--kind", "device"],
            TextWriter.Null,
            error,
            default);

        Assert.Equal(2, exitCode);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal("INVALID_ARGUMENTS", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Runtime_search_failure_returns_exit_code_one_and_stable_error()
    {
        var service = new FakeEverythingSearchService
        {
            SearchException = new AIEverythingException(
                "EVERYTHING_NOT_RUNNING",
                "Everything IPC is unavailable.",
                "Start Everything and retry.",
                2)
        };
        var error = new StringWriter();
        var runner = new CliCommandRunner(service);

        var exitCode = await runner.RunAsync(
            ["search", "report"], TextWriter.Null, error, default);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal("EVERYTHING_NOT_RUNNING", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(2u, json.RootElement.GetProperty("nativeErrorCode").GetUInt32());
    }

    [Fact]
    public async Task Benchmark_discards_warmup_and_calculates_percentiles()
    {
        var service = new FakeEverythingSearchService();
        foreach (var duration in new[] { 999d, 1d, 2d, 3d, 4d, 100d })
        {
            service.QueryDurations.Enqueue(duration);
        }

        var output = new StringWriter();
        var runner = new CliCommandRunner(service);

        var exitCode = await runner.RunAsync(
            ["benchmark", "--iterations", "5"], output, TextWriter.Null, default);

        Assert.Equal(0, exitCode);
        Assert.Equal(6, service.RawSearchCalls);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(5, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("minMs").GetDouble());
        Assert.Equal(3, json.RootElement.GetProperty("medianMs").GetDouble());
        Assert.Equal(100, json.RootElement.GetProperty("p95Ms").GetDouble());
        Assert.Equal(100, json.RootElement.GetProperty("maxMs").GetDouble());
        Assert.True(json.RootElement.GetProperty("medianUnder10Ms").GetBoolean());
        Assert.False(json.RootElement.GetProperty("p95Under50Ms").GetBoolean());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("many")]
    public async Task Benchmark_rejects_invalid_iteration_count(string value)
    {
        var runner = new CliCommandRunner(new FakeEverythingSearchService());

        var exitCode = await runner.RunAsync(
            ["benchmark", "--iterations", value],
            TextWriter.Null,
            TextWriter.Null,
            default);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Unknown_command_returns_exit_code_two()
    {
        var runner = new CliCommandRunner(new FakeEverythingSearchService());

        var exitCode = await runner.RunAsync(
            ["erase"], TextWriter.Null, TextWriter.Null, default);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Content_search_parses_filters_and_writes_daemon_results()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService
        {
            NextResponse = new ContentSearchResponse(
                "正文", 0, 0, 0, 5, 1, [])
        };
        var runner = new CliCommandRunner(
            everything, content, new HybridSearchService(everything, content));
        var output = new StringWriter();

        var exitCode = await runner.RunAsync(
            [
                "content-search", "正文搜索",
                "--path", @"C:\docs",
                "--ext", "pdf",
                "--after", "2026-07-01T00:00:00+08:00",
                "--before", "2026-07-16T00:00:00+08:00",
                "--limit", "5",
                "--offset", "2"
            ],
            output,
            TextWriter.Null,
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal("正文搜索", content.LastSearch!.Query);
        Assert.Equal(@"C:\docs", content.LastSearch.RootPath);
        Assert.Equal(["pdf"], content.LastSearch.Extensions);
        Assert.Equal(5, content.LastSearch.Limit);
        Assert.Equal(2, content.LastSearch.Offset);
        Assert.Equal(ContentSearchField.Body, content.LastSearch.Field);
        JsonDocument.Parse(output.ToString()).Dispose();
    }

    [Fact]
    public async Task Content_index_commands_expose_pause_and_sync_without_manual_roots()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService();
        var runner = new CliCommandRunner(
            everything, content, new HybridSearchService(everything, content));

        var pauseExit = await runner.RunAsync(
            ["content-index", "pause"],
            TextWriter.Null,
            TextWriter.Null,
            default);
        var syncExit = await runner.RunAsync(
            ["content-index", "sync"],
            TextWriter.Null,
            TextWriter.Null,
            default);

        Assert.Equal(0, pauseExit);
        Assert.Equal(0, syncExit);
    }

    [Fact]
    public async Task Content_failure_returns_exit_code_one_and_stable_error()
    {
        var everything = new FakeEverythingSearchService();
        var content = new FakeContentSearchService
        {
            Exception = new AIEverything.Content.Errors.ContentIndexException(
                "CONTENT_SERVICE_UNAVAILABLE", "missing", "start daemon")
        };
        var runner = new CliCommandRunner(
            everything, content, new HybridSearchService(everything, content));
        var error = new StringWriter();

        var exitCode = await runner.RunAsync(
            ["content-search", "needle"], TextWriter.Null, error, default);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal("CONTENT_SERVICE_UNAVAILABLE", json.RootElement.GetProperty("code").GetString());
    }
}
