namespace AIEverything.Desktop.Ranking;

public sealed class DesktopRankingCoordinator
{
    private const int BehaviorCandidateLimit = 10;
    private const int SemanticCandidateLimit = 10;
    private const int EnhancedResultLimit = 5;
    private const double SemanticWeight = 0.65;
    private const double BehaviorWeight = 0.35;
    private const double LowConfidenceThreshold = 0.15;

    private readonly IRankingBehaviorStore _behaviorStore;
    private readonly ILocalSemanticReranker _localReranker;
    private readonly ICloudReranker _cloudReranker;
    private readonly TimeProvider _timeProvider;

    public DesktopRankingCoordinator(
        IRankingBehaviorStore behaviorStore,
        ILocalSemanticReranker localReranker,
        ICloudReranker cloudReranker,
        TimeProvider timeProvider)
    {
        _behaviorStore = behaviorStore ?? throw new ArgumentNullException(nameof(behaviorStore));
        _localReranker = localReranker ?? throw new ArgumentNullException(nameof(localReranker));
        _cloudReranker = cloudReranker ?? throw new ArgumentNullException(nameof(cloudReranker));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<RankingRun> StartAsync(
        DesktopSearchResponse baseline,
        RankingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(options);

        if (baseline.Mode == DesktopSearchMode.Content || baseline.Items.Count < 2)
        {
            return new RankingRun(baseline, Task.FromResult<DesktopSearchResponse?>(null));
        }

        var immediate = options.BehaviorEnabled
            ? await ApplyBehaviorAsync(baseline, cancellationToken)
            : baseline;
        var enhancement = options.LocalModelEnabled &&
                          immediate.Items.All(item => item.RankingTier != RankingProtectedTier.Exact)
            ? EnhanceAsync(immediate, options, cancellationToken)
            : Task.FromResult<DesktopSearchResponse?>(null);
        return new RankingRun(immediate, enhancement);
    }

    public ValueTask RecordAsync(
        RankingFeedback feedback,
        RankingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentNullException.ThrowIfNull(options);
        return options.BehaviorEnabled
            ? _behaviorStore.RecordAsync(feedback, cancellationToken)
            : ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default) =>
        _behaviorStore.ClearAsync(cancellationToken);

    private async ValueTask<DesktopSearchResponse> ApplyBehaviorAsync(
        DesktopSearchResponse response,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, BehaviorAffinity> affinities;
        var top = response.Items.Take(BehaviorCandidateLimit).ToArray();
        try
        {
            affinities = await _behaviorStore.ReadAsync(
                top.Select(item => new RankingIdentity(item.FullPath, item.Extension)).ToArray(),
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return response;
        }

        if (affinities.Count == 0)
        {
            return response;
        }

        var orderedTop = top
            .Select((item, index) => (Item: item, Index: index,
                Affinity: affinities.TryGetValue(item.FullPath, out var value) ? value : null))
            .OrderBy(value => value.Item.RankingTier)
            .ThenBy(value => value.Index - Math.Clamp(value.Affinity?.Promotion ?? 0, 0, 10))
            .ThenBy(value => value.Index)
            .Select(value => value.Affinity is { Promotion: > 0 }
                ? value.Item with { RankingReason = value.Affinity.Reason }
                : value.Item)
            .ToArray();
        return response with
        {
            Items = orderedTop.Concat(response.Items.Skip(top.Length)).ToArray()
        };
    }

    private async Task<DesktopSearchResponse?> EnhanceAsync(
        DesktopSearchResponse immediate,
        RankingOptions options,
        CancellationToken cancellationToken)
    {
        var top = immediate.Items.Take(SemanticCandidateLimit).ToArray();
        if (top.Length < 2)
        {
            return null;
        }

        var candidates = top.Select((item, index) => new LocalSemanticCandidate(
            $"c{index}", item.Name, item.FullPath, item.Snippet, item.MatchSource,
            item.RankingTier, index)).ToArray();
        LocalSemanticResult local;
        try
        {
            local = await _localReranker.ScoreAsync(
                new LocalSemanticRequest(immediate.Query, candidates), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (local.Status != LocalModelStatus.Ready || !HasCompleteFiniteScores(candidates, local.Scores))
        {
            return null;
        }

        var localOrder = BuildLocalOrder(candidates, local.Scores);
        var localFallbackOrder = ExpandProtectedOrder(candidates, localOrder);
        var localResponse = ReorderTop(
            immediate,
            candidates,
            localOrder,
            "\u672c\u5730\u8bed\u4e49\u5339\u914d");

        if (!options.DeepSeekEnabled || !ShouldUseCloud(immediate.Query, candidates, local.Scores))
        {
            return OrdersEqual(immediate.Items, localResponse.Items) ? null : localResponse;
        }

        CloudRerankResult? cloud;
        try
        {
            var candidatesById = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
            cloud = await _cloudReranker.RerankAsync(new CloudRerankRequest(
                immediate.Query,
                localFallbackOrder.Select(id => candidatesById[id]).Select(item => new CloudRerankCandidate(
                    item.Id, item.Name, item.FullPath, TruncateSnippet(item.Snippet),
                    item.MatchSource, item.Tier)).ToArray()), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return OrdersEqual(immediate.Items, localResponse.Items) ? null : localResponse;
        }

        if (!IsValidCloudResult(cloud, candidates))
        {
            return OrdersEqual(immediate.Items, localResponse.Items) ? null : localResponse;
        }

        var cloudResponse = ReorderTop(
            immediate,
            candidates,
            cloud!.TopFiveIds,
            "AI\u4f18\u5316",
            localFallbackOrder);
        return OrdersEqual(immediate.Items, cloudResponse.Items) ? null : cloudResponse;
    }

    private static IReadOnlyList<string> BuildLocalOrder(
        IReadOnlyList<LocalSemanticCandidate> candidates,
        IReadOnlyDictionary<string, double> scores)
    {
        var selected = new List<string>(EnhancedResultLimit);
        foreach (var tier in Enum.GetValues<RankingProtectedTier>())
        {
            var tierCandidates = candidates.Where(candidate => candidate.Tier == tier).ToArray();
            if (tier == RankingProtectedTier.Exact)
            {
                selected.AddRange(tierCandidates.Select(candidate => candidate.Id));
                continue;
            }

            var semanticRanks = tierCandidates
                .OrderByDescending(candidate => scores[candidate.Id])
                .ThenBy(candidate => candidate.BehaviorIndex)
                .Select((candidate, index) => (candidate.Id, Rank: index + 1))
                .ToDictionary(value => value.Id, value => value.Rank, StringComparer.Ordinal);
            var count = Math.Max(1, tierCandidates.Length);
            selected.AddRange(tierCandidates
                .OrderByDescending(candidate =>
                    BehaviorWeight * RankValue(candidate.BehaviorIndex + 1, count) +
                    SemanticWeight * RankValue(semanticRanks[candidate.Id], count))
                .ThenBy(candidate => candidate.BehaviorIndex)
                .Select(candidate => candidate.Id));
        }

        return selected.Take(EnhancedResultLimit).ToArray();
    }

    private static DesktopSearchResponse ReorderTop(
        DesktopSearchResponse response,
        IReadOnlyList<LocalSemanticCandidate> candidates,
        IReadOnlyList<string> requestedTop,
        string reason,
        IReadOnlyList<string>? fallbackOrder = null)
    {
        var byId = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var selected = new List<DesktopSearchItem>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var fallback = fallbackOrder ?? candidates.Select(candidate => candidate.Id).ToArray();

        foreach (var tier in Enum.GetValues<RankingProtectedTier>())
        {
            foreach (var id in requestedTop)
            {
                if (!byId.TryGetValue(id, out var candidate) || candidate.Tier != tier || !used.Add(id))
                {
                    continue;
                }

                selected.Add(response.Items[candidate.BehaviorIndex] with { RankingReason = reason });
            }

            foreach (var id in fallback)
            {
                if (byId.TryGetValue(id, out var candidate) &&
                    candidate.Tier == tier &&
                    used.Add(id))
                {
                    selected.Add(response.Items[candidate.BehaviorIndex]);
                }
            }
        }

        selected.AddRange(response.Items.Skip(candidates.Count));
        return response with { Items = selected };
    }

    private static IReadOnlyList<string> ExpandProtectedOrder(
        IReadOnlyList<LocalSemanticCandidate> candidates,
        IReadOnlyList<string> requestedTop)
    {
        var byId = candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var selected = new List<string>(candidates.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tier in Enum.GetValues<RankingProtectedTier>())
        {
            foreach (var id in requestedTop)
            {
                if (byId.TryGetValue(id, out var candidate) && candidate.Tier == tier && used.Add(id))
                {
                    selected.Add(id);
                }
            }

            foreach (var candidate in candidates.Where(candidate => candidate.Tier == tier))
            {
                if (used.Add(candidate.Id))
                {
                    selected.Add(candidate.Id);
                }
            }
        }

        return selected;
    }

    private static bool HasCompleteFiniteScores(
        IReadOnlyList<LocalSemanticCandidate> candidates,
        IReadOnlyDictionary<string, double> scores) =>
        scores.Count == candidates.Count && candidates.All(candidate =>
            scores.TryGetValue(candidate.Id, out var score) && double.IsFinite(score));

    private static bool IsLowConfidence(
        IReadOnlyList<LocalSemanticCandidate> candidates,
        IReadOnlyDictionary<string, double> scores)
    {
        var ordered = candidates.Select(candidate => scores[candidate.Id]).OrderByDescending(score => score).ToArray();
        if (ordered.Length < 2)
        {
            return false;
        }

        var spread = Math.Max(ordered[0] - ordered[^1], 1e-9);
        var topGap = (ordered[0] - ordered[1]) / spread;
        if (ordered.Length < 6)
        {
            return topGap < LowConfidenceThreshold;
        }

        var fifthGap = (ordered[4] - ordered[5]) / spread;
        return Math.Min(topGap, fifthGap) < LowConfidenceThreshold;
    }

    private static bool ShouldUseCloud(
        string query,
        IReadOnlyList<LocalSemanticCandidate> candidates,
        IReadOnlyDictionary<string, double> scores)
    {
        if (candidates.Any(candidate => candidate.Tier == RankingProtectedTier.Exact) ||
            candidates.Count(candidate => candidate.Tier == RankingProtectedTier.Eligible) < 3 ||
            IsExplicitFileOrPathQuery(query))
        {
            return false;
        }

        return IsLowConfidence(candidates, scores) ||
               HasDuplicateNames(candidates) ||
               HasMixedMatchEvidence(candidates) ||
               IsNaturalLanguageQuery(query);
    }

    private static bool HasDuplicateNames(IReadOnlyList<LocalSemanticCandidate> candidates) =>
        candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .GroupBy(candidate => candidate.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Skip(1).Any());

    private static bool HasMixedMatchEvidence(IReadOnlyList<LocalSemanticCandidate> candidates)
    {
        var hasNameEvidence = false;
        var hasContentEvidence = false;
        foreach (var candidate in candidates)
        {
            var source = candidate.MatchSource?.Trim();
            if (string.Equals(source, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "both", StringComparison.OrdinalIgnoreCase))
            {
                hasNameEvidence = true;
            }

            if (string.Equals(source, "content", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, "both", StringComparison.OrdinalIgnoreCase))
            {
                hasContentEvidence = true;
            }

            if (hasNameEvidence && hasContentEvidence)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitFileOrPathQuery(string query)
    {
        var value = query.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (IsWholeQueryQuoted(value) ||
            Path.IsPathRooted(value) ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return true;
        }

        var leaf = Path.GetFileName(value);
        if (leaf.Length > 1 &&
            (Path.GetExtension(leaf).Length > 1 || leaf[0] == '.'))
        {
            return true;
        }

        return false;
    }

    private static bool IsWholeQueryQuoted(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') ||
         (value[0] == '\'' && value[^1] == '\'') ||
         (value[0] == '\u201c' && value[^1] == '\u201d'));

    private static bool IsNaturalLanguageQuery(string query)
    {
        var value = query.Trim();
        if (value.IndexOfAny(['?', '\uFF1F']) >= 0)
        {
            return true;
        }

        var terms = value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length >= 4)
        {
            return true;
        }

        return NaturalLanguageCues.Any(cue =>
            value.Contains(cue, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] NaturalLanguageCues =
    [
        "find ", "show me", "where is", "which ", "what ",
        "\u5e2e\u6211\u627e", "\u627e\u4e00\u4e0b", "\u67e5\u627e", "\u54ea\u4e2a", "\u54ea\u91cc", "\u4ec0\u4e48"
    ];

    private static bool IsValidCloudResult(
        CloudRerankResult? result,
        IReadOnlyList<LocalSemanticCandidate> candidates)
    {
        if (result is null || result.TopFiveIds.Count is < 1 or > EnhancedResultLimit)
        {
            return false;
        }

        var allowed = candidates.Select(candidate => candidate.Id).ToHashSet(StringComparer.Ordinal);
        return result.TopFiveIds.Distinct(StringComparer.Ordinal).Count() == result.TopFiveIds.Count &&
               result.TopFiveIds.All(allowed.Contains);
    }

    private static double RankValue(int rank, int count) =>
        count <= 1 ? 1 : (count - rank) / (double)(count - 1);

    private static string? TruncateSnippet(string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return null;
        }

        var compact = string.Join(' ', snippet.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length <= 200)
        {
            return compact;
        }

        var length = char.IsHighSurrogate(compact[199]) ? 199 : 200;
        return compact[..length];
    }

    private static bool OrdersEqual(
        IReadOnlyList<DesktopSearchItem> left,
        IReadOnlyList<DesktopSearchItem> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.BaselineIndex == pair.Second.BaselineIndex &&
            pair.First.StartLine == pair.Second.StartLine &&
            pair.First.EndLine == pair.Second.EndLine &&
            string.Equals(pair.First.FullPath, pair.Second.FullPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.First.JsonPath, pair.Second.JsonPath, StringComparison.Ordinal));
}
