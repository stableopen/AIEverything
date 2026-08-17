using AIEverything.Core;
using AIEverything.Desktop;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Server.Tests.Desktop;

public sealed class DesktopRankingCoordinatorTests
{
    [Fact]
    public async Task Cold_start_preserves_the_existing_order_exactly()
    {
        var baseline = Response(
            Item("one.txt", @"D:\docs\one.txt", RankingProtectedTier.Eligible),
            Item("two.txt", @"D:\docs\two.txt", RankingProtectedTier.Eligible),
            Item("cache.dll", @"D:\work\node_modules\cache.dll", RankingProtectedTier.Soft));
        var coordinator = Coordinator();

        var run = await coordinator.StartAsync(baseline, RankingOptions.Default);

        Assert.Equal(baseline.Items.Select(item => item.FullPath),
            run.Immediate.Items.Select(item => item.FullPath));
        Assert.Null(await run.Enhancement);
    }

    [Fact]
    public async Task Behavior_can_promote_inside_eligible_but_never_above_exact_or_move_soft_ahead()
    {
        var exact = Item("needle.txt", @"D:\docs\needle.txt", RankingProtectedTier.Exact);
        var ordinary = Item("project-needle.txt", @"D:\project\project-needle.txt", RankingProtectedTier.Eligible);
        var frequent = Item("notes.txt", @"D:\docs\notes.txt", RankingProtectedTier.Eligible);
        var soft = Item("needle.dll", @"D:\build\needle.dll", RankingProtectedTier.Soft);
        var store = new FakeBehaviorStore(new Dictionary<string, BehaviorAffinity>(StringComparer.OrdinalIgnoreCase)
        {
            [frequent.FullPath] = new(10, "你最近常用"),
            [soft.FullPath] = new(10, "你最近常用")
        });

        var run = await Coordinator(store).StartAsync(Response(exact, ordinary, frequent, soft),
            RankingOptions.Default with { LocalModelEnabled = false });

        Assert.Equal([exact.FullPath, frequent.FullPath, ordinary.FullPath, soft.FullPath],
            run.Immediate.Items.Select(item => item.FullPath));
        Assert.Equal("你最近常用", run.Immediate.Items[1].RankingReason);
    }

    [Fact]
    public async Task Behavior_reads_and_reorders_only_baseline_top_ten_and_preserves_the_tail_instances()
    {
        var items = Enumerable.Range(1, 12)
            .Select(index => Item($"item-{index}.txt", $@"D:\docs\item-{index}.txt",
                RankingProtectedTier.Eligible))
            .ToArray();
        var baseline = Response(items);
        var store = new FakeBehaviorStore(new Dictionary<string, BehaviorAffinity>(StringComparer.OrdinalIgnoreCase)
        {
            [items[9].FullPath] = new(10, "recent"),
            [items[11].FullPath] = new(10, "recent")
        });

        var run = await Coordinator(store).StartAsync(
            baseline,
            RankingOptions.Default with { LocalModelEnabled = false });

        var read = Assert.Single(store.ReadRequests);
        Assert.Equal(baseline.Items.Take(10).Select(item => item.FullPath),
            read.Select(candidate => candidate.FullPath));
        Assert.Equal(items[9].FullPath, run.Immediate.Items[0].FullPath);
        Assert.Same(baseline.Items[10], run.Immediate.Items[10]);
        Assert.Same(baseline.Items[11], run.Immediate.Items[11]);
    }

    [Fact]
    public async Task Behavior_disabled_never_reads_or_records_the_store()
    {
        var first = Item("one.txt", @"D:\docs\one.txt", RankingProtectedTier.Eligible);
        var second = Item("two.txt", @"D:\docs\two.txt", RankingProtectedTier.Eligible);
        var store = new FakeBehaviorStore();
        var coordinator = Coordinator(store);
        var options = RankingOptions.Default with
        {
            BehaviorEnabled = false,
            LocalModelEnabled = false
        };

        _ = await coordinator.StartAsync(Response(first, second), options);
        await coordinator.RecordAsync(
            new RankingFeedback(
                first.FullPath,
                first.Extension,
                DesktopSearchMode.Hybrid,
                first.MatchSource,
                RankingActionType.Open,
                BaselineRank: 1,
                PresentedRank: 1),
            options);

        Assert.Empty(store.ReadRequests);
        Assert.Empty(store.RecordRequests);
    }

