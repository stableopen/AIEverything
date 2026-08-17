namespace AIEverything.Desktop.Ranking;

public static class DesktopRankingFeedbackFactory
{
    public static RankingFeedback Create(
        DesktopSearchItem item,
        DesktopSearchMode mode,
        RankingActionType action,
        int presentedRank,
        bool previewedBeforeAction)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (presentedRank < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(presentedRank));
        }

        var baselineRank = item.BaselineIndex >= 0
            ? item.BaselineIndex + 1
            : presentedRank;
        return new RankingFeedback(
            item.FullPath,
            item.Extension,
            mode,
            item.MatchSource,
            action,
            baselineRank,
            presentedRank,
            previewedBeforeAction);
    }
}
