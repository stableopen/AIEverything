using AIEverything.Core;
using AIEverything.Everything;
using AIEverything.Server.Tests.TestDoubles;

namespace AIEverything.Server.Tests.Everything;

public sealed class EverythingSearchServiceTests
{
    [Fact]
    public void Search_returns_empty_success_instead_of_error()
    {
        var native = new FakeEverythingNativeApi
        {
            NextResult = new NativeSearchResult(0, [])
        };
        var service = new EverythingSearchService(native);

        var response = service.Search(new StructuredSearchRequest(Query: "missing-name"));

        Assert.Empty(response.Items);
        Assert.Equal((uint)0, response.TotalResults);
        Assert.Equal("\"missing-name\"", native.LastQuery!.Query);
        Assert.Equal(20, response.Limit);
        Assert.True(response.QueryDurationMs >= 0);
    }

    [Fact]
    public void Search_raw_preserves_everything_syntax()
    {
        var native = new FakeEverythingNativeApi();
        var service = new EverythingSearchService(native);

        service.SearchRaw("ext:pdf dm:thisweek", 7, 3, SearchSortBy.Modified, SearchSortDirection.Desc);

        Assert.Equal("ext:pdf dm:thisweek", native.LastQuery!.Query);
        Assert.Equal(EverythingSort.DateModifiedDescending, native.LastQuery.Sort);
        Assert.Equal(7, native.LastQuery.Limit);
        Assert.Equal(3, native.LastQuery.Offset);
    }

    [Fact]
    public void Search_populates_result_metadata_and_duration()
    {
        var item = new SearchItem(
            "report.pdf", @"C:\docs\report.pdf", @"C:\docs", "pdf",
            SearchItemKind.File, 42, DateTimeOffset.Parse("2026-07-15T10:00:00+08:00"));
        var native = new FakeEverythingNativeApi
        {
            NextResult = new NativeSearchResult(8, [item])
        };
        var service = new EverythingSearchService(native);

        var response = service.Search(new StructuredSearchRequest(Query: "report", Limit: 5, Offset: 2));

        Assert.Equal((uint)8, response.TotalResults);
        Assert.Equal(1, response.ReturnedResults);
        Assert.Equal(2, response.Offset);
        Assert.Equal(5, response.Limit);
        Assert.Same(item, response.Items[0]);
        Assert.True(response.QueryDurationMs >= 0);
    }

    [Fact]
    public void Search_maps_ipc_failure_to_everything_not_running()
    {
        var native = new FakeEverythingNativeApi
        {
            QueryException = new EverythingNativeException(2)
        };
        var service = new EverythingSearchService(native);

        var exception = Assert.Throws<AIEverythingException>(() =>
            service.Search(new StructuredSearchRequest(Query: "report")));

        Assert.Equal("EVERYTHING_NOT_RUNNING", exception.Code);
        Assert.Equal("Start Everything and retry.", exception.CorrectiveAction);
        Assert.Equal((uint)2, exception.NativeErrorCode);
    }

    [Fact]
    public void Search_maps_other_native_failures_to_query_failed()
    {
        var native = new FakeEverythingNativeApi
        {
            QueryException = new EverythingNativeException(9)
        };
        var service = new EverythingSearchService(native);

        var exception = Assert.Throws<AIEverythingException>(() =>
            service.Search(new StructuredSearchRequest(Query: "report")));

        Assert.Equal("QUERY_FAILED", exception.Code);
        Assert.Equal((uint)9, exception.NativeErrorCode);
    }

    [Fact]
    public void Search_maps_relative_path_to_invalid_path()
    {
        var service = new EverythingSearchService(new FakeEverythingNativeApi());

        var exception = Assert.Throws<AIEverythingException>(() =>
            service.Search(new StructuredSearchRequest(Path: @"relative\folder")));

        Assert.Equal("INVALID_PATH", exception.Code);
        Assert.Equal("Provide an absolute Windows path.", exception.CorrectiveAction);
    }

    [Fact]
    public void Search_maps_invalid_limit_to_invalid_query()
    {
        var service = new EverythingSearchService(new FakeEverythingNativeApi());

        var exception = Assert.Throws<AIEverythingException>(() =>
            service.Search(new StructuredSearchRequest(Limit: 101)));

        Assert.Equal("INVALID_QUERY", exception.Code);
    }

    [Fact]
    public void Status_is_read_only_and_reports_ready_version()
    {
        var native = new FakeEverythingNativeApi();
        var service = new EverythingSearchService(native);

        var status = service.GetStatus();

        Assert.True(status.Ready);
        Assert.Equal("1.4.1.1028", status.EverythingVersion);
        Assert.Equal(1, native.StatusCalls);
        Assert.Equal(0, native.QueryCalls);
    }

    [Fact]
    public void Status_maps_missing_sdk_to_stable_error()
    {
        var native = new FakeEverythingNativeApi
        {
            Status = new EverythingRuntimeStatus(false, false, 0, 0, 0, 0, 0, "missing")
        };
        var service = new EverythingSearchService(native);

        var status = service.GetStatus();

        Assert.False(status.Ready);
        Assert.Equal("EVERYTHING_SDK_LOAD_FAILED", status.ErrorCode);
        Assert.Equal("Reinstall AIEverything to restore Everything64.dll.", status.CorrectiveAction);
    }

    [Fact]
    public void Status_maps_unloaded_database_without_ipc_error()
    {
        var native = new FakeEverythingNativeApi
        {
            Status = new EverythingRuntimeStatus(true, false, 1, 4, 1, 1028, 0, null)
        };
        var service = new EverythingSearchService(native);

        var status = service.GetStatus();

        Assert.Equal("EVERYTHING_DATABASE_NOT_LOADED", status.ErrorCode);
        Assert.Equal("Wait for Everything indexing to finish and retry.", status.CorrectiveAction);
    }

    [Fact]
    public void Unsupported_platform_returns_stable_status_and_blocks_search()
    {
        var native = new FakeEverythingNativeApi();
        var service = new EverythingSearchService(native, isSupportedPlatform: () => false);

        var status = service.GetStatus();
        var exception = Assert.Throws<AIEverythingException>(() =>
            service.Search(new StructuredSearchRequest(Query: "report")));

        Assert.Equal("UNSUPPORTED_PLATFORM", status.ErrorCode);
        Assert.Equal("UNSUPPORTED_PLATFORM", exception.Code);
        Assert.Equal(0, native.StatusCalls);
        Assert.Equal(0, native.QueryCalls);
    }
}
