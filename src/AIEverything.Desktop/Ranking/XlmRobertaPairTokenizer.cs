using Microsoft.ML.Tokenizers;

namespace AIEverything.Desktop.Ranking;

public sealed record XlmRobertaTokenizedBatch(
    long[] InputIds,
    long[] AttentionMask,
    int BatchSize,
    int SequenceLength);

public sealed class XlmRobertaPairTokenizer
{
    public const long BeginningOfSentenceId = 0;
    public const long PaddingId = 1;
    public const long EndOfSentenceId = 2;
    public const long UnknownId = 3;

    private readonly SentencePieceTokenizer _tokenizer;

    public XlmRobertaPairTokenizer(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("SentencePiece model path is required.", nameof(modelPath));
        }

        using var stream = File.OpenRead(modelPath);
        _tokenizer = SentencePieceTokenizer.Create(
            stream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);
    }

    public XlmRobertaTokenizedBatch Encode(
        string query,
        IReadOnlyList<string> candidates,
        int maximumSequenceLength = 192,
        int maximumQueryTokens = 48)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required.", nameof(query));
        }

        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));
        }

        if (maximumSequenceLength < 8 || maximumQueryTokens < 1 ||
            maximumQueryTokens > maximumSequenceLength - 5)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSequenceLength));
        }

        var queryIds = EncodeText(query, maximumQueryTokens);
        var maximumCandidateTokens = maximumSequenceLength - queryIds.Count - 4;
        var inputIds = new long[checked(candidates.Count * maximumSequenceLength)];
        var attentionMask = new long[inputIds.Length];
        Array.Fill(inputIds, PaddingId);

        for (var batch = 0; batch < candidates.Count; batch++)
        {
            var candidateIds = EncodeText(candidates[batch] ?? string.Empty, maximumCandidateTokens);
            var offset = batch * maximumSequenceLength;
            var position = 0;
            inputIds[offset + position++] = BeginningOfSentenceId;
            foreach (var id in queryIds)
            {
                inputIds[offset + position++] = id;
            }

            inputIds[offset + position++] = EndOfSentenceId;
            inputIds[offset + position++] = EndOfSentenceId;
            foreach (var id in candidateIds)
            {
                inputIds[offset + position++] = id;
            }

            inputIds[offset + position++] = EndOfSentenceId;
            Array.Fill(attentionMask, 1L, offset, position);
        }

        return new XlmRobertaTokenizedBatch(
            inputIds, attentionMask, candidates.Count, maximumSequenceLength);
    }

    public static long MapSentencePieceId(int rawId)
    {
        if (rawId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rawId));
        }

        return rawId == 0 ? UnknownId : checked(rawId + 1L);
    }

    private IReadOnlyList<long> EncodeText(string text, int maximumTokens) =>
        _tokenizer.EncodeToIds(
                text,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                considerPreTokenization: true,
                considerNormalization: true)
            .Take(maximumTokens)
            .Select(MapSentencePieceId)
            .ToArray();
}
