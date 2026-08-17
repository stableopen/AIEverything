using AIEverything.Content.Contracts;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Desktop.Ranking;
using AIEverything.Everything;

namespace AIEverything.Desktop;

public sealed class StandaloneSearchService
{
    private readonly IEverythingSearchService _everything;
    private readonly IContentSearchService _content;
    private readonly HybridSearchService _hybrid;

    public StandaloneSearchService(
        IEverythingSearchService everything,
        IContentSearchService content,
        HybridSearchService hybrid)
    {
        _everything = everything ?? throw new ArgumentNullException(nameof(everything));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _hybrid = hybrid ?? throw new ArgumentNullException(nameof(hybrid));
    }

    public AIEverythingStatus GetEverythingStatus() => _everything.GetStatus();

    public Task<ContentIndexStatus> GetIndexStatusAsync(
        CancellationToken cancellationToken = default) =>
        _content.GetStatusAsync(cancellationToken);

    public Task<IReadOnlyList<ContentIndexFailure>> ListIndexFailuresAsync(
        CancellationToken cancellationToken = default) =>
        _content.ListFailuresAsync(cancellationToken);

    public Task<ContentIndexStatus> ConfigureIndexAsync(bool disclosureAccepted, bool enabled,
        CancellationToken cancellationToken = default) =>
        _content.ConfigureAsync(disclosureAccepted, enabled, cancellationToken);

