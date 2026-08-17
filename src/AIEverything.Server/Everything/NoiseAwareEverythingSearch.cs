using AIEverything.Core;

namespace AIEverything.Everything;

public static class NoiseAwareEverythingSearch
{
    private const int PageSize = 100;
    private const int MaximumPages = 5;

    public static SearchResponse Search(
        IEverythingSearchService everything,
        StructuredSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(everything);
        ArgumentNullException.ThrowIfNull(request);

        var normal = new List<SearchItem>();
        var soft = new List<SearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double durationMs = 0;

        for (var pageIndex = 0; pageIndex < MaximumPages; pageIndex++)
        {
            var offset = checked(request.Offset + pageIndex * PageSize);
            var page = everything.Search(request with { Limit = PageSize, Offset = offset });
            durationMs += page.QueryDurationMs;

            foreach (var item in page.Items)
            {
                if (!seen.Add(item.FullPath))
                {
                    continue;
                }

                switch (SearchResultRanking.ClassifyNoise(request.Query, item))
                {
                    case SearchNoiseLevel.Normal:
                        normal.Add(item);
                        break;
                    case SearchNoiseLevel.SoftRanked:
                        soft.Add(item);
                        break;
                    case SearchNoiseLevel.HardFiltered:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(item));
                }
            }

            if (normal.Count >= request.Limit ||
                page.Items.Count < PageSize ||
                offset + page.Items.Count >= page.TotalResults)
            {
                break;
            }
        }

        var items = Rank(normal, request.Query)
            .Concat(Rank(soft, request.Query))
            .Take(request.Limit)
            .ToArray();
        return new SearchResponse(
            request.Query,
            checked((uint)items.Length),
            items.Length,
            request.Offset,
            request.Limit,
            durationMs,
            items);
    }

    private static IOrderedEnumerable<SearchItem> Rank(
        IEnumerable<SearchItem> items,
        string query) => items
        .OrderBy(item => SearchResultRanking.NameMatchRank(query, item.Name))
        .ThenBy(item => item.Name.Length)
        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
}
