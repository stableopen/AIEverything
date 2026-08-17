namespace AIEverything.Desktop.Ranking;

public sealed record RankingSearchLease(
    long QueryVersion,
    string Query,
    DesktopSearchMode Mode);

public sealed record RankingEnhancementLease(
    RankingSearchLease Search,
    long InteractionVersion);

public sealed class DesktopRankingPresentationGate
{
    private long _queryVersion;
    private long _interactionVersion;

    public RankingSearchLease BeginSearch(string query, DesktopSearchMode mode)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required.", nameof(query));
        }

        return new RankingSearchLease(
            Interlocked.Increment(ref _queryVersion), query, mode);
    }

    public void InvalidateQuery() => Interlocked.Increment(ref _queryVersion);

    public void MarkInteraction() => Interlocked.Increment(ref _interactionVersion);

    public RankingEnhancementLease CaptureEnhancement(RankingSearchLease search)
    {
        ArgumentNullException.ThrowIfNull(search);
        return new RankingEnhancementLease(search, Volatile.Read(ref _interactionVersion));
    }

    public bool IsCurrent(
        RankingSearchLease search,
        string currentQuery,
        DesktopSearchMode currentMode) =>
        search.QueryVersion == Volatile.Read(ref _queryVersion) &&
        search.Mode == currentMode &&
        string.Equals(search.Query, currentQuery.Trim(), StringComparison.Ordinal);

    public bool CanFinalize(RankingSearchLease search) =>
        search.QueryVersion == Volatile.Read(ref _queryVersion);

    public bool CanApplyEnhancement(
        RankingEnhancementLease enhancement,
        string currentQuery,
        DesktopSearchMode currentMode) =>
        IsCurrent(enhancement.Search, currentQuery, currentMode) &&
        enhancement.InteractionVersion == Volatile.Read(ref _interactionVersion);
}
