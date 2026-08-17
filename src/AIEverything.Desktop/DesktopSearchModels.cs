using AIEverything.Core;

namespace AIEverything.Desktop;

public enum DesktopSearchMode
{
    Hybrid,
    FileName,
    Content
}

public sealed record DesktopSearchRequest(
    string Query,
    DesktopSearchMode Mode = DesktopSearchMode.Hybrid,
    string? RootPath = null,
    int Limit = 100,
    IReadOnlyList<string>? Extensions = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null);

public sealed record DesktopSearchItem(
    string Name,
    string FullPath,
    string Extension,
    SearchItemKind Kind,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Snippet,
    string MatchSource,
    string? Detail = null,
    string? CopyText = null,
    int? StartLine = null,
    int? EndLine = null,
    string? HeadingPath = null,
    string? JsonPath = null,
    string? LocationLabel = null,
    bool Imported = false,
    Ranking.RankingProtectedTier RankingTier = Ranking.RankingProtectedTier.Eligible,
    int BaselineIndex = -1,
    double? BaselineScore = null,
    string? RankingReason = null);

public sealed record DesktopSearchResponse(
    string Query,
    DesktopSearchMode Mode,
    int TotalResults,
    int ReturnedResults,
    double QueryDurationMs,
    IReadOnlyList<DesktopSearchItem> Items);
