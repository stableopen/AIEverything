namespace AIEverything.Content.Contracts;

public sealed record AuthorizedRoot(
    long Id,
    string Path,
    IReadOnlyList<string> Excludes);

public enum ContentSearchField
{
    All,
    Title,
    Body
}

public sealed record ContentSearchRequest(
    string Query,
    string? RootPath = null,
    IReadOnlyList<string>? Extensions = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    int Limit = 20,
    int Offset = 0,
    ContentSearchField Field = ContentSearchField.All);

public sealed record ContentSearchItem(
    string Name,
    string FullPath,
    string Extension,
    long Size,
    DateTimeOffset ModifiedAt,
    string Snippet,
    double Score,
    bool TitleMatched,
    bool BodyMatched = true,
    int? StartLine = null,
    int? EndLine = null,
    string? HeadingPath = null,
    string? JsonPath = null,
    string? LocationLabel = null,
    bool Imported = false);

public sealed record ContentSearchResponse(
    string Query,
    int TotalResults,
    int ReturnedResults,
    int Offset,
    int Limit,
    double QueryDurationMs,
    IReadOnlyList<ContentSearchItem> Items);

public sealed record ContentIndexStatus(
    bool Ready,
    bool Paused,
    int RootCount,
    int IndexedDocuments,
    int QueuedDocuments,
    int FailedDocuments,
    DateTimeOffset? LastIndexedAt,
    string? DatabasePath,
    string? ErrorCode = null,
    string? Message = null,
    string? ServiceProtocolVersion = null,
    string? TextExtractionRevision = null,
    bool Enabled = false,
    bool DisclosureAccepted = false,
    string SyncState = "unknown",
    long DatabaseBytes = 0,
    int FilteredCandidates = 0,
    DateTimeOffset? LastSynchronizedAt = null);

public sealed record ContentIndexFailure(
    string RootPath,
    string FullPath,
    string ErrorCode,
    string Message,
    int Attempts,
    DateTimeOffset FailedAt);

public static class ContentServiceCompatibility
{
    public const string ProtocolVersion = "5";
    public const string TextExtractionRevision = "machine-source-location-v1";

    public static bool IsCompatible(ContentIndexStatus status) =>
        string.Equals(
            status.ServiceProtocolVersion,
            ProtocolVersion,
            StringComparison.Ordinal) &&
        string.Equals(
            status.TextExtractionRevision,
            TextExtractionRevision,
            StringComparison.Ordinal);
}

public sealed record RootOperationResponse(
    string Action,
    AuthorizedRoot? Root,
    IReadOnlyList<AuthorizedRoot> Roots,
    ContentIndexStatus Status);

public sealed record FileCandidate(
    string FullPath,
    string Name,
    string Extension,
    long Size,
    DateTimeOffset ModifiedAt,
    string Fingerprint,
    int Priority = 2,
    long MaxBytes = 5 * 1024 * 1024,
    int MaxCharacters = 1_000_000)
{
    public static FileCandidate FromFile(string fullPath, int priority)
    {
        var info = new FileInfo(Path.GetFullPath(fullPath));
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        return new FileCandidate(
            info.FullName, info.Name, info.Extension.TrimStart('.').ToLowerInvariant(), info.Length,
            modified,
            Indexing.ScopedFileEnumerator.CreateFingerprint(info.FullName, info.Length, modified),
            priority,
            5 * 1024 * 1024,
            1_000_000);
    }
}

public sealed record QueueLease(
    long Id,
    string FullPath,
    string Name,
    string Extension,
    long Size,
    DateTimeOffset ModifiedAt,
    string Fingerprint,
    int Attempts,
    long MaxBytes = 5 * 1024 * 1024,
    int MaxCharacters = 1_000_000);
