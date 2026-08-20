namespace AIEverything.Desktop.Ranking;

public enum RankingProtectedTier
{
    Exact,
    Eligible,
    Soft
}

public enum RankingActionType
{
    Open,
    Locate,
    CopyReference,
    PreviewConfirmed
}

public enum LocalModelStatus
{
    Disabled,
    Ready,
    UnsupportedCpu,
    MissingAssets,
    HashMismatch,
    RuntimeUnavailable,
    InvalidModel,
    InferenceFailed,
    TimedOut
}

public sealed record RankingOptions(
    bool BehaviorEnabled,
    bool LocalModelEnabled,
    bool DeepSeekEnabled,
    bool DeepSeekDisclosureAccepted)
{
    public static RankingOptions Default { get; } = new(true, true, true, true);
}

public sealed record RankingIdentity(string FullPath, string Extension);

public sealed record BehaviorAffinity(double Score, string? Reason)
{
    public int Promotion => Score <= 0
        ? 0
        : Math.Clamp((int)Math.Ceiling(Score), 1, 10);
}

public sealed record RankingFeedback(
    string FullPath,
    string Extension,
    DesktopSearchMode Mode,
    string MatchSource,
    RankingActionType Action,
    int BaselineRank,
    int PresentedRank,
    bool PreviewedBeforeAction = false);

public sealed record LocalSemanticCandidate(
    string Id,
    string Name,
    string FullPath,
    string? Snippet,
    string MatchSource,
    RankingProtectedTier Tier,
    int BehaviorIndex);

public sealed record LocalSemanticRequest(
    string Query,
    IReadOnlyList<LocalSemanticCandidate> Candidates);

public sealed record LocalSemanticResult(
    LocalModelStatus Status,
    IReadOnlyDictionary<string, double> Scores,
    double DurationMs = 0,
    string? Detail = null);

public sealed record CloudRerankCandidate(
    string Id,
    string Name,
    string FullPath,
    string? Snippet,
    string MatchSource,
    RankingProtectedTier Tier);

public sealed record CloudRerankRequest(
    string Query,
    IReadOnlyList<CloudRerankCandidate> Candidates);

public sealed record CloudRerankResult(IReadOnlyList<string> TopFiveIds);

public sealed record RankingRun(
    DesktopSearchResponse Immediate,
    Task<DesktopSearchResponse?> Enhancement);

public interface IRankingBehaviorStore
{
    ValueTask<IReadOnlyDictionary<string, BehaviorAffinity>> ReadAsync(
        IReadOnlyList<RankingIdentity> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask RecordAsync(RankingFeedback feedback, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

public interface ILocalSemanticReranker
{
    ValueTask<LocalSemanticResult> ScoreAsync(
        LocalSemanticRequest request,
        CancellationToken cancellationToken = default);
}

public interface ICloudReranker
{
    ValueTask<CloudRerankResult?> RerankAsync(
        CloudRerankRequest request,
        CancellationToken cancellationToken = default);
}
