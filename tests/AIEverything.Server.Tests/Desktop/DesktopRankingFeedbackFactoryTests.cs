using AIEverything.Core;
using AIEverything.Desktop;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Server.Tests.Desktop;

public sealed class DesktopRankingFeedbackFactoryTests
{
    [Fact]
    public void Creates_one_based_feedback_from_the_original_and_presented_ranks()
    {
        var item = new DesktopSearchItem(
            "notes.md", @"D:\docs\notes.md", "md", SearchItemKind.File,
            10, DateTimeOffset.UtcNow, "text", "content", BaselineIndex: 6);

        var feedback = DesktopRankingFeedbackFactory.Create(
            item,
            DesktopSearchMode.Hybrid,
            RankingActionType.Open,
            presentedRank: 2,
            previewedBeforeAction: true);

        Assert.Equal(7, feedback.BaselineRank);
        Assert.Equal(2, feedback.PresentedRank);
        Assert.Equal("content", feedback.MatchSource);
        Assert.True(feedback.PreviewedBeforeAction);
    }

    [Fact]
    public void Missing_baseline_uses_the_presented_rank_and_invalid_rank_is_rejected()
    {
        var item = new DesktopSearchItem(
            "notes.md", @"D:\docs\notes.md", "md", SearchItemKind.File,
            10, DateTimeOffset.UtcNow, null, "name");

        Assert.Equal(3, DesktopRankingFeedbackFactory.Create(
            item, DesktopSearchMode.FileName, RankingActionType.Locate, 3, false).BaselineRank);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopRankingFeedbackFactory.Create(
            item, DesktopSearchMode.FileName, RankingActionType.Open, 0, false));
    }
}
