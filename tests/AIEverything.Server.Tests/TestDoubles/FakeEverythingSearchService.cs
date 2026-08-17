using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.Server.Tests.TestDoubles;

internal sealed class FakeEverythingSearchService : IEverythingSearchService
{
    public AIEverythingStatus Status { get; set; } = new(
        Ready: true,
        SdkLoaded: true,
        DatabaseLoaded: true,
        EverythingVersion: "1.4.1.1028",
        NativeErrorCode: 0,
        ErrorCode: null,
        Message: "Everything SDK and database are ready.",
        CorrectiveAction: null);

    public SearchResponse NextResponse { get; set; } = EmptyResponse();

    public Queue<double> QueryDurations { get; } = new();

    public Queue<SearchResponse> SearchResponses { get; } = new();

    public List<StructuredSearchRequest> StructuredRequests { get; } = [];

    public StructuredSearchRequest? LastStructuredRequest { get; private set; }

    public string? LastRawQuery { get; private set; }

    public int RawSearchCalls { get; private set; }

    public Exception? SearchException { get; set; }

    public SearchResponse Search(StructuredSearchRequest request)
    {
        LastStructuredRequest = request;
        StructuredRequests.Add(request);
        if (SearchException is not null)
        {
            throw SearchException;
        }

        var response = SearchResponses.Count > 0 ? SearchResponses.Dequeue() : NextResponse;
        return response with
        {
            Query = request.Query,
            Offset = request.Offset,
            Limit = request.Limit
        };
    }

    public SearchResponse SearchRaw(
        string query,
        int limit = 20,
        int offset = 0,
        SearchSortBy sortBy = SearchSortBy.Name,
        SearchSortDirection direction = SearchSortDirection.Asc)
    {
        RawSearchCalls++;
        LastRawQuery = query;
        if (SearchException is not null)
        {
            throw SearchException;
        }

        var duration = QueryDurations.Count > 0
            ? QueryDurations.Dequeue()
            : NextResponse.QueryDurationMs;
        return NextResponse with
        {
            Query = query,
            Offset = offset,
            Limit = limit,
            QueryDurationMs = duration
        };
    }

    public AIEverythingStatus GetStatus() => Status;

    private static SearchResponse EmptyResponse() => new(
        Query: string.Empty,
        TotalResults: 0,
        ReturnedResults: 0,
        Offset: 0,
        Limit: 20,
        QueryDurationMs: 0.25,
        Items: []);
}
