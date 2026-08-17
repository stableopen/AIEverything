using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Ipc;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.Cli;

public sealed class CliCommandRunner
{
    private const int DefaultBenchmarkIterations = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IEverythingSearchService _searchService;
    private readonly IContentSearchService _contentService;
    private readonly HybridSearchService _hybridService;

    public CliCommandRunner(IEverythingSearchService searchService)
        : this(
            searchService,
            new ContentDaemonClient(ContentPipeNaming.ForCurrentUser(), TimeSpan.FromSeconds(2)),
            null)
    {
    }

    public CliCommandRunner(
        IEverythingSearchService searchService,
        IContentSearchService contentService,
        HybridSearchService? hybridService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        _hybridService = hybridService ?? new HybridSearchService(searchService, contentService);
    }

    public async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (args.Length == 0)
            {
                throw new CliUsageException("A command is required: doctor, search, or benchmark.");
            }

            switch (args[0].ToLowerInvariant())
            {
                case "doctor":
                    RequireArgumentCount(args, 1, "doctor does not accept arguments.");
                    await WriteJsonAsync(stdout, _searchService.GetStatus(), cancellationToken);
                    return 0;

                case "search":
                    var request = ParseSearch(args);
                    await WriteJsonAsync(stdout, _searchService.Search(request), cancellationToken);
                    return 0;

                case "benchmark":
                    var iterations = ParseBenchmark(args);
                    var benchmark = RunBenchmark(iterations, cancellationToken);
                    await WriteJsonAsync(stdout, benchmark, cancellationToken);
                    return 0;

                case "content-index":
                    await WriteJsonAsync(
                        stdout,
                        await RunContentIndexAsync(args, cancellationToken),
                        cancellationToken);
                    return 0;

                case "content-search":
                    await WriteJsonAsync(
                        stdout,
                        await _contentService.SearchAsync(
                            ParseContentSearch(args), cancellationToken),
                        cancellationToken);
                    return 0;

                case "hybrid-search":
                    await WriteJsonAsync(
                        stdout,
                        await _hybridService.SearchAsync(
                            ParseHybridSearch(args), cancellationToken),
                        cancellationToken);
                    return 0;

                default:
                    throw new CliUsageException($"Unknown command: {args[0]}");
            }
        }
        catch (CliUsageException exception)
        {
            await WriteJsonAsync(stderr, new
            {
                code = "INVALID_ARGUMENTS",
                message = exception.Message,
                correctiveAction = "Run AIEverything.Server.exe doctor, search <query>, or benchmark --iterations <1..1000>."
            }, cancellationToken);
            return 2;
        }
        catch (AIEverythingException exception)
        {
            await WriteJsonAsync(stderr, new
            {
                code = exception.Code,
                message = exception.Message,
                correctiveAction = exception.CorrectiveAction,
                nativeErrorCode = exception.NativeErrorCode
            }, cancellationToken);
            return 1;
        }
        catch (ContentIndexException exception)
        {
            await WriteJsonAsync(stderr, new
            {
                code = exception.Code,
                message = exception.Message,
                correctiveAction = exception.CorrectiveAction
            }, cancellationToken);
            return 1;
        }
    }

    private async Task<ContentIndexStatus> RunContentIndexAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length == 2)
        {
            return args[1].ToLowerInvariant() switch
            {
                "status" => await _contentService.GetStatusAsync(cancellationToken),
                "pause" => await _contentService.SetPausedAsync(true, cancellationToken),
                "resume" => await _contentService.SetPausedAsync(false, cancellationToken),
                "sync" => await _contentService.SynchronizeAsync(cancellationToken),
                _ => throw new CliUsageException(
                    "Use content-index status, pause, resume, or sync.")
            };
        }

        throw new CliUsageException(
            "Use content-index status, pause, resume, or sync.");
    }

    private static ContentSearchRequest ParseContentSearch(string[] args)
    {
        var filters = ParseContentFilters(args, "content-search");
        return new ContentSearchRequest(
            filters.Query,
            filters.Path,
            filters.Extensions,
            filters.ModifiedAfter,
            filters.ModifiedBefore,
            filters.Limit,
            filters.Offset,
            ContentSearchField.Body);
    }

    private static HybridSearchRequest ParseHybridSearch(string[] args)
    {
        var filters = ParseContentFilters(args, "hybrid-search");
        return new HybridSearchRequest(
            filters.Query,
            filters.Path,
            filters.Extensions,
            filters.ModifiedAfter,
            filters.ModifiedBefore,
            filters.Limit,
            filters.Offset);
    }

    private static ContentFilters ParseContentFilters(string[] args, string command)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"{command} requires a query argument.");
        }

        string? path = null;
        var extensions = new List<string>();
        DateTimeOffset? modifiedAfter = null;
        DateTimeOffset? modifiedBefore = null;
        var limit = 20;
        var offset = 0;
        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--path":
                    path = ReadValue(args, ref index, "--path");
                    RequireAbsolutePath(path, "--path");
                    break;
                case "--ext":
                    extensions.Add(ReadValue(args, ref index, "--ext"));
                    break;
                case "--after":
                    modifiedAfter = ReadTimestamp(args, ref index, "--after");
                    break;
                case "--before":
                    modifiedBefore = ReadTimestamp(args, ref index, "--before");
                    break;
                case "--limit":
                    limit = ReadInteger(args, ref index, "--limit");
                    if (limit is < 1 or > 100)
                    {
                        throw new CliUsageException("--limit must be between 1 and 100.");
                    }
                    break;
                case "--offset":
                    offset = ReadInteger(args, ref index, "--offset");
                    if (offset < 0)
                    {
                        throw new CliUsageException("--offset must be non-negative.");
                    }
                    break;
                default:
                    throw new CliUsageException($"Unknown {command} option: {args[index]}");
            }
        }

        if (modifiedAfter > modifiedBefore)
        {
            throw new CliUsageException("--after must not be later than --before.");
        }

        return new ContentFilters(
            args[1],
            path,
            extensions.Count == 0 ? null : extensions,
            modifiedAfter,
            modifiedBefore,
            limit,
            offset);
    }

    private static StructuredSearchRequest ParseSearch(string[] args)
    {
        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException("search requires a query argument.");
        }

        var query = args[1];
        string? path = null;
        var extensions = new List<string>();
        var kind = SearchItemKind.Any;
        var sortBy = SearchSortBy.Name;
        var direction = SearchSortDirection.Asc;
        var limit = 20;
        var offset = 0;

        for (var index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--path":
                    path = ReadValue(args, ref index, "--path");
                    if (!Path.IsPathFullyQualified(path))
                    {
                        throw new CliUsageException("--path must be an absolute Windows path.");
                    }
                    break;

                case "--ext":
                    extensions.Add(ReadValue(args, ref index, "--ext"));
                    break;

                case "--kind":
                    kind = ReadValue(args, ref index, "--kind").ToLowerInvariant() switch
                    {
                        "any" => SearchItemKind.Any,
                        "file" => SearchItemKind.File,
                        "folder" => SearchItemKind.Folder,
                        _ => throw new CliUsageException("--kind must be any, file, or folder.")
                    };
                    break;

                case "--sort":
                    sortBy = ReadValue(args, ref index, "--sort").ToLowerInvariant() switch
                    {
                        "name" => SearchSortBy.Name,
                        "path" => SearchSortBy.Path,
                        "size" => SearchSortBy.Size,
                        "modified" => SearchSortBy.Modified,
                        _ => throw new CliUsageException("--sort must be name, path, size, or modified.")
                    };
                    break;

                case "--desc":
                    direction = SearchSortDirection.Desc;
                    break;

                case "--limit":
                    limit = ReadInteger(args, ref index, "--limit");
                    if (limit is < 1 or > 100)
                    {
                        throw new CliUsageException("--limit must be between 1 and 100.");
                    }
                    break;

                case "--offset":
                    offset = ReadInteger(args, ref index, "--offset");
                    if (offset < 0)
                    {
                        throw new CliUsageException("--offset must be non-negative.");
                    }
                    break;

                default:
                    throw new CliUsageException($"Unknown search option: {args[index]}");
            }
        }

        return new StructuredSearchRequest(
            query,
            path,
            extensions.Count == 0 ? null : extensions,
            kind,
            SortBy: sortBy,
            SortDirection: direction,
            Limit: limit,
            Offset: offset);
    }

    private static int ParseBenchmark(string[] args)
    {
        if (args.Length == 1)
        {
            return DefaultBenchmarkIterations;
        }

        if (args.Length != 3 || !args[1].Equals("--iterations", StringComparison.Ordinal))
        {
            throw new CliUsageException("benchmark accepts only --iterations <1..1000>.");
        }

        if (!int.TryParse(args[2], out var iterations) || iterations is < 1 or > 1000)
        {
            throw new CliUsageException("--iterations must be an integer between 1 and 1000.");
        }

        return iterations;
    }

    private object RunBenchmark(int iterations, CancellationToken cancellationToken)
    {
        _searchService.SearchRaw("Everything.exe", limit: 1);

        var durations = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            durations[index] = _searchService.SearchRaw("Everything.exe", limit: 1).QueryDurationMs;
        }

        Array.Sort(durations);
        var median = durations.Length % 2 == 1
            ? durations[durations.Length / 2]
            : (durations[(durations.Length / 2) - 1] + durations[durations.Length / 2]) / 2;
        var p95Index = Math.Max(0, (int)Math.Ceiling(durations.Length * 0.95) - 1);
        var p95 = durations[p95Index];

        return new
        {
            query = "Everything.exe",
            count = durations.Length,
            minMs = durations[0],
            medianMs = median,
            p95Ms = p95,
            maxMs = durations[^1],
            medianUnder10Ms = median < 10,
            p95Under50Ms = p95 < 50
        };
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static int ReadInteger(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        if (!int.TryParse(value, out var result))
        {
            throw new CliUsageException($"{option} requires an integer.");
        }

        return result;
    }

    private static DateTimeOffset ReadTimestamp(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            throw new CliUsageException($"{option} requires an ISO 8601 timestamp.");
        }

        return timestamp;
    }

    private static void RequireAbsolutePath(string path, string parameter)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new CliUsageException($"{parameter} must be an absolute Windows path.");
        }
    }

    private static void RequireArgumentCount(string[] args, int expected, string message)
    {
        if (args.Length != expected)
        {
            throw new CliUsageException(message);
        }
    }

    private static async Task WriteJsonAsync(
        TextWriter writer,
        object value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    private sealed class CliUsageException(string message) : Exception(message);

    private sealed record ContentFilters(
        string Query,
        string? Path,
        IReadOnlyList<string>? Extensions,
        DateTimeOffset? ModifiedAfter,
        DateTimeOffset? ModifiedBefore,
        int Limit,
        int Offset);
}
