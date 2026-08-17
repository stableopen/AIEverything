using System.ComponentModel;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Ipc;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Everything;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AIEverything.Mcp;

[McpServerToolType]
public sealed class AIEverythingTools
{
    private readonly IEverythingSearchService _searchService;
    private readonly IContentSearchService _contentService;
    private readonly HybridSearchService _hybridService;

    public AIEverythingTools(IEverythingSearchService searchService)
        : this(
            searchService,
            new ContentDaemonClient(ContentPipeNaming.ForCurrentUser(), TimeSpan.FromSeconds(2)),
            null)
    {
    }

    public AIEverythingTools(
        IEverythingSearchService searchService,
        IContentSearchService contentService,
        HybridSearchService? hybridService)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _contentService = contentService ?? throw new ArgumentNullException(nameof(contentService));
        _hybridService = hybridService ?? new HybridSearchService(searchService, contentService);
    }

    [McpServerTool(
        Name = "search_local_files",
        Title = "Search local files",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Quickly find local Windows files and folders by filename, path, extension, modified time, and indexed metadata through Everything. This does not search document contents.")]
    public SearchResponse SearchLocalFiles(
        [Description("Literal filename or folder-name text. Leave empty when filters alone are sufficient.")] string query = "",
        [Description("Optional absolute Windows directory path used to narrow results.")] string? path = null,
        [Description("Optional extensions without wildcards, such as pdf, docx, or csv.")] string[]? extensions = null,
        [Description("Result kind: any, file, or folder.")] string kind = "any",
        [Description("Optional inclusive lower modified-time bound as an ISO 8601 timestamp.")] DateTimeOffset? modifiedAfter = null,
        [Description("Optional inclusive upper modified-time bound as an ISO 8601 timestamp.")] DateTimeOffset? modifiedBefore = null,
        [Description("Sort field: name, path, size, or modified.")] string sortBy = "name",
        [Description("Sort direction: asc or desc.")] string sortDirection = "asc",
        [Description("Maximum results to return, from 1 to 100.")] int limit = 20,
        [Description("Zero-based result offset for pagination.")] int offset = 0)
    {
        try
        {
            return _searchService.Search(new StructuredSearchRequest(
                query,
                path,
                extensions,
                ParseKind(kind),
                modifiedAfter,
                modifiedBefore,
                ParseSortBy(sortBy),
                ParseSortDirection(sortDirection),
                limit,
                offset));
        }
        catch (AIEverythingException exception)
        {
            throw ToMcpException(exception);
        }
    }

    [McpServerTool(
        Name = "search_everything_query",
        Title = "Run an Everything query",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Run native Everything 1.4 search syntax against local filenames, paths, and indexed metadata. This does not search document contents.")]
    public SearchResponse SearchEverythingQuery(
        [Description("Raw Everything 1.4 query syntax, such as ext:pdf dm:thisweek.")] string query,
        [Description("Sort field: name, path, size, or modified.")] string sortBy = "name",
        [Description("Sort direction: asc or desc.")] string sortDirection = "asc",
        [Description("Maximum results to return, from 1 to 100.")] int limit = 20,
        [Description("Zero-based result offset for pagination.")] int offset = 0)
    {
        try
        {
            return _searchService.SearchRaw(
                query,
                limit,
                offset,
                ParseSortBy(sortBy),
                ParseSortDirection(sortDirection));
        }
        catch (AIEverythingException exception)
        {
            throw ToMcpException(exception);
        }
    }

    [McpServerTool(
        Name = "aieverything_status",
        Title = "Check AIEverything status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Check whether the Everything SDK and local index are ready without starting or changing Everything.")]
    public AIEverythingStatus GetStatus() => _searchService.GetStatus();

    [McpServerTool(
        Name = "search_local_content",
        Title = "Search local document content",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Search the existing local AIEverything TXT/MD/MARKDOWN body index. Returns matching snippets and never scans an entire drive on demand.")]
    public async Task<ContentSearchResponse> SearchLocalContent(
        [Description("Required body-text keywords. Use at least two CJK characters for Chinese queries.")] string query,
        [Description("Optional absolute local path used to narrow indexed results.")] string? path = null,
        [Description("Optional body extensions: txt, md, or markdown.")] string[]? extensions = null,
        [Description("Optional inclusive lower modified-time bound as ISO 8601.")] DateTimeOffset? modifiedAfter = null,
        [Description("Optional inclusive upper modified-time bound as ISO 8601.")] DateTimeOffset? modifiedBefore = null,
        [Description("Maximum results from 1 to 100.")] int limit = 20,
        [Description("Zero-based result offset.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _contentService.SearchAsync(new ContentSearchRequest(
                query,
                path,
                extensions,
                modifiedAfter,
                modifiedBefore,
                limit,
                offset,
                ContentSearchField.Body), cancellationToken);
        }
        catch (ContentIndexException exception)
        {
            throw ToMcpException(exception);
        }
    }

    [McpServerTool(
        Name = "search_local_hybrid",
        Title = "Search local filenames and document content",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Combine Everything filename/path results with the existing local AIEverything TXT/MD/MARKDOWN body index. Results identify name, content, or both as the match source.")]
    public async Task<HybridSearchResponse> SearchLocalHybrid(
        [Description("Required filename or document-body keywords.")] string query,
        [Description("Optional absolute local path used to narrow results.")] string? path = null,
        [Description("Optional extensions; body matches are limited to txt, md, and markdown.")] string[]? extensions = null,
        [Description("Optional inclusive lower modified-time bound as ISO 8601.")] DateTimeOffset? modifiedAfter = null,
        [Description("Optional inclusive upper modified-time bound as ISO 8601.")] DateTimeOffset? modifiedBefore = null,
        [Description("Maximum fused results from 1 to 100.")] int limit = 20,
        [Description("Zero-based fused result offset.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _hybridService.SearchAsync(new HybridSearchRequest(
                query,
                path,
                extensions,
                modifiedAfter,
                modifiedBefore,
                limit,
                offset), cancellationToken);
        }
        catch (ContentIndexException exception)
        {
            throw ToMcpException(exception);
        }
        catch (AIEverythingException exception)
        {
            throw ToMcpException(exception);
        }
    }

    [McpServerTool(
        Name = "aieverything_index_status",
        Title = "Check content index status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Check machine-index readiness, queued files, indexed documents, failures, and pause state.")]
    public async Task<ContentIndexStatus> GetIndexStatus(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _contentService.GetStatusAsync(cancellationToken);
        }
        catch (ContentIndexException exception)
        {
            throw ToMcpException(exception);
        }
    }

    private static SearchItemKind ParseKind(string? value) => value?.ToLowerInvariant() switch
    {
        "any" => SearchItemKind.Any,
        "file" => SearchItemKind.File,
        "folder" => SearchItemKind.Folder,
        _ => throw InvalidEnum("kind", value, "any, file, or folder")
    };

    private static SearchSortBy ParseSortBy(string? value) => value?.ToLowerInvariant() switch
    {
        "name" => SearchSortBy.Name,
        "path" => SearchSortBy.Path,
        "size" => SearchSortBy.Size,
        "modified" => SearchSortBy.Modified,
        _ => throw InvalidEnum("sortBy", value, "name, path, size, or modified")
    };

    private static SearchSortDirection ParseSortDirection(string? value) => value?.ToLowerInvariant() switch
    {
        "asc" => SearchSortDirection.Asc,
        "desc" => SearchSortDirection.Desc,
        _ => throw InvalidEnum("sortDirection", value, "asc or desc")
    };

    private static McpException InvalidEnum(string parameter, string? value, string choices) =>
        new($"INVALID_QUERY: {parameter} value '{value}' is invalid; use {choices}. " +
            "Corrective action: Correct the search text or structured filter.");

    private static McpException ToMcpException(AIEverythingException exception) =>
        new($"{exception.Code}: {exception.Message} Corrective action: {exception.CorrectiveAction} " +
            $"Native error code: {exception.NativeErrorCode}.", exception);

    private static McpException ToMcpException(ContentIndexException exception) =>
        new($"{exception.Code}: {exception.Message} Corrective action: {exception.CorrectiveAction}", exception);
}
