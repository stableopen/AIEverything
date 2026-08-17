using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Reflection;
using System.Text.Json;
using AIEverything.Desktop.Ranking;
using Xunit.Abstractions;

namespace AIEverything.Server.Tests.Desktop;

public sealed class OnnxCrossEncoderRerankerTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"aieverything-onnx-{Guid.NewGuid():N}");

    [Fact]
    public void Maps_sentencepiece_ids_into_xlmr_fairseq_space()
    {
        Assert.Equal(XlmRobertaPairTokenizer.UnknownId,
            XlmRobertaPairTokenizer.MapSentencePieceId(0));
        Assert.Equal(6, XlmRobertaPairTokenizer.MapSentencePieceId(5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            XlmRobertaPairTokenizer.MapSentencePieceId(-1));
    }

    [Fact]
    public void Real_tokenizer_pairs_match_the_fixed_huggingface_reference()
    {
        var reference = LoadReference();
        var root = ModelAssetRoot();
        var tokenizer = new XlmRobertaPairTokenizer(
            Path.Combine(root, "sentencepiece.bpe.model"));

        Assert.Equal(OnnxCrossEncoderReranker.MaximumSequenceLength,
            reference.MaximumSequenceLength);
        Assert.Equal(OnnxCrossEncoderReranker.MaximumQueryTokens,
            reference.MaximumQueryTokens);
        Assert.Equal("1427fd652930e4ba29e8149678df786c240d8825", reference.Revision);
        foreach (var item in reference.TokenCases)
        {
            Assert.True(item.QueryTokenCount < reference.MaximumQueryTokens,
                $"Reference query '{item.Id}' has {item.QueryTokenCount} tokens.");
            var batch = tokenizer.Encode(
                item.Query,
                [item.CandidateText],
                reference.MaximumSequenceLength,
                reference.MaximumQueryTokens);

            Assert.Equal(item.InputIds, batch.InputIds);
            Assert.Equal(item.AttentionMask, batch.AttentionMask);
        }
    }

    [Fact]
    public void Candidate_text_contains_only_name_path_and_a_surrogate_safe_200_character_snippet()
    {
        const string matchSourceCanary = "MATCH-SOURCE-MUST-NOT-ENTER-ONNX";
        var candidate = new LocalSemanticCandidate(
            "candidate",
            "预算😀.md",
            @"D:\项目\年度预算😀.md",
            new string('中', 199) + "😀tail",
            matchSourceCanary,
            RankingProtectedTier.Eligible,
            BehaviorIndex: 0);
        var method = typeof(OnnxCrossEncoderReranker).GetMethod(
            "BuildCandidateText",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var text = Assert.IsType<string>(method!.Invoke(null, [candidate]));

        Assert.StartsWith($"{candidate.Name}\n{candidate.FullPath}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(matchSourceCanary, text, StringComparison.Ordinal);
        var snippet = text[(candidate.Name.Length + candidate.FullPath.Length + 2)..];
        Assert.InRange(snippet.Length, 1, 200);
        Assert.False(char.IsHighSurrogate(snippet[^1]));
    }

    [Fact]
    public void Full_192_token_sequence_keeps_name_and_path_before_truncating_long_snippet()
    {
        var tokenizer = new XlmRobertaPairTokenizer(
            Path.Combine(ModelAssetRoot(), "sentencepiece.bpe.model"));
        var prefixCandidate = Candidate("priority", null) with
        {
            Name = "年度预算😀.md",
            FullPath = @"D:\项目资料\财务\年度预算😀.md"
        };
        var fullCandidate = prefixCandidate with { Snippet = new string('中', 200) };
        var prefix = ActiveIds(tokenizer.Encode(
            "查找年度预算",
            [CandidateText(prefixCandidate)],
            OnnxCrossEncoderReranker.MaximumSequenceLength,
            OnnxCrossEncoderReranker.MaximumQueryTokens));
        var full = ActiveIds(tokenizer.Encode(
            "查找年度预算",
            [CandidateText(fullCandidate)],
            OnnxCrossEncoderReranker.MaximumSequenceLength,
            OnnxCrossEncoderReranker.MaximumQueryTokens));

        Assert.Equal(OnnxCrossEncoderReranker.MaximumSequenceLength, full.Length);
        Assert.Equal(prefix[..^1], full.Take(prefix.Length - 1));
        Assert.Equal(XlmRobertaPairTokenizer.EndOfSentenceId, full[^1]);
    }

    [Fact]
    public async Task Missing_or_modified_assets_fail_closed_without_inference()
    {
        Directory.CreateDirectory(_directory);
        await using var missing = new OnnxCrossEncoderReranker(_directory);
        var missingStatus = await missing.WarmAsync();
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Avx2.IsSupported)
        {
            Assert.Equal(LocalModelStatus.UnsupportedCpu, missingStatus);
            return;
        }

        Assert.Equal(LocalModelStatus.MissingAssets, missingStatus);

        foreach (var name in new[]
                 {
                     "model_quint8_avx2.onnx", "sentencepiece.bpe.model", "config.json",
                     "tokenizer_config.json", "special_tokens_map.json", "MODEL_CARD.md",
                     "LICENSE.apache-2.0.txt", "model-calibration.json"
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(_directory, name), "modified");
        }

        await using var modified = new OnnxCrossEncoderReranker(_directory);
        Assert.Equal(LocalModelStatus.HashMismatch, await modified.WarmAsync());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Fresh_model_initialization_p95_meets_the_desktop_budget()
    {
        const int sampleCount = 5;
        var durations = new List<double>(sampleCount);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            await using var reranker = new OnnxCrossEncoderReranker(ModelAssetRoot());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(LocalModelStatus.Ready, await reranker.WarmAsync(timeout.Token));
            stopwatch.Stop();
            durations.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var ordered = durations.Order().ToArray();
        var p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        _output.WriteLine(
            "Fresh initialization p95={0:F1} ms; samples={1}",
            p95,
            string.Join(", ", ordered.Select(value => value.ToString("F1"))));
        Assert.True(p95 <= 3000,
            $"Fresh initialization p95 was {p95:F1} ms; samples: {string.Join(", ", ordered.Select(value => value.ToString("F1")))}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_model_loads_and_scores_relevant_text_above_irrelevant_text()
    {
        await using var reranker = new OnnxCrossEncoderReranker(
            ModelAssetRoot(), TimeSpan.FromSeconds(10));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalModelStatus.Ready, await reranker.WarmAsync(timeout.Token));
        var result = await reranker.ScoreAsync(new LocalSemanticRequest(
            "How many people live in Berlin?",
            [
                Candidate("relevant", "Berlin has 3,520,031 registered inhabitants."),
                Candidate("irrelevant", "New York is famous for the Metropolitan Museum of Art.")
            ]));

        Assert.Equal(LocalModelStatus.Ready, result.Status);
        Assert.Equal(2, result.Scores.Count);
        Assert.All(result.Scores.Values, score => Assert.True(double.IsFinite(score)));
        Assert.True(result.Scores["relevant"] > result.Scores["irrelevant"]);

        var unicodeResult = await reranker.ScoreAsync(new LocalSemanticRequest(
            "查找年度预算路径",
            [
                new LocalSemanticCandidate(
                    "unicode",
                    "年度预算😀.md",
                    @"D:\项目资料\财务\年度预算😀.md",
                    new string('中', 220),
                    "MATCH-SOURCE-MUST-NOT-ENTER-ONNX",
                    RankingProtectedTier.Eligible,
                    BehaviorIndex: 0),
                Candidate("other", "普通英文文档")
            ]));
        Assert.Equal(LocalModelStatus.Ready, unicodeResult.Status);
        Assert.Equal(2, unicodeResult.Scores.Count);
        Assert.All(unicodeResult.Scores.Values, score => Assert.True(double.IsFinite(score)));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_model_matches_python_reference_logits_and_top_ten_ranking()
    {
        var reference = LoadReference();
        var topTen = reference.Top10;
        Assert.Equal(10, topTen.Candidates.Count);
        Assert.True(topTen.QueryTokenCount < reference.MaximumQueryTokens);

        var tokenizer = new XlmRobertaPairTokenizer(
            Path.Combine(ModelAssetRoot(), "sentencepiece.bpe.model"));
        var semanticCandidates = topTen.Candidates.Select((candidate, index) =>
        {
            var semantic = new LocalSemanticCandidate(
                candidate.Id,
                candidate.Name,
                candidate.FullPath,
                candidate.Snippet,
                candidate.MatchSource,
                RankingProtectedTier.Eligible,
                index);
            Assert.Equal(candidate.CandidateText, CandidateText(semantic));
            return semantic;
        }).ToArray();
        var tokenized = tokenizer.Encode(
            topTen.Query,
            topTen.Candidates.Select(candidate => candidate.CandidateText).ToArray(),
            reference.MaximumSequenceLength,
            reference.MaximumQueryTokens);
        Assert.Equal(topTen.InputIds.SelectMany(row => row), tokenized.InputIds);
        Assert.Equal(topTen.AttentionMask.SelectMany(row => row), tokenized.AttentionMask);

        await using var reranker = new OnnxCrossEncoderReranker(
            ModelAssetRoot(), TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalModelStatus.Ready, await reranker.WarmAsync(timeout.Token));
        var result = await reranker.ScoreAsync(
            new LocalSemanticRequest(topTen.Query, semanticCandidates), timeout.Token);

        Assert.Equal(LocalModelStatus.Ready, result.Status);
        Assert.Equal(topTen.Logits.Count, result.Scores.Count);
        for (var index = 0; index < topTen.Candidates.Count; index++)
        {
            var id = topTen.Candidates[index].Id;
            Assert.InRange(
                Math.Abs(result.Scores[id] - topTen.Logits[index]),
                0,
                reference.LogitAbsoluteTolerance);
        }

        var actualRanking = topTen.Candidates
            .Select((candidate, index) => new
            {
                candidate.Id,
                Index = index,
                Score = result.Scores[candidate.Id]
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Id)
            .ToArray();
        Assert.Equal(topTen.Ranking, actualRanking);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Warm_top_ten_inference_meets_the_desktop_latency_budget()
    {
        await using var reranker = new OnnxCrossEncoderReranker(ModelAssetRoot());
        Assert.Equal(LocalModelStatus.Ready, await reranker.WarmAsync());
        var candidates = Enumerable.Range(0, 10)
            .Select(index => Candidate($"candidate-{index}",
                $"Annual budget planning document number {index} with finance details and milestones."))
            .ToArray();
        var priming = await reranker.ScoreAsync(new LocalSemanticRequest(
            "annual budget milestones", candidates));
        Assert.Equal(LocalModelStatus.Ready, priming.Status);
        Assert.True(priming.DurationMs <= OnnxCrossEncoderReranker.DefaultInferenceTimeout.TotalMilliseconds,
            $"First Top10 inference was {priming.DurationMs:F1} ms.");
        const int sampleCount = 20;
        var durations = new List<double>(sampleCount);

        for (var iteration = 0; iteration < sampleCount; iteration++)
        {
            var result = await reranker.ScoreAsync(new LocalSemanticRequest(
                "annual budget milestones", candidates));
            Assert.Equal(LocalModelStatus.Ready, result.Status);
            durations.Add(result.DurationMs);
        }

        var ordered = durations.Order().ToArray();
        var p95 = ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
        _output.WriteLine(
            "Warm Top10 p95={0:F1} ms; max={1:F1} ms; samples={2}",
            p95,
            ordered[^1],
            string.Join(", ", ordered.Select(value => value.ToString("F1"))));
        Assert.True(p95 <= 250,
            $"Warm Top10 p95 was {p95:F1} ms; samples: {string.Join(", ", ordered.Select(value => value.ToString("F1")))}");
        Assert.True(ordered[^1] <= OnnxCrossEncoderReranker.DefaultInferenceTimeout.TotalMilliseconds,
            $"Warm Top10 maximum was {ordered[^1]:F1} ms.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static LocalSemanticCandidate Candidate(string id, string? snippet) => new(
        id,
        $"{id}.txt",
        $@"D:\docs\{id}.txt",
        snippet,
        "content",
        RankingProtectedTier.Eligible,
        BehaviorIndex: 0);

    private static string CandidateText(LocalSemanticCandidate candidate)
    {
        var method = typeof(OnnxCrossEncoderReranker).GetMethod(
            "BuildCandidateText",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [candidate]));
    }

    private static long[] ActiveIds(XlmRobertaTokenizedBatch batch) =>
        batch.InputIds.TakeWhile((_, index) => batch.AttentionMask[index] == 1).ToArray();

    private static string ModelAssetRoot()
    {
        var directory = RepositoryRoot();
        return Path.Combine(directory.FullName, "src", "AIEverything.Desktop", "Models",
            OnnxCrossEncoderReranker.ModelDirectoryName);
    }

    private static OnnxReference LoadReference()
    {
        var path = Path.Combine(
            RepositoryRoot().FullName,
            "tests",
            "AIEverything.Server.Tests",
            "Desktop",
            "Fixtures",
            "onnx-reference-v1427fd.json");
        return Assert.IsType<OnnxReference>(JsonSerializer.Deserialize<OnnxReference>(
            File.ReadAllText(path)));
    }

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AIEverything.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory;
    }

    private sealed class OnnxReference
    {
        public int SchemaVersion { get; init; }
        public string Revision { get; init; } = string.Empty;
        public int MaximumSequenceLength { get; init; }
        public int MaximumQueryTokens { get; init; }
        public double LogitAbsoluteTolerance { get; init; }
        public List<TokenReferenceCase> TokenCases { get; init; } = [];
        public TopTenReference Top10 { get; init; } = new();
    }

    private sealed class TokenReferenceCase
    {
        public string Id { get; init; } = string.Empty;
        public string Query { get; init; } = string.Empty;
        public int QueryTokenCount { get; init; }
        public string CandidateText { get; init; } = string.Empty;
        public long[] InputIds { get; init; } = [];
        public long[] AttentionMask { get; init; } = [];
    }

    private sealed class TopTenReference
    {
        public string Query { get; init; } = string.Empty;
        public int QueryTokenCount { get; init; }
        public List<ReferenceCandidate> Candidates { get; init; } = [];
        public List<List<long>> InputIds { get; init; } = [];
        public List<List<long>> AttentionMask { get; init; } = [];
        public List<double> Logits { get; init; } = [];
        public List<string> Ranking { get; init; } = [];
    }

    private sealed class ReferenceCandidate
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public string? Snippet { get; init; }
        public string MatchSource { get; init; } = string.Empty;
        public string CandidateText { get; init; } = string.Empty;
    }
}
