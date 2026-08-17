using AIEverything.Desktop;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Server.Tests.Desktop;

public sealed class DesktopRankingPresentationGateTests
{
    [Fact]
    public void Query_or_mode_change_rejects_stale_baseline_and_finally()
    {
        var gate = new DesktopRankingPresentationGate();
        var first = gate.BeginSearch("alpha", DesktopSearchMode.Hybrid);

        gate.InvalidateQuery();
        var second = gate.BeginSearch("beta", DesktopSearchMode.FileName);

        Assert.False(gate.IsCurrent(first, "alpha", DesktopSearchMode.Hybrid));
        Assert.False(gate.CanFinalize(first));
        Assert.True(gate.IsCurrent(second, "beta", DesktopSearchMode.FileName));
        Assert.True(gate.CanFinalize(second));
        Assert.False(gate.IsCurrent(second, "beta", DesktopSearchMode.Hybrid));
    }

    [Fact]
    public void Enhancement_applies_only_without_query_change_or_user_interaction()
    {
        var gate = new DesktopRankingPresentationGate();
        var search = gate.BeginSearch("alpha", DesktopSearchMode.Hybrid);
        var enhancement = gate.CaptureEnhancement(search);

        Assert.True(gate.CanApplyEnhancement(
            enhancement, "alpha", DesktopSearchMode.Hybrid));

        gate.MarkInteraction();
        Assert.False(gate.CanApplyEnhancement(
            enhancement, "alpha", DesktopSearchMode.Hybrid));

        var next = gate.BeginSearch("alpha", DesktopSearchMode.Hybrid);
        var nextEnhancement = gate.CaptureEnhancement(next);
        gate.InvalidateQuery();
        Assert.False(gate.CanApplyEnhancement(
            nextEnhancement, "alpha", DesktopSearchMode.Hybrid));
    }
}