    [Fact]
    public async Task Local_model_selects_top_five_from_top_ten_and_never_changes_item_eleven()
    {
        var items = Enumerable.Range(1, 12)
            .Select(index => Item($"item-{index}.txt", $@"D:\docs\item-{index}.txt",
                RankingProtectedTier.Eligible))
            .ToArray();
        var scores = Enumerable.Range(1, 10)
            .ToDictionary(index => $"c{index - 1}", index => (double)index, StringComparer.Ordinal);
        var local = new FakeLocalReranker(new LocalSemanticResult(LocalModelStatus.Ready, scores));

        var run = await Coordinator(local: local).StartAsync(Response(items), RankingOptions.Default);
        var enhanced = Assert.IsType<DesktopSearchResponse>(await run.Enhancement);

        Assert.Equal(items[9].FullPath, enhanced.Items[0].FullPath);
        Assert.Equal(items[8].FullPath, enhanced.Items[1].FullPath);
        Assert.Equal("\u672c\u5730\u8bed\u4e49\u5339\u914d", enhanced.Items[0].RankingReason);
        Assert.Equal(items[10].FullPath, enhanced.Items[10].FullPath);
        Assert.Equal(items[11].FullPath, enhanced.Items[11].FullPath);
        Assert.Equal(10, Assert.Single(local.Requests).Candidates.Count);
    }

    [Fact]
    public async Task DeepSeek_is_used_only_after_valid_low_confidence_local_scores()
    {
        var items = Enumerable.Range(1, 10)
            .Select(index => Item($"item-{index}.txt", $@"D:\docs\item-{index}.txt",
                RankingProtectedTier.Eligible))
            .ToArray();
        var closeScores = Enumerable.Range(0, 10)
            .ToDictionary(index => $"c{index}", index => 1d - index * 0.001d, StringComparer.Ordinal);
        var local = new FakeLocalReranker(new LocalSemanticResult(LocalModelStatus.Ready, closeScores));
        var cloud = new FakeCloudReranker(["c4", "c3", "c2", "c1", "c0"]);
        var options = RankingOptions.Default with
        {
            DeepSeekEnabled = true,
            DeepSeekDisclosureAccepted = true
        };

        var run = await Coordinator(local: local, cloud: cloud).StartAsync(Response(items), options);
        var enhanced = Assert.IsType<DesktopSearchResponse>(await run.Enhancement);

        Assert.Single(cloud.Requests);
        Assert.Equal(items[4].FullPath, enhanced.Items[0].FullPath);
        Assert.Equal("AI\u4f18\u5316", enhanced.Items[0].RankingReason);
        Assert.Equal(items[9].FullPath, enhanced.Items[9].FullPath);
    }

    [Fact]
    public async Task DeepSeek_requires_at_least_three_eligible_candidates()
    {
        var items = new[]
        {
            Item("one.txt", @"D:\docs\one.txt", RankingProtectedTier.Eligible),
            Item("two.txt", @"D:\docs\two.txt", RankingProtectedTier.Eligible),
            Item("soft.dll", @"D:\build\soft.dll", RankingProtectedTier.Soft)
        };
        var local = new FakeLocalReranker(new LocalSemanticResult(
            LocalModelStatus.Ready,
            new Dictionary<string, double> { ["c0"] = 1, ["c1"] = 0.999, ["c2"] = 0 }));
        var cloud = new FakeCloudReranker(["c1", "c0"]);

        var run = await Coordinator(local: local, cloud: cloud).StartAsync(
            Response(items), CloudEnabled());

        _ = await run.Enhancement;
        Assert.Empty(cloud.Requests);
    }

    [Fact]
    public async Task Duplicate_names_allow_cloud_even_when_local_scores_are_well_separated()
    {
        var items = new[]
        {
            Item("report.md", @"D:\finance\report.md", RankingProtectedTier.Eligible),
            Item("report.md", @"D:\projects\report.md", RankingProtectedTier.Eligible),
            Item("notes.md", @"D:\docs\notes.md", RankingProtectedTier.Eligible)
        };
        var cloud = new FakeCloudReranker(["c1"]);

        var run = await Coordinator(local: HighConfidenceLocal(), cloud: cloud).StartAsync(
            Response(items), CloudEnabled());

        _ = await run.Enhancement;
        Assert.Single(cloud.Requests);
    }

