namespace AIEverything.Core;

public enum SearchItemKind
{
    Any,
    File,
    Folder
}

public enum SearchSortBy
{
    Name,
    Path,
    Size,
    Modified
}

public enum SearchSortDirection
{
    Asc,
    Desc
}

public enum EverythingSort : uint
{
    NameAscending = 1,
    NameDescending = 2,
    PathAscending = 3,
    PathDescending = 4,
    SizeAscending = 5,
    SizeDescending = 6,
    DateModifiedAscending = 13,
    DateModifiedDescending = 14
}

public sealed record StructuredSearchRequest(
    string Query = "",
    string? Path = null,
    IReadOnlyList<string>? Extensions = null,
    SearchItemKind Kind = SearchItemKind.Any,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    SearchSortBy SortBy = SearchSortBy.Name,
    SearchSortDirection SortDirection = SearchSortDirection.Asc,
    int Limit = 20,
    int Offset = 0);

public sealed record CompiledEverythingQuery(
    string Query,
    EverythingSort Sort,
    int Limit,
    int Offset);

public sealed record SearchItem(
    string Name,
    string FullPath,
    string ParentPath,
    string Extension,
    SearchItemKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt,
    FileAttributes Attributes = 0);

public sealed record SearchResponse(
    string Query,
    uint TotalResults,
    int ReturnedResults,
    int Offset,
    int Limit,
    double QueryDurationMs,
    IReadOnlyList<SearchItem> Items);
