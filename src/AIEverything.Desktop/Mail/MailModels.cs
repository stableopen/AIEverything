namespace AIEverything.Desktop.Mail;

public sealed record MailIdentity(string StoreId, string EntryId);

public sealed record MailMessageSnapshot(
    MailIdentity Identity,
    string Subject,
    string Sender,
    string Recipients,
    DateTimeOffset Timestamp,
    string Folder,
    string Body,
    string AttachmentNames);

public sealed record MailReadBatch(
    IReadOnlyList<MailMessageSnapshot> Messages,
    int SkippedMessages);

public sealed record MailSearchHit(
    MailIdentity Identity,
    string Subject,
    string Sender,
    string Recipients,
    DateTimeOffset Timestamp,
    string Folder,
    string Snippet,
    string AttachmentNames,
    double Score);

public sealed record MailIndexStatus(
    bool Enabled,
    int IndexedMessages,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    int LastSkippedMessages,
    string DatabasePath);

public sealed record MailCommandResult(
    MailIndexStatus Status,
    int SourceMessages = 0,
    int UpsertedMessages = 0,
    int SkippedMessages = 0);

public enum MailIndexCommand
{
    EnableAndSynchronize,
    Synchronize,
    Disable,
    Clear
}

public interface IMailSource
{
    Task<MailReadBatch> ReadRecentAsync(int limit, CancellationToken cancellationToken);
    Task OpenAsync(MailIdentity identity, CancellationToken cancellationToken);
}

public interface IMailSearch
{
    Task<IReadOnlyList<MailSearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public interface IMailSearchModule : IMailSearch
{
    Task<MailIndexStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<MailCommandResult> SynchronizeOnStartupAsync(CancellationToken cancellationToken);
    Task<MailCommandResult> ExecuteAsync(
        MailIndexCommand command,
        CancellationToken cancellationToken);
    Task OpenAsync(MailIdentity identity, CancellationToken cancellationToken);
}
