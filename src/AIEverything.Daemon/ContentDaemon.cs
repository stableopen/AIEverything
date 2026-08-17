using System.Diagnostics;
using System.Text.Json;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Extraction;
using AIEverything.Content.Indexing;
using AIEverything.Content.Ipc;
using AIEverything.Content.MachineIndex;
using AIEverything.Content.Storage;
using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.Daemon;

public sealed class ContentDaemon : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ContentDaemonOptions _options;
    private readonly ContentIndexStore _store;
    private readonly ContentIndexer _indexer;
    private readonly IEverythingSearchService _everything;
    private readonly MachineTextIndexPlan _plan;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly SemaphoreSlim _syncSignal = new(0, 1);
    private Mutex? _mutex;
    private int _started;

    public ContentDaemon(
        ContentDaemonOptions options,
        ITextExtractor? extractor = null,
        IEverythingSearchService? everything = null,
        MachineTextIndexPlan? plan = null)
    {
        _options = options;
        _store = new ContentIndexStore(Path.GetFullPath(options.DatabasePath));
        _indexer = new ContentIndexer(
            _store,
            extractor ?? new WorkerTextExtractor(options.WorkerPath, TimeSpan.FromSeconds(15)));
        _everything = everything ?? new EverythingSearchService(new EverythingNativeApi());
        _plan = plan ?? MachineTextIndexPolicy.BuildCurrentMachine();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Content daemon instance can only be started once.");
        AcquireSingleton();
        TrySetBelowNormalPriority();
        await _store.InitializeAsync(cancellationToken);
        var pipe = new ContentPipeServer(_options.PipeName, HandleRequestAsync);
        try
        {
            await Task.WhenAll(pipe.RunAsync(cancellationToken), RunQueueAsync(cancellationToken),
                RunReconcileAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<object> HandleRequestAsync(ContentDaemonRequest request, CancellationToken cancellationToken) =>
        request.Operation switch
        {
            "index.status" => await GetCompatibleStatusAsync(cancellationToken),
            "index.failures" => await _store.ListFailuresAsync(cancellationToken),
            "index.configure" => await ConfigureAsync(Deserialize<IndexConfigurationRequest>(request.Payload), cancellationToken),
            "index.pause" => await SetPausedAsync(true, cancellationToken),
            "index.resume" => await SetPausedAsync(false, cancellationToken),
            "index.sync" => await SyncNowAsync(cancellationToken),
            "content.search" => await _store.SearchAsync(Deserialize<ContentSearchRequest>(request.Payload), cancellationToken),
            _ => throw new ContentIndexException(ContentErrorCodes.InvalidArguments,
                $"Unknown daemon operation: {request.Operation}",
                "Use index.status/failures/configure/pause/resume/sync or content.search.")
        };

    private async Task<ContentIndexStatus> ConfigureAsync(IndexConfigurationRequest request, CancellationToken token)
    {
        await _store.ConfigureAsync(request.DisclosureAccepted, request.Enabled, token);
        if (request.DisclosureAccepted && request.Enabled && _syncSignal.CurrentCount == 0)
            _syncSignal.Release();
        return await GetCompatibleStatusAsync(token);
    }

    private async Task<ContentIndexStatus> SetPausedAsync(bool paused, CancellationToken token)
    {
        await _store.SetPausedAsync(paused, token);
        return await GetCompatibleStatusAsync(token);
    }

    private async Task<ContentIndexStatus> SyncNowAsync(CancellationToken token)
    {
        var status = await _store.GetStatusAsync(token);
        if (status.Enabled && status.DisclosureAccepted && !status.Paused)
            await SynchronizeCandidatesAsync(token, waitForActiveSync: true);
        return await GetCompatibleStatusAsync(token);
    }

    private async Task RunReconcileAsync(CancellationToken token)
    {
        var first = true;
        while (!token.IsCancellationRequested)
        {
            if (!first)
                await _syncSignal.WaitAsync(_options.ReconcileInterval, token);
            first = false;
            var status = await _store.GetStatusAsync(token);
            if (!status.Enabled || !status.DisclosureAccepted || status.Paused) continue;
            await SynchronizeCandidatesAsync(token);
        }
    }

    internal async Task<bool> SynchronizeCandidatesAsync(
        CancellationToken token,
        bool waitForActiveSync = false)
    {
        if (waitForActiveSync)
            await _syncGate.WaitAsync(token);
        else if (!await _syncGate.WaitAsync(0, token))
            return false;
        string? scanId = null;
        try
        {
            var status = _everything.GetStatus();
            if (!status.Ready)
            {
                scanId = await _store.BeginCandidateScanAsync(token);
                await _store.AbortCandidateScanAsync(scanId, status.Message, token);
                return false;
            }
            scanId = await _store.BeginCandidateScanAsync(token);
            var markerEntries = QueryAllMarkers(token);
            var prefixes = _plan.BuildDynamicExclusionPrefixes(markerEntries);
            foreach (var entry in QueryAllCandidates(token))
            {
                token.ThrowIfCancellationRequested();
                var decision = _plan.Evaluate(entry, prefixes);
                if (!decision.Accepted) continue;
                var fingerprint = ScopedFileEnumerator.CreateFingerprint(
                    entry.FullPath, entry.Size, entry.ModifiedAt);
                var candidate = new FileCandidate(entry.FullPath, entry.Name,
                    entry.Extension.TrimStart('.').ToLowerInvariant(), entry.Size, entry.ModifiedAt,
                    fingerprint, decision.Priority, decision.MaxBytes, decision.MaxCharacters);
                await _store.StageCandidateAsync(scanId, candidate, token);
                await _store.EnqueueAsync(candidate, token);
            }
            await _store.CommitCandidateScanAsync(scanId, token);
            return true;
        }
        catch (Exception exception) when (exception is AIEverythingException or IOException or UnauthorizedAccessException)
        {
            if (scanId is not null) await _store.AbortCandidateScanAsync(scanId, exception.Message, token);
            return false;
        }
        finally { _syncGate.Release(); }
    }

    private IEnumerable<CatalogEntry> QueryAllMarkers(CancellationToken token)
    {
        var markerQuery = "<" + string.Join('|', _plan.MarkerNames.Select(EscapeName)) + ">";
        return QueryPages(markerQuery, SearchSortBy.Path, SearchSortDirection.Asc, token);
    }

    private IEnumerable<CatalogEntry> QueryAllCandidates(CancellationToken token)
    {
        var extensionQuery = "file:<" + string.Join('|', _plan.SupportedExtensions
            .Select(extension => $"ext:{extension.TrimStart('.')}")) + ">";
        return QueryPages(extensionQuery, SearchSortBy.Modified, SearchSortDirection.Desc, token);
    }

    private IEnumerable<CatalogEntry> QueryPages(string query, SearchSortBy sortBy,
        SearchSortDirection direction, CancellationToken token)
    {
        const int pageSize = 100;
        for (var offset = 0; ; offset += pageSize)
        {
            token.ThrowIfCancellationRequested();
            var page = _everything.SearchRaw(query, pageSize, offset, sortBy, direction);
            foreach (var item in page.Items)
            {
                yield return new CatalogEntry(item.FullPath, item.Name, item.Extension,
                    item.Size ?? 0, item.ModifiedAt ?? DateTimeOffset.UnixEpoch, item.Attributes);
            }
            if (page.Items.Count < pageSize || offset + page.Items.Count >= page.TotalResults) yield break;
        }
    }

    private async Task RunQueueAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!await _indexer.ProcessOneAsync(token)) await Task.Delay(_options.IdleDelay, token);
        }
    }

    private async Task<ContentIndexStatus> GetCompatibleStatusAsync(CancellationToken token) =>
        (await _store.GetStatusAsync(token)) with
        {
            ServiceProtocolVersion = ContentServiceCompatibility.ProtocolVersion,
            TextExtractionRevision = ContentServiceCompatibility.TextExtractionRevision
        };

    private static string EscapeName(string value) => value.Replace("\"", "quot:", StringComparison.Ordinal);
    private static T Deserialize<T>(JsonElement payload) => payload.Deserialize<T>(JsonOptions) ??
        throw new ContentIndexException(ContentErrorCodes.InvalidArguments,
            "Daemon request payload is missing required fields.", "Send valid camelCase JSON fields.");

    private void AcquireSingleton()
    {
        var name = $"Local\\AIEverything.Daemon.{_options.PipeName}";
        _mutex = new Mutex(false, name, out var created);
        if (!created) { _mutex.Dispose(); _mutex = null; throw new ContentIndexException(
            ContentErrorCodes.IndexBusy, "Another content daemon is running.", "Use the existing daemon."); }
    }

    private static void TrySetBelowNormalPriority()
    {
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        (_everything as IDisposable)?.Dispose();
        _syncGate.Dispose();
        _syncSignal.Dispose();
        _mutex?.Dispose();
    }
}
