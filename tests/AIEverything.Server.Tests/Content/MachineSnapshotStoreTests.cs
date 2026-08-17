using System.Text;
using AIEverything.Content.Contracts;
using AIEverything.Content.Extraction;
using AIEverything.Content.MachineIndex;
using AIEverything.Content.Storage;

namespace AIEverything.Server.Tests.Content;

public sealed class MachineSnapshotStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aieverything-v020-snapshot", Guid.NewGuid().ToString("N"));
    private ContentIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new ContentIndexStore(Path.Combine(_root, "content.db"));
        await _store.InitializeAsync(CancellationToken.None);
        await _store.ConfigureAsync(disclosureAccepted: true, enabled: true, CancellationToken.None);
    }

    [Fact]
    public async Task Complete_snapshot_queues_changed_candidates_and_removes_disappeared_documents()
    {
        var first = Write("first.txt", "old searchable");
        var second = Write("second.md", "stay searchable");
        await CommitAndProcessAsync(first, second);

        File.WriteAllText(second, "changed searchable", Encoding.UTF8);
        var scan = await _store.BeginCandidateScanAsync(CancellationToken.None);
        await _store.StageCandidateAsync(scan, FileCandidate.FromFile(second, priority: 0), CancellationToken.None);
        await _store.EnqueueAsync(FileCandidate.FromFile(second, priority: 0), CancellationToken.None);
        var result = await _store.CommitCandidateScanAsync(scan, CancellationToken.None);

        Assert.Equal(1, result.Queued);
        Assert.Equal(1, result.Removed);
        var status = await _store.GetStatusAsync(CancellationToken.None);
        Assert.Equal("ready", status.SyncState);
        Assert.NotNull(status.LastSynchronizedAt);
        Assert.Empty((await _store.SearchAsync(new ContentSearchRequest("old"), CancellationToken.None)).Items);
        Assert.True(await ProcessOneAsync(new CompositeTextExtractor(
            new PlainTextExtractor(), new OpenXmlTextExtractor(), new PdfTextExtractor())));
        Assert.Single((await _store.SearchAsync(new ContentSearchRequest("changed"), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task Aborted_partial_snapshot_keeps_old_documents_and_never_cleans_them()
    {
        var first = Write("first.txt", "durable searchable");
        await CommitAndProcessAsync(first);
        var scan = await _store.BeginCandidateScanAsync(CancellationToken.None);
        await _store.StageCandidateAsync(
            scan,
            FileCandidate.FromFile(Write("partial.txt", "partial"), priority: 0),
            CancellationToken.None);

        await _store.AbortCandidateScanAsync(scan, "Everything unavailable", CancellationToken.None);

        Assert.Single((await _store.SearchAsync(new ContentSearchRequest("durable"), CancellationToken.None)).Items);
        var status = await _store.GetStatusAsync(CancellationToken.None);
        Assert.Contains("Everything", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_hides_and_cleans_a_deleted_source_before_the_next_full_snapshot()
    {
        var path = Write("deleted.txt", "stale searchable");
        await CommitAndProcessAsync(path);
        File.Delete(path);

        var response = await _store.SearchAsync(
            new ContentSearchRequest("stale"), CancellationToken.None);

        Assert.Equal(0, response.TotalResults);
        Assert.Empty(response.Items);
        Assert.Equal(0, (await _store.GetStatusAsync(CancellationToken.None)).IndexedDocuments);
    }

    private async Task CommitAndProcessAsync(params string[] paths)
    {
        var scan = await _store.BeginCandidateScanAsync(CancellationToken.None);
        foreach (var path in paths)
        {
            await _store.StageCandidateAsync(scan, FileCandidate.FromFile(path, priority: 0), CancellationToken.None);
            await _store.EnqueueAsync(FileCandidate.FromFile(path, priority: 0), CancellationToken.None);
        }
        await _store.CommitCandidateScanAsync(scan, CancellationToken.None);
        var extractor = new CompositeTextExtractor(
            new PlainTextExtractor(), new OpenXmlTextExtractor(), new PdfTextExtractor());
        while (await ProcessOneAsync(extractor)) { }
    }

    private Task<bool> ProcessOneAsync(ITextExtractor extractor) =>
        new AIEverything.Content.Indexing.ContentIndexer(_store, extractor)
            .ProcessOneAsync(CancellationToken.None);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
