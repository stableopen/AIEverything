using AIEverything.Core;
using System.Text.Json.Serialization;

namespace AIEverything.ContentClient;

public sealed record HybridSearchRequest(
    string Query,
    string? Path = null,
    IReadOnlyList<string>? Extensions = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    int Limit = 20,
    int Offset = 0);

public sealed record HybridSearchItem(
    string Name,
    string FullPath,
    string Extension,
    SearchItemKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Snippet,
    double Score,
    string MatchSource,
    int? StartLine = null,
    int? EndLine = null,
    string? HeadingPath = null,
    string? JsonPath = null,
    string? LocationLabel = null,
    bool Imported = false,
    [property: JsonIgnore]
    SearchNoiseLevel NameNoise = SearchNoiseLevel.Normal);

public sealed record HybridSearchResponse(
    string Query,
    int TotalResults,
    int ReturnedResults,
    int Offset,
    int Limit,
    double QueryDurationMs,
    IReadOnlyList<HybridSearchItem> Items);
