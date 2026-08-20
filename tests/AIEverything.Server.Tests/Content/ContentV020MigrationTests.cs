using AIEverything.Content.Storage;
using Microsoft.Data.Sqlite;

namespace AIEverything.Server.Tests.Content;

public sealed class ContentV020MigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aieverything-v020-migration", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Legacy_root_database_is_backed_up_then_replaced_without_migrating_roots_queue_or_failures()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "content.db");
        await CreateLegacyDatabaseAsync(database);
        await using var store = new ContentIndexStore(database);

        var first = await store.InitializeAsync(CancellationToken.None);
        var second = await store.InitializeAsync(CancellationToken.None);

        Assert.True(first.Migrated);
        Assert.NotNull(first.BackupPath);
        Assert.True(File.Exists(first.BackupPath));
        Assert.False(second.Migrated);
        Assert.Null(second.BackupPath);
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        Assert.Equal("21", await ScalarAsync(connection, "SELECT value FROM settings WHERE key='schema_version';"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM documents;"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM index_queue;"));
        Assert.Equal("0", await ScalarAsync(connection, "SELECT COUNT(*) FROM index_failures;"));
        await Assert.ThrowsAsync<SqliteException>(async () =>
            await ScalarAsync(connection, "SELECT COUNT(*) FROM roots;"));
        await connection.CloseAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE roots(id INTEGER PRIMARY KEY, path TEXT, excludes_json TEXT, created_at INTEGER);
            CREATE TABLE documents(id INTEGER PRIMARY KEY, root_id INTEGER, full_path TEXT, name TEXT, extension TEXT, size INTEGER, modified_at INTEGER, fingerprint TEXT, content TEXT, indexed_at INTEGER);
            CREATE TABLE index_queue(id INTEGER PRIMARY KEY, root_id INTEGER, full_path TEXT, name TEXT, extension TEXT, size INTEGER, modified_at INTEGER, fingerprint TEXT, attempts INTEGER, next_attempt_at INTEGER, state TEXT);
            CREATE TABLE index_failures(id INTEGER PRIMARY KEY, root_id INTEGER, full_path TEXT, fingerprint TEXT, error_code TEXT, message TEXT, attempts INTEGER, failed_at INTEGER);
            INSERT INTO settings VALUES('paused','true');
            INSERT INTO roots VALUES(1,'D:\docs','[]',0);
            INSERT INTO documents VALUES(1,1,'D:\docs\old.txt','old.txt','txt',1,0,'old','legacy body',0);
            INSERT INTO index_queue VALUES(1,1,'D:\docs\queued.txt','queued.txt','txt',1,0,'q',0,NULL,'pending');
            INSERT INTO index_failures VALUES(1,1,'D:\docs\bad.txt','b','bad','bad',3,0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
