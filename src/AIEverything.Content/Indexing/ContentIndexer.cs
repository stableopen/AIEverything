using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Extraction;
using AIEverything.Content.Storage;

namespace AIEverything.Content.Indexing;

public sealed class ContentIndexer
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1)
    ];

    private readonly ContentIndexStore _store;
    private readonly ITextExtractor _extractor;
    public ContentIndexer(
        ContentIndexStore store,
        ITextExtractor extractor)
    {
        _store = store;
        _extractor = extractor;
    }

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        if (await _store.GetPausedAsync(cancellationToken))
        {
            return false;
        }

        var lease = await _store.LeaseNextAsync(cancellationToken);
        if (lease is null)
        {
            return false;
        }

        if (!File.Exists(lease.FullPath))
        {
            await _store.RemoveDocumentAsync(lease.FullPath, cancellationToken);
            return true;
        }

        try
        {
            var extraction = await _extractor.ExtractAsync(
                new ExtractionRequest(lease.FullPath, lease.MaxBytes, lease.MaxCharacters), cancellationToken);
            await _store.CompleteAsync(lease, extraction, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ContentIndexException exception)
        {
            await ScheduleFailureAsync(lease, exception.Code, exception.Message, cancellationToken);
        }
        catch (Exception exception)
        {
            await ScheduleFailureAsync(
                lease,
                ContentErrorCodes.ExtractionFailed,
                exception.Message,
                cancellationToken);
        }

        return true;
    }

    public Task RemovePathAsync(string fullPath, CancellationToken cancellationToken) =>
        _store.RemoveDocumentAsync(Path.GetFullPath(fullPath), cancellationToken);

    private Task ScheduleFailureAsync(
        QueueLease lease,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var delayIndex = Math.Min(lease.Attempts, RetryDelays.Length - 1);
        return _store.FailAsync(
            lease,
            code,
            message,
            DateTimeOffset.UtcNow.Add(RetryDelays[delayIndex]),
            cancellationToken);
    }
}