    [Fact]
    public async Task Mixed_name_and_content_evidence_allows_cloud_when_scores_are_well_separated()
    {
        var items = new[]
        {
            Item("one.md", @"D:\docs\one.md", RankingProtectedTier.Eligible),
            Item("two.md", @"D:\docs\two.md", RankingProtectedTier.Eligible) with
            {
                MatchSource = "content"
            },
            Item("three.md", @"D:\docs\three.md", RankingProtectedTier.Eligible)
        };
        var cloud = new FakeCloudReranker(["c1"]);

        var run = await Coordinator(local: HighConfidenceLocal(), cloud: cloud).StartAsync(
            Response(items), CloudEnabled());

        _ = await run.Enhancement;
        Assert.Single(cloud.Requests);
    }

    [Fact]
    public async Task A_both_match_source_is_mixed_evidence_even_when_every_candidate_uses_it()
    {
        var items = new[]
        {
            Item("one.md", @"D:\docs\one.md", RankingProtectedTier.Eligible) with
            {
                MatchSource = "both"
            },
            Item("two.md", @"D:\docs\two.md", RankingProtectedTier.Eligible) with
            {
                MatchSource = "both"
            },
            Item("three.md", @"D:\docs\three.md", RankingProtectedTier.Eligible) with
            {
                MatchSource = "both"
            }
        };
        var cloud = new FakeCloudReranker(["c1"]);

        var run = await Coordinator(local: HighConfidenceLocal(), cloud: cloud).StartAsync(
            Response(items), CloudEnabled());

        _ = await run.Enhancement;
        Assert.Single(cloud.Requests);
    }

    [Fact]
    public async Task Natural_language_ambiguity_allows_cloud_when_scores_are_well_separated()
    {
        var items = new[]
        {
            Item("one.md", @"D:\docs\one.md", RankingProtectedTier.Eligible),
            Item("two.md", @"D:\docs\two.md", RankingProtectedTier.Eligible),
            Item("three.md", @"D:\docs\three.md", RankingProtectedTier.Eligible)
        };
        var naturalCloud = new FakeCloudReranker(["c1"]);
        var naturalRun = await Coordinator(local: HighConfidenceLocal(), cloud: naturalCloud).StartAsync(
            Response(items) with { Query = "find the annual budget presentation" },
            CloudEnabled());
        _ = await naturalRun.Enhancement;
        Assert.Single(naturalCloud.Requests);
    }

