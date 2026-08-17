using AIEverything.Core;

namespace AIEverything.Everything;

public sealed record AIEverythingStatus(
    bool Ready,
    bool SdkLoaded,
    bool DatabaseLoaded,
    string EverythingVersion,
    uint NativeErrorCode,
    string? ErrorCode,
    string Message,
    string? CorrectiveAction);

public interface IEverythingSearchService
{
    SearchResponse Search(StructuredSearchRequest request);

    SearchResponse SearchRaw(
        string query,
        int limit = 20,
        int offset = 0,
        SearchSortBy sortBy = SearchSortBy.Name,
        SearchSortDirection direction = SearchSortDirection.Asc);

    AIEverythingStatus GetStatus();
}
