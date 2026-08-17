using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace AIEverything.Desktop.Ranking;

public sealed class OnnxCrossEncoderReranker : ILocalSemanticReranker, IAsyncDisposable
{
    public const string ModelDirectoryName = "mmarco-mMiniLMv2-L12-H384-v1";
    public const int MaximumSequenceLength = 192;
    public const int MaximumQueryTokens = 48;
    public static readonly TimeSpan DefaultInferenceTimeout = TimeSpan.FromMilliseconds(400);

    private static readonly IReadOnlyDictionary<string, string> ExpectedHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["model_quint8_avx2.onnx"] = "6C2513767FB63D008A4377BEF7A7A3555433D9436342BB53E35A3A72FFC52D4B",
            ["sentencepiece.bpe.model"] = "CFC8146ABE2A0488E9E2A0C56DE7952F7C11AB059ECA145A0A727AFCE0DB2865",
            ["config.json"] = "CC2CFE51AA3FD759D21D21ACF5DFD6994AA67A3C9210636D22E143699D336C77",
            ["tokenizer_config.json"] = "E7FBFBFA6347B4E414C1CEE50D142E2C2F9A895DAD68B068AE83A8B564C3837E",
            ["special_tokens_map.json"] = "378EB3BF733EB16E65792D7E3FDA5B8A4631387CA04D2015199C4D4F22AE554D",
            ["MODEL_CARD.md"] = "474736A65D6393A060119A8DC304563AF67AF4D8D86CCFEE4A05DD0DF107FC11",
            ["LICENSE.apache-2.0.txt"] = "C71D239DF91726FC519C6EB72D318EC65820627232B2F796219E87DCF35D0AB4",
            ["model-calibration.json"] = "A188F75C2AE3B2B2827909B76A386D7508A6F473F95CCBB8977AB5D5E33F92F3"
        };

    private readonly string _assetRoot;
    private readonly TimeSpan _inferenceTimeout;
    private readonly Lazy<Task<RuntimeLoadResult>> _runtime;
    private readonly LatestPendingWorkScheduler<InferenceWork, LocalSemanticResult> _inferenceScheduler;
    private bool _disposed;
    private LocalModelStatus _status = LocalModelStatus.Disabled;

    public OnnxCrossEncoderReranker(
        string assetRoot,
        TimeSpan? inferenceTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(assetRoot) || !Path.IsPathFullyQualified(assetRoot))
        {
            throw new ArgumentException("Model asset root must be absolute.", nameof(assetRoot));
        }

        _assetRoot = Path.GetFullPath(assetRoot);
        _inferenceTimeout = inferenceTimeout ?? DefaultInferenceTimeout;
        if (_inferenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inferenceTimeout));
        }

        _runtime = new Lazy<Task<RuntimeLoadResult>>(
            () => Task.Run(LoadRuntime), LazyThreadSafetyMode.ExecutionAndPublication);
        _inferenceScheduler = new LatestPendingWorkScheduler<InferenceWork, LocalSemanticResult>(
            work => RunInference(work.Runtime, work.Request),
            _inferenceTimeout,
            (elapsed, detail) => new LocalSemanticResult(
                LocalModelStatus.TimedOut,
                new Dictionary<string, double>(),
                elapsed.TotalMilliseconds,
                detail));
    }

    public static string DefaultAssetRoot =>
        Path.Combine(AppContext.BaseDirectory, "Models", ModelDirectoryName);

    public LocalModelStatus Status => _status;

    public async Task<LocalModelStatus> WarmAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _runtime.Value.WaitAsync(cancellationToken);
        _status = result.Status;
        return result.Status;
    }

    public async ValueTask<LocalSemanticResult> ScoreAsync(
        LocalSemanticRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query) || request.Candidates.Count is < 1 or > 10)
        {
            return new LocalSemanticResult(LocalModelStatus.InvalidModel,
                new Dictionary<string, double>(), Detail: "Invalid local rerank request.");
        }

        var loaded = await _runtime.Value.WaitAsync(cancellationToken);
        _status = loaded.Status;
        if (loaded.Runtime is null)
        {
            return new LocalSemanticResult(loaded.Status,
                new Dictionary<string, double>(), Detail: loaded.Detail);
        }

        var result = await _inferenceScheduler.EnqueueAsync(
            new InferenceWork(loaded.Runtime, request), cancellationToken);
        _status = result.Status;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _inferenceScheduler.DisposeAsync();
        if (_runtime.IsValueCreated)
        {
            var loaded = await _runtime.Value;
            loaded.Runtime?.Dispose();
        }
    }

    private RuntimeLoadResult LoadRuntime()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !Avx2.IsSupported)
        {
            return new RuntimeLoadResult(LocalModelStatus.UnsupportedCpu, null,
                "The bundled model requires Windows x64 with AVX2.");
        }

        foreach (var expected in ExpectedHashes)
        {
            var path = Path.Combine(_assetRoot, expected.Key);
            if (!File.Exists(path))
            {
                return new RuntimeLoadResult(LocalModelStatus.MissingAssets, null,
                    $"Missing model asset: {expected.Key}");
            }

            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeLoadResult(LocalModelStatus.HashMismatch, null,
                    $"Model asset hash mismatch: {expected.Key}");
            }
        }

        ModelRuntime runtime;
        try
        {
            runtime = new ModelRuntime(
                Path.Combine(_assetRoot, "model_quint8_avx2.onnx"),
                Path.Combine(_assetRoot, "sentencepiece.bpe.model"));
        }
        catch (Exception exception) when (exception is DllNotFoundException or
                                          BadImageFormatException or
                                          TypeInitializationException)
        {
            return new RuntimeLoadResult(LocalModelStatus.RuntimeUnavailable, null,
                exception.GetType().Name);
        }
        catch (OnnxRuntimeException exception)
        {
            return new RuntimeLoadResult(LocalModelStatus.InvalidModel, null,
                exception.GetType().Name);
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                          InvalidOperationException or
                                          IOException or
                                          UnauthorizedAccessException)
        {
            return new RuntimeLoadResult(LocalModelStatus.InvalidModel, null,
                exception.GetType().Name);
        }

        try
        {
            var warmup = runtime.Score("local search", ["document"]);
            if (warmup.Length != 1 || !float.IsFinite(warmup[0]))
            {
                runtime.Dispose();
                return new RuntimeLoadResult(LocalModelStatus.InferenceFailed, null,
                    "Local model warmup returned an invalid score.");
            }

            return new RuntimeLoadResult(LocalModelStatus.Ready, runtime, null);
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or
                                          InvalidOperationException or
                                          ArgumentException)
        {
            runtime.Dispose();
            return new RuntimeLoadResult(LocalModelStatus.InferenceFailed, null,
                exception.GetType().Name);
        }
    }

    private static LocalSemanticResult RunInference(
        ModelRuntime runtime,
        LocalSemanticRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var candidates = request.Candidates.Select(BuildCandidateText).ToArray();
            var scores = runtime.Score(request.Query, candidates);
            stopwatch.Stop();
            if (scores.Length != request.Candidates.Count || scores.Any(score => !float.IsFinite(score)))
            {
                return new LocalSemanticResult(LocalModelStatus.InferenceFailed,
                    new Dictionary<string, double>(), stopwatch.Elapsed.TotalMilliseconds,
                    "Local model returned invalid scores.");
            }

            return new LocalSemanticResult(
                LocalModelStatus.Ready,
                request.Candidates.Select((candidate, index) => (candidate.Id, Score: (double)scores[index]))
                    .ToDictionary(value => value.Id, value => value.Score, StringComparer.Ordinal),
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or
                                          InvalidOperationException or
                                          ArgumentException)
        {
            stopwatch.Stop();
            return new LocalSemanticResult(LocalModelStatus.InferenceFailed,
                new Dictionary<string, double>(), stopwatch.Elapsed.TotalMilliseconds,
                exception.GetType().Name);
        }
    }

    private static string BuildCandidateText(LocalSemanticCandidate candidate)
    {
        var prefix = $"{candidate.Name}\n{candidate.FullPath}";
        var snippet = TruncateSnippet(candidate.Snippet);
        return snippet is null ? prefix : $"{prefix}\n{snippet}";
    }

    private static string? TruncateSnippet(string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return null;
        }

        var length = Math.Min(200, snippet.Length);
        if (length > 0 && char.IsHighSurrogate(snippet[length - 1]))
        {
            length--;
        }

        return snippet[..length];
    }

    private sealed record RuntimeLoadResult(
        LocalModelStatus Status,
        ModelRuntime? Runtime,
        string? Detail);

    private sealed record InferenceWork(
        ModelRuntime Runtime,
        LocalSemanticRequest Request);

    private sealed class ModelRuntime : IDisposable
    {
        private const string InputIdsName = "input_ids";
        private const string AttentionMaskName = "attention_mask";
        private const string OutputName = "logits";

        private readonly InferenceSession _session;
        private readonly XlmRobertaPairTokenizer _tokenizer;

        internal ModelRuntime(string modelPath, string tokenizerPath)
        {
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2))
            };
            try
            {
                _session = new InferenceSession(modelPath, options);
            }
            finally
            {
                options.Dispose();
            }

            try
            {
                ValidateTensor(_session.InputMetadata, InputIdsName, typeof(long), rank: 2);
                ValidateTensor(_session.InputMetadata, AttentionMaskName, typeof(long), rank: 2);
                ValidateTensor(_session.OutputMetadata, OutputName, typeof(float), rank: 2);
                _tokenizer = new XlmRobertaPairTokenizer(tokenizerPath);
            }
            catch
            {
                _session.Dispose();
                throw;
            }
        }

        internal float[] Score(string query, IReadOnlyList<string> candidates)
        {
            var batch = _tokenizer.Encode(
                query, candidates, MaximumSequenceLength, MaximumQueryTokens);
            long[] shape = [batch.BatchSize, batch.SequenceLength];
            using var inputIds = OrtValue.CreateTensorValueFromMemory(batch.InputIds, shape);
            using var attentionMask = OrtValue.CreateTensorValueFromMemory(batch.AttentionMask, shape);
            var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
            {
                [InputIdsName] = inputIds,
                [AttentionMaskName] = attentionMask
            };
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, inputs, [OutputName]);
            return outputs.Single().GetTensorDataAsSpan<float>().ToArray();
        }

        public void Dispose()
        {
            _session.Dispose();
        }

        private static void ValidateTensor(
            IReadOnlyDictionary<string, NodeMetadata> metadata,
            string name,
            Type elementType,
            int rank)
        {
            if (!metadata.TryGetValue(name, out var node) ||
                !node.IsTensor ||
                node.ElementType != elementType ||
                node.Dimensions.Length != rank)
            {
                throw new InvalidDataException($"Unexpected ONNX tensor metadata: {name}");
            }
        }
    }
}
