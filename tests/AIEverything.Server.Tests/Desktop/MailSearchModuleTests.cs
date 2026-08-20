using AIEverything.Desktop.Mail;

namespace AIEverything.Server.Tests.Desktop;

public sealed class MailSearchModuleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"aieverything-mail-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task New_mail_index_defaults_to_enabled()
    {
        await using var module = CreateModule(new FakeMailSource([]));

        var status = await module.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Enabled);
        Assert.Equal(0, status.IndexedMessages);
        Assert.Empty(await module.SearchAsync("anything", 20, CancellationToken.None));
    }

    [Fact]
    public async Task V103_disabled_database_is_enabled_once_by_v104_default_migration()
    {
        await CreateV103SettingsAsync(enabled: false);

        await using var module = CreateModule(new FakeMailSource([]));
        var status = await module.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Enabled);
    }

    [Fact]
    public async Task User_disable_after_v104_migration_survives_restart()
    {
        await CreateV103SettingsAsync(enabled: false);
        await using (var module = CreateModule(new FakeMailSource([])))
        {
            Assert.True((await module.GetStatusAsync(CancellationToken.None)).Enabled);
            await module.ExecuteAsync(MailIndexCommand.Disable, CancellationToken.None);
        }

        await using var reopened = CreateModule(new FakeMailSource([]));
        Assert.False((await reopened.GetStatusAsync(CancellationToken.None)).Enabled);
    }

    [Fact]
    public async Task Startup_path_synchronizes_recent_mail_when_enabled()
    {
        var source = new FakeMailSource(
            [Message("store-a", "entry-1", "Startup subject", "startup searchable body")]);
        await using var module = CreateModule(source);

        var result = await module.SynchronizeOnStartupAsync(CancellationToken.None);

        Assert.True(result.Status.Enabled);
        Assert.Equal(1, result.Status.IndexedMessages);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task Enable_sync_search_and_repeat_sync_keep_one_updated_row_per_outlook_identity()
    {
        var source = new FakeMailSource(
        [
            Message("store-a", "entry-1", "Quarterly plan", "first searchable body"),
            Message("store-a", "entry-2", "Project update", "second searchable body")
        ]);
        await using var module = CreateModule(source);

        var first = await module.ExecuteAsync(
            MailIndexCommand.EnableAndSynchronize, CancellationToken.None);
        source.Messages =
        [
            Message("store-a", "entry-1", "Quarterly plan revised", "updated searchable body"),
            Message("store-a", "entry-2", "Project update", "second searchable body")
        ];
        var second = await module.ExecuteAsync(
            MailIndexCommand.Synchronize, CancellationToken.None);
        var revised = await module.SearchAsync("revised", 20, CancellationToken.None);

        Assert.True(first.Status.Enabled);
        Assert.Equal(2, first.Status.IndexedMessages);
        Assert.Equal(2, second.Status.IndexedMessages);
        Assert.Equal(2, source.ReadCount);
        var hit = Assert.Single(revised);
        Assert.Equal("entry-1", hit.Identity.EntryId);
        Assert.Contains("updated", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disable_hides_existing_mail_and_clear_removes_local_rows()
    {
        await using var module = CreateModule(new FakeMailSource(
            [Message("store-a", "entry-1", "Budget", "forecast needle")]));
        await module.ExecuteAsync(MailIndexCommand.EnableAndSynchronize, CancellationToken.None);

        var disabled = await module.ExecuteAsync(
            MailIndexCommand.Disable, CancellationToken.None);
        var hidden = await module.SearchAsync("needle", 20, CancellationToken.None);
        var cleared = await module.ExecuteAsync(MailIndexCommand.Clear, CancellationToken.None);

        Assert.False(disabled.Status.Enabled);
        Assert.Empty(hidden);
        Assert.Equal(0, cleared.Status.IndexedMessages);
    }

    private MailSearchModule CreateModule(IMailSource source)
    {
        Directory.CreateDirectory(_directory);
        return new MailSearchModule(Path.Combine(_directory, "mail.db"), source);
    }

    private async Task CreateV103SettingsAsync(bool enabled)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mail.db");
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);INSERT INTO settings(key,value) VALUES('enabled',$enabled);";
        command.Parameters.AddWithValue("$enabled", enabled ? "true" : "false");
        await command.ExecuteNonQueryAsync();
    }

    private static MailMessageSnapshot Message(
        string storeId,
        string entryId,
        string subject,
        string body) => new(
            new MailIdentity(storeId, entryId), subject, "Sender", "Recipient",
            new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
            "Inbox", body, "brief.docx");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeMailSource(IReadOnlyList<MailMessageSnapshot> messages) : IMailSource
    {
        public IReadOnlyList<MailMessageSnapshot> Messages { get; set; } = messages;
        public int ReadCount { get; private set; }
        public MailIdentity? Opened { get; private set; }

        public Task<MailReadBatch> ReadRecentAsync(int limit, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(new MailReadBatch(Messages.Take(limit).ToArray(), 0));
        }

        public Task OpenAsync(MailIdentity identity, CancellationToken cancellationToken)
        {
            Opened = identity;
            return Task.CompletedTask;
        }
    }
}
