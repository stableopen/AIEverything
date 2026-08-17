using System.Diagnostics;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.ContentClient;

public sealed class HybridSearchService
{
    private const double RrfK = 60;
    private readonly IEverythingSearchService _everything;
    private readonly IContentSearchService _content;

    public HybridSearchService(
        IEverythingSearchService everything,
        IContentSearchService content)
    {
        _everything = everything;
        _content = content;
    }

    public async Task<HybridSearchResponse> SearchAsync(
        HybridSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var stopwatch = Stopwatch.StartNew();
        var nameLimit = checked((int)Math.Min(100L, (long)request.Offset + request.Limit));
        var nameTask = Task.Run(
            () => NoiseAwareEverythingSearch.Search(_everything, new StructuredSearchRequest(
                request.Query,
                request.Path,
                request.Extensions,
                SearchItemKind.Any,
                request.ModifiedAfter,
                request.ModifiedBefore,
                Limit: nameLimit)),
            cancellationToken);
        var contentTask = Task.Run(
            () => _content.SearchAsync(new ContentSearchRequest(
                request.Query,
                request.Path,
                request.Extensions,
                request.ModifiedAfter,
                request.ModifiedBefore,
                Limit: 50,
                Field: ContentSearchField.All), cancellationToken),
            cancellationToken);

        SearchResponse? nameResponse = null;
        ContentSearchResponse? contentResponse = null;
        Exception? nameError = null;
        Exception? contentError = null;
        try
        {
            nameResponse = await nameTask;
        }
        catch (Exception exception) when (exception is AIEverythingException)
        {
            nameError = exception;
        }

        try
        {
            contentResponse = await contentTask;
        }
        catch (Exception exception) when (exception is ContentIndexException)
        {
            contentError = exception;
        }

        if (nameResponse is null && contentResponse is null)
        {
            throw contentError ?? nameError ?? new InvalidOperationException("Both search sources failed.");
        }

        var fused = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);
        if (nameResponse is not null)
        {
            for (var index = 0; index < nameResponse.Items.Count; index++)
            {
                var item = nameResponse.Items[index];
                var accumulator = GetOrAdd(
                    fused, item.FullPath, item.FullPath, item.Name, item.Extension, item.Kind);
                accumulator.Size = item.Kind == SearchItemKind.Folder ? null : item.Size;
                accumulator.ModifiedAt = item.ModifiedAt;
                accumulator.NameMatched = true;
                accumulator.NameNoise = SearchResultRanking.ClassifyNoise(request.Query, item);
                accumulator.Score += 1d / (RrfK + index + 1);
            }
        }

        if (contentResponse is not null)
        {
            for (var index = 0; index < contentResponse.Items.Count; index++)
            {
                var item = contentResponse.Items[index];
                fused.TryGetValue(item.FullPath, out var nameOnly);
                var key = $"{item.FullPath}\0{item.StartLine}\0{item.JsonPath}";
                var accumulator = GetOrAdd(
                    fused, key, item.FullPath, item.Name, item.Extension, SearchItemKind.File);
                accumulator.Size ??= item.Size;
                accumulator.ModifiedAt ??= item.ModifiedAt;
                accumulator.Snippet = item.BodyMatched ? item.Snippet : null;
                accumulator.NameMatched |= item.TitleMatched || nameOnly?.NameMatched == true;
                accumulator.ContentMatched |= item.BodyMatched;
                accumulator.TitleMatched |= item.TitleMatched;
                accumulator.StartLine = item.StartLine;
                accumulator.EndLine = item.EndLine;
                accumulator.HeadingPath = item.HeadingPath;
                accumulator.JsonPath = item.JsonPath;
                accumulator.LocationLabel = item.LocationLabel;
                accumulator.Imported = item.Imported;
                accumulator.Score += 1d / (RrfK + index + 1);
                if (nameOnly is not null)
                {
                    accumulator.Score += nameOnly.Score;
                    fused.Remove(item.FullPath);
                }
            }
        }

        foreach (var item in fused.Values)
        {
            if (item.Name.Equals(request.Query, StringComparison.OrdinalIgnoreCase))
            {
                item.Score *= 1.5;
            }

            if (item.TitleMatched)
            {
                item.Score *= 1.2;
            }
        }

        var ordered = fused.Values
            .OrderBy(item => item.ContentMatched ? 0 : item.NameNoise == SearchNoiseLevel.Normal ? 1 : 2)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(item => new HybridSearchItem(
                item.Name,
                item.FullPath,
                item.Extension,
                item.Kind,
                item.Size,
                item.ModifiedAt,
                item.Snippet,
                item.Score,
                item.NameMatched && item.ContentMatched
                    ? "both"
                    : item.NameMatched ? "name" : "content",
                item.StartLine,
                item.EndLine,
                item.HeadingPath,
                item.JsonPath,
                item.LocationLabel,
                item.Imported,
                item.NameNoise))
            .ToArray();
        var page = ordered.Skip(request.Offset).Take(request.Limit).ToArray();
        stopwatch.Stop();
        return new HybridSearchResponse(
            request.Query,
            ordered.Length,
            page.Length,
            request.Offset,
            request.Limit,
            stopwatch.Elapsed.TotalMilliseconds,
            page);
    }

    private static Accumulator GetOrAdd(
        IDictionary<string, Accumulator> values,
        string key,
        string fullPath,
        string name,
        string extension,
        SearchItemKind kind)
    {
        if (!values.TryGetValue(key, out var item))
        {
            item = new Accumulator(name, fullPath, extension, kind);
            values.Add(key, item);
        }

        return item;
    }

    private static void Validate(HybridSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query) ||
            request.Limit is < 1 or > 100 ||
            request.Offset < 0 ||
            request.ModifiedAfter > request.ModifiedBefore)
        {
            throw new ContentIndexException(
                ContentErrorCodes.QueryTooBroad,
                "Hybrid search query, limit, offset, or time range is invalid.",
                "Provide a query, limit 1..100, non-negative offset, and ordered time range.");
        }
    }

    private sealed class Accumulator(
        string name,
        string fullPath,
        string extension,
        SearchItemKind kind)
    {
        internal string Name { get; } = name;
        internal string FullPath { get; } = fullPath;
        internal string Extension { get; } = extension;
        internal SearchItemKind Kind { get; } = kind;
        internal long? Size { get; set; }
        internal DateTimeOffset? ModifiedAt { get; set; }
        internal string? Snippet { get; set; }
        internal double Score { get; set; }
        internal bool NameMatched { get; set; }
        internal bool ContentMatched { get; set; }
        internal bool TitleMatched { get; set; }
        internal SearchNoiseLevel NameNoise { get; set; }
        internal int? StartLine { get; set; }
        internal int? EndLine { get; set; }
        internal string? HeadingPath { get; set; }
        internal string? JsonPath { get; set; }
        internal string? LocationLabel { get; set; }
        internal bool Imported { get; set; }
    }
}