    [Theory]
    [InlineData("kernel32.dll")]
    [InlineData("annual report.docx")]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData("\"kernel32.dll\"")]
    public async Task Explicit_file_or_path_query_never_uses_cloud(string query)
    {
        var items = new[]
        {
            Item("one.md", @"D:\docs\one.md", RankingProtectedTier.Eligible),
            Item("two.md", @"D:\docs\two.md", RankingProtectedTier.Eligible),
            Item("three.md", @"D:\docs\three.md", RankingProtectedTier.Eligible)
        };

        var closeScores = new Dictionary<string, double>
        {
            ["c0"] = 1,
            ["c1"] = 0.999,
            ["c2"] = 0
        };
        var explicitCloud = new FakeCloudReranker(["c1"]);
        var explicitRun = await Coordinator(
                local: new FakeLocalReranker(new LocalSemanticResult(LocalModelStatus.Ready, closeScores)),
                cloud: explicitCloud)
            .StartAsync(Response(items) with { Query = query }, CloudEnabled());
        _ = await explicitRun.Enhancement;
        Assert.Empty(explicitCloud.Requests);
    }

    [Fact]
    public async Task DeepSeek_receives_the_local_top_ten_and_keeps_local_order_after_its_selection()
    {
        var items = Enumerable.Range(0, 10)
            .Select(index => Item($"item-{index}.txt", $@"D:\docs\item-{index}.txt",
                RankingProtectedTier.Eligible))
            .ToArray();
        var reverseCloseScores = Enumerable.Range(0, 10)
            .ToDictionary(index => $"c{index}", index => 1d - (9 - index) * 0.001d,
                StringComparer.Ordinal);
        var localOnly = await Coordinator(local: new FakeLocalReranker(
                new LocalSemanticResult(LocalModelStatus.Ready, reverseCloseScores)))
            .StartAsync(Response(items), RankingOptions.Default);
        var localOrder = Assert.IsType<DesktopSearchResponse>(await localOnly.Enhancement);
        var cloud = new FakeCloudReranker(["c5"]);
        var cloudRun = await Coordinator(
                local: new FakeLocalReranker(new LocalSemanticResult(
                    LocalModelStatus.Ready, reverseCloseScores)),
                cloud: cloud)
            .StartAsync(Response(items), RankingOptions.Default with
            {
                DeepSeekEnabled = true,
                DeepSeekDisclosureAccepted = true
            });

        var cloudOrder = Assert.IsType<DesktopSearchResponse>(await cloudRun.Enhancement);
        Assert.Equal(localOrder.Items.Select(item => item.FullPath),
            Assert.Single(cloud.Requests).Candidates.Select(candidate => candidate.FullPath));
        Assert.Equal(items[5].FullPath, cloudOrder.Items[0].FullPath);
        Assert.Equal(
            localOrder.Items.Where(item => item.FullPath != items[5].FullPath)
                .Select(item => item.FullPath),
            cloudOrder.Items.Skip(1).Select(item => item.FullPath));
    }

    [Fact]
    public async Task Duplicate_paths_and_surrogate_snippets_do_not_break_top_ten_or_item_eleven()
    {
        var sharedPath = @"D:\docs\multi-hit.md";
        var items = Enumerable.Range(0, 12)
            .Select(index => Item($"item-{index}.md", index is 0 or 10 ? sharedPath : $@"D:\docs\item-{index}.md",
                RankingProtectedTier.Eligible) with
            {
                StartLine = index + 1,
                Snippet = index == 0 ? new string('a', 199) + "😀tail" : null
            })
            .ToArray();
        var closeScores = Enumerable.Range(0, 10)
            .ToDictionary(index => $"c{index}", index => 1d - index * 0.001d,
                StringComparer.Ordinal);
        var cloud = new FakeCloudReranker(["c1", "c0"]);

        var run = await Coordinator(
                local: new FakeLocalReranker(new LocalSemanticResult(LocalModelStatus.Ready, closeScores)),
                cloud: cloud)
            .StartAsync(Response(items), RankingOptions.Default with
            {
                DeepSeekEnabled = true,
                DeepSeekDisclosureAccepted = true
            });
        var enhanced = Assert.IsType<DesktopSearchResponse>(await run.Enhancement);

        Assert.Equal(items.Length, enhanced.Items.Count);
        Assert.Equal(10, enhanced.Items[10].BaselineIndex);
        Assert.Equal(11, enhanced.Items[11].BaselineIndex);
        var sentSnippet = Assert.Single(cloud.Requests).Candidates
            .Single(candidate => candidate.Id == "c0").Snippet;
        Assert.Equal(199, Assert.IsType<string>(sentSnippet).Length);
        Assert.False(char.IsHighSurrogate(sentSnippet[^1]));
    }

    [Theory]
    [InlineData(LocalModelStatus.UnsupportedCpu)]
    [InlineData(LocalModelStatus.MissingAssets)]
    [InlineData(LocalModelStatus.HashMismatch)]
    [InlineData(LocalModelStatus.RuntimeUnavailable)]
    [InlineData(LocalModelStatus.InvalidModel)]
    [InlineData(LocalModelStatus.InferenceFailed)]
    [InlineData(LocalModelStatus.TimedOut)]
    public async Task Local_model_failures_fall_back_to_behavior_without_cloud(LocalModelStatus status)
    {
        var items = Enumerable.Range(1, 5)
            .Select(index => Item($"item-{index}.txt", $@"D:\docs\item-{index}.txt",
                RankingProtectedTier.Eligible))
            .ToArray();
        var local = new FakeLocalReranker(new LocalSemanticResult(status,
            new Dictionary<string, double>()));
        var cloud = new FakeCloudReranker(["c4", "c3", "c2", "c1", "c0"]);

        var run = await Coordinator(local: local, cloud: cloud).StartAsync(Response(items),
            RankingOptions.Default with { DeepSeekEnabled = true, DeepSeekDisclosureAccepted = true });

        Assert.Null(await run.Enhancement);
        Assert.Empty(cloud.Requests);
        Assert.Equal(items.Select(item => item.FullPath),
            run.Immediate.Items.Select(item => item.FullPath));
    }

    [Fact]
    public async Task Content_mode_bypasses_personalized_and_semantic_ranking()
    {
        var local = new FakeLocalReranker(new LocalSemanticResult(LocalModelStatus.Ready,
            new Dictionary<string, double> { ["c0"] = 0, ["c1"] = 10 }));
        var response = Response(
            Item("one.md", @"D:\docs\one.md", RankingProtectedTier.Eligible),
            Item("two.md", @"D:\docs\two.md", RankingProtectedTier.Eligible)) with
        {
            Mode = DesktopSearchMode.Content
        };

        var run = await Coordinator(local: local).StartAsync(response, RankingOptions.Default);

        Assert.Empty(local.Requests);
        Assert.Null(await run.Enhancement);
        Assert.Equal(response.Items.Select(item => item.FullPath),
            run.Immediate.Items.Select(item => item.FullPath));
    }

    private static DesktopRankingCoordinator Coordinator(
        IRankingBehaviorStore? store = null,
        ILocalSemanticReranker? local = null,
        ICloudReranker? cloud = null) => new(
        store ?? new FakeBehaviorStore(),
        local ?? new FakeLocalReranker(new LocalSemanticResult(LocalModelStatus.Disabled,
            new Dictionary<string, double>())),
        cloud ?? new FakeCloudReranker([]),
        TimeProvider.System);

    private static RankingOptions CloudEnabled() => RankingOptions.Default with
    {
        DeepSeekEnabled = true,
        DeepSeekDisclosureAccepted = true
    };

    private static FakeLocalReranker HighConfidenceLocal() => new(new LocalSemanticResult(
        LocalModelStatus.Ready,
        new Dictionary<string, double> { ["c0"] = 1, ["c1"] = 0.5, ["c2"] = 0 }));

    private static DesktopSearchResponse Response(params DesktopSearchItem[] items) => new(
        "needle",
        DesktopSearchMode.Hybrid,
        items.Length,
        items.Length,
        1,
        items.Select((item, index) => item with { BaselineIndex = index }).ToArray());

    private static DesktopSearchItem Item(string name, string path, RankingProtectedTier tier) => new(
        name,
        path,
        Path.GetExtension(name).TrimStart('.'),
        SearchItemKind.File,
        1,
        DateTimeOffset.UtcNow,
        null,
        "name",
        RankingTier: tier);

    private sealed class FakeBehaviorStore : IRankingBehaviorStore
    {
        private readonly IReadOnlyDictionary<string, BehaviorAffinity> _values;

        internal FakeBehaviorStore(IReadOnlyDictionary<string, BehaviorAffinity>? values = null) =>
            _values = values ?? new Dictionary<string, BehaviorAffinity>();

        internal List<IReadOnlyList<RankingIdentity>> ReadRequests { get; } = [];
        internal List<RankingFeedback> RecordRequests { get; } = [];

        public ValueTask<IReadOnlyDictionary<string, BehaviorAffinity>> ReadAsync(
            IReadOnlyList<RankingIdentity> candidates,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            ReadRequests.Add(candidates.ToArray());
            return ValueTask.FromResult(_values);
        }

        public ValueTask RecordAsync(RankingFeedback feedback, CancellationToken cancellationToken = default)
        {
            RecordRequests.Add(feedback);
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class FakeLocalReranker(LocalSemanticResult result) : ILocalSemanticReranker
    {
        internal List<LocalSemanticRequest> Requests { get; } = [];

        public ValueTask<LocalSemanticResult> ScoreAsync(
            LocalSemanticRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeCloudReranker(IReadOnlyList<string> topFive) : ICloudReranker
    {
        internal List<CloudRerankRequest> Requests { get; } = [];

        public ValueTask<CloudRerankResult?> RerankAsync(
            CloudRerankRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult<CloudRerankResult?>(new CloudRerankResult(topFive));
        }
    }
}