    public Task<ContentIndexStatus> SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default) =>
        _content.SetPausedAsync(paused, cancellationToken);

    public Task<ContentIndexStatus> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        _content.SynchronizeAsync(cancellationToken);

    public async Task<DesktopSearchResponse> SearchAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        return request.Mode switch
        {
            DesktopSearchMode.FileName => await SearchFileNamesAsync(request, cancellationToken),
            DesktopSearchMode.Content => await SearchContentAsync(request, cancellationToken),
            DesktopSearchMode.Hybrid => await SearchHybridAsync(request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown search mode.")
        };
    }

    private async Task<DesktopSearchResponse> SearchFileNamesAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        SearchResponse response;
        try
        {
            response = await Task.Run(
                () => NoiseAwareEverythingSearch.Search(_everything, new StructuredSearchRequest(
                    Query: request.Query,
                    Path: null,
                    Extensions: null,
                    Kind: SearchItemKind.Any,
                    ModifiedAfter: null,
                    ModifiedBefore: null,
                    Limit: request.Limit)),
                cancellationToken);
        }
        catch (AIEverythingException)
        {
            return await SearchIndexedFileNamesAsync(request, cancellationToken);
        }

        var items = response.Items.Select((item, index) => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            item.Kind,
            item.Kind == SearchItemKind.Folder ? null : item.Size,
            item.ModifiedAt,
            null,
            "name",
            RankingTier: GetFileNameTier(request.Query, item),
            BaselineIndex: index)).ToArray();
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.FileName,
            checked((int)Math.Min(int.MaxValue, response.TotalResults)),
            response.ReturnedResults,
            response.QueryDurationMs,
            items);
    }

    private async Task<DesktopSearchResponse> SearchIndexedFileNamesAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _content.SearchAsync(new ContentSearchRequest(
            request.Query,
            null,
            null,
            null,
            null,
            Limit: request.Limit,
            Field: ContentSearchField.Title), cancellationToken);
        var items = response.Items.Select((item, index) => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            SearchItemKind.File,
            item.Size,
            item.ModifiedAt,
            null,
            "name",
            RankingTier: SearchResultRanking.IsExactQuery(
                request.Query, item.Name, item.FullPath)
                ? RankingProtectedTier.Exact
                : RankingProtectedTier.Eligible,
            BaselineIndex: index)).ToArray();
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.FileName,
            response.TotalResults,
            response.ReturnedResults,
            response.QueryDurationMs,
            items);
    }

    private async Task<DesktopSearchResponse> SearchContentAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _content.SearchAsync(new ContentSearchRequest(
            request.Query,
            null,
            null,
            null,
            null,
            Limit: request.Limit,
            Field: ContentSearchField.Body), cancellationToken);
        var items = CollapseFileMatches(response.Items.Select(item => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            SearchItemKind.File,
            item.Size,
            item.ModifiedAt,
            item.Snippet,
            "content",
            StartLine: item.StartLine,
            EndLine: item.EndLine,
            HeadingPath: item.HeadingPath,
            JsonPath: item.JsonPath,
            LocationLabel: item.LocationLabel,
            Imported: item.Imported)));
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.Content,
            items.Length,
            items.Length,
            response.QueryDurationMs,
            items);
    }

    private async Task<DesktopSearchResponse> SearchHybridAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _hybrid.SearchAsync(new HybridSearchRequest(
            request.Query,
            null,
            null,
            null,
            null,
            Limit: request.Limit), cancellationToken);
        var items = CollapseFileMatches(response.Items.Select((item, index) => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            item.Kind,
            item.Size,
            item.ModifiedAt,
            item.Snippet,
            item.MatchSource,
            StartLine: item.StartLine,
            EndLine: item.EndLine,
            HeadingPath: item.HeadingPath,
            JsonPath: item.JsonPath,
            LocationLabel: item.LocationLabel,
            Imported: item.Imported,
            RankingTier: GetHybridTier(request.Query, item),
            BaselineIndex: index,
            BaselineScore: item.Score)));
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.Hybrid,
            items.Length,
            items.Length,
            response.QueryDurationMs,
            items);
    }

    private static DesktopSearchItem[] CollapseFileMatches(
        IEnumerable<DesktopSearchItem> items)
    {
        return items
            .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                var matches = group.ToArray();
                var primary = matches[0];
                if (matches.Length == 1)
                {
                    return primary with { BaselineIndex = index };
                }

                var hasNameMatch = matches.Any(item =>
                    item.MatchSource is "name" or "both");
                var hasContentMatch = matches.Any(item =>
                    item.MatchSource is "content" or "both");
                var matchSource = hasNameMatch && hasContentMatch
                    ? "both"
                    : hasNameMatch ? "name" : "content";
                var primaryLocation = primary.LocationLabel
                    ?? primary.HeadingPath
                    ?? (primary.StartLine is { } line ? $"lines {line}-{primary.EndLine ?? line}" : null);
                var location = string.IsNullOrWhiteSpace(primaryLocation)
                    ? $"{matches.Length} 处命中"
                    : $"{matches.Length} 处命中 · {primaryLocation}";

                return primary with
                {
                    MatchSource = matchSource,
                    LocationLabel = location,
                    RankingTier = matches.Min(item => item.RankingTier),
                    BaselineIndex = index,
                    BaselineScore = matches
                        .Where(item => item.BaselineScore.HasValue)
                        .Select(item => item.BaselineScore)
                        .Max()
                };
            })
            .ToArray();
    }

    private static void Validate(DesktopSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Search text is required.", nameof(request));
        }

        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Limit must be between 1 and 100.");
        }

    }

    private static RankingProtectedTier GetFileNameTier(string query, SearchItem item)
    {
        if (SearchResultRanking.IsExactQuery(query, item.Name, item.FullPath))
        {
            return RankingProtectedTier.Exact;
        }

        return SearchResultRanking.ClassifyNoise(query, item) == SearchNoiseLevel.SoftRanked
            ? RankingProtectedTier.Soft
            : RankingProtectedTier.Eligible;
    }

    private static RankingProtectedTier GetHybridTier(string query, HybridSearchItem item)
    {
        if (SearchResultRanking.IsExactQuery(query, item.Name, item.FullPath))
        {
            return RankingProtectedTier.Exact;
        }

        return item.MatchSource == "name" && item.NameNoise == SearchNoiseLevel.SoftRanked
            ? RankingProtectedTier.Soft
            : RankingProtectedTier.Eligible;
    }
}
