using AIEverything.Content.Storage;
using AIEverything.Content.Contracts;
using AIEverything.Content.Extraction;

namespace AIEverything.Server.Tests.Content;

public sealed class ContentIndexDefaultsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"aieverything-content-defaults-{Guid.NewGuid():N}");

    [Fact]
    public async Task New_database_enables_body_indexing_without_a_disclosure_step()
    {
        await using var store = await OpenAsync();

        var status = await store.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Enabled);
        Assert.True(status.DisclosureAccepted);
    }

    [Fact]
    public async Task Explicitly_disabled_database_stays_disabled_after_restart()
    {
        await using (var store = await OpenAsync())
        {
            await store.ConfigureAsync(
                disclosureAccepted: true,
                enabled: false,
                CancellationToken.None);
        }

        await using var reopened = await OpenAsync();
        var status = await reopened.GetStatusAsync(CancellationToken.None);

        Assert.False(status.Enabled);
        Assert.True(status.DisclosureAccepted);
    }

    [Fact]
    public async Task Enabled_database_stays_enabled_after_restart()
    {
        var path = Path.Combine(_directory, "existing.txt");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(path, "existing searchable content");
        await using (var store = await OpenAsync())
        {
            await store.ConfigureAsync(
                disclosureAccepted: true,
                enabled: true,
                CancellationToken.None);
            await store.EnqueueAsync(
                FileCandidate.FromFile(path, priority: 0), CancellationToken.None);
            var lease = Assert.IsType<QueueLease>(
                await store.LeaseNextAsync(CancellationToken.None));
            await store.CompleteAsync(
                lease,
                new ExtractionResult("existing searchable content", false, 27),
                CancellationToken.None);
            Assert.Equal(1, (await store.GetStatusAsync(CancellationToken.None)).IndexedDocuments);
        }

        await using var reopened = await OpenAsync();
        var status = await reopened.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Enabled);
        Assert.True(status.DisclosureAccepted);
        Assert.Equal(1, status.IndexedDocuments);
    }

    private async Task<ContentIndexStore> OpenAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new ContentIndexStore(Path.Combine(_directory, "content.db"));
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
