using System.Text;
using AIEverything.Desktop;
using AIEverything.Desktop.Ranking;
using Microsoft.Data.Sqlite;

namespace AIEverything.Server.Tests.Desktop;

public sealed class RankingBehaviorStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"aieverything-ranking-{Guid.NewGuid():N}");

    [Fact]
    public async Task Action_weights_decay_and_file_directory_extension_affinities_compose()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-30T09:00:00Z"));
        var path = Path.Combine(_directory, "ranking.db");
        await using var store = new SqliteRankingBehaviorStore(path, clock);
        await store.RecordAsync(Feedback(@"D:\docs\plan.md", RankingActionType.Open));
        await store.RecordAsync(Feedback(@"D:\copy\copy.pdf", RankingActionType.CopyReference) with
        {
            PreviewedBeforeAction = true
        });
        await store.RecordAsync(Feedback(@"D:\locate\locate.txt", RankingActionType.Locate));
        await store.RecordAsync(Feedback(@"D:\preview\preview.log", RankingActionType.PreviewConfirmed));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));

        var values = await store.ReadAsync(
            [
                new RankingIdentity(@"D:\docs\plan.md", "md"),
                new RankingIdentity(@"D:\docs\sibling.md", "md"),
                new RankingIdentity(@"D:\elsewhere\other.md", "md"),
                new RankingIdentity(@"D:\copy\copy.pdf", "pdf"),
                new RankingIdentity(@"D:\locate\locate.txt", "txt"),
                new RankingIdentity(@"D:\preview\preview.log", "log")
            ],
            clock.GetUtcNow());

        var decay = Math.Pow(2, -0.5);
        Assert.Equal(1.4 * decay, values[@"D:\docs\plan.md"].Score, 6);
        Assert.Equal("\u6700\u8fd1\u5e38\u7528", values[@"D:\docs\plan.md"].Reason);
        Assert.Equal(0.4 * decay, values[@"D:\docs\sibling.md"].Score, 6);
        Assert.Equal(0.1 * decay, values[@"D:\elsewhere\other.md"].Score, 6);
        Assert.Equal(1.25 * 1.4 * decay, values[@"D:\copy\copy.pdf"].Score, 6);
        Assert.Equal(0.5 * 1.4 * decay, values[@"D:\locate\locate.txt"].Score, 6);
        Assert.DoesNotContain(@"D:\preview\preview.log", values.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Records_only_one_daily_aggregate_and_returns_a_path_affinity()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        var path = Path.Combine(_directory, "ranking.db");
        await using var store = new SqliteRankingBehaviorStore(path, clock);
        var feedback = Feedback(@"D:\docs\project-plan.md", RankingActionType.Open);

        await store.RecordAsync(feedback);
        await store.RecordAsync(feedback);
        var values = await store.ReadAsync(
            [new RankingIdentity(@"d:\DOCS\project-plan.md", "md")], clock.GetUtcNow());

        var affinity = Assert.Single(values).Value;
        Assert.InRange(affinity.Promotion, 1, 10);
        Assert.False(string.IsNullOrWhiteSpace(affinity.Reason));
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),SUM(event_count) FROM behavior_daily;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
    }

    [Fact]
    public async Task Read_prunes_aggregates_older_than_thirty_calendar_days()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-06-01T09:00:00Z"));
        var path = Path.Combine(_directory, "ranking.db");
        await using var store = new SqliteRankingBehaviorStore(path, clock);
        await store.RecordAsync(Feedback(@"D:\archive\old.txt", RankingActionType.Open));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        await store.RecordAsync(Feedback(@"D:\docs\current.md", RankingActionType.Open));

        var values = await store.ReadAsync(
            [
                new RankingIdentity(@"D:\archive\old.txt", "txt"),
                new RankingIdentity(@"D:\docs\current.md", "md")
            ],
            clock.GetUtcNow());

        Assert.DoesNotContain(@"D:\archive\old.txt", values.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"D:\docs\current.md", values.Keys, StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM behavior_daily;";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Clear_removes_all_behavior_aggregates()
    {
        var path = Path.Combine(_directory, "ranking.db");
        await using var store = new SqliteRankingBehaviorStore(path);
        await store.RecordAsync(Feedback(@"D:\docs\notes.txt", RankingActionType.CopyReference));

        await store.ClearAsync();

        var values = await store.ReadAsync(
            [new RankingIdentity(@"D:\docs\notes.txt", "txt")], DateTimeOffset.UtcNow);
        Assert.Empty(values);
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM behavior_daily;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Clear_rotates_salt_and_the_same_path_receives_a_new_file_key()
    {
        var path = Path.Combine(_directory, "ranking.db");
        await using var store = new SqliteRankingBehaviorStore(path);
        var feedback = Feedback(@"D:\docs\private-notes.txt", RankingActionType.Open);
        await store.RecordAsync(feedback);
        var firstSalt = await ReadBlobHexAsync(path, "SELECT salt FROM behavior_meta WHERE id = 1;");
        var firstFileKey = await ReadBlobHexAsync(path, "SELECT file_key FROM behavior_daily;");

        await store.ClearAsync();
        await store.RecordAsync(feedback);

        var nextSalt = await ReadBlobHexAsync(path, "SELECT salt FROM behavior_meta WHERE id = 1;");
        var nextFileKey = await ReadBlobHexAsync(path, "SELECT file_key FROM behavior_daily;");
        Assert.NotEqual(firstSalt, nextSalt);
        Assert.NotEqual(firstFileKey, nextFileKey);
    }

    [Fact]
    public async Task Preview_only_does_not_create_or_write_a_behavior_database()
    {
        var path = Path.Combine(_directory, "ranking.db");
        await using var store = new SqliteRankingBehaviorStore(path);

        await store.RecordAsync(Feedback(@"D:\docs\preview-only.txt", RankingActionType.PreviewConfirmed));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Database_never_contains_raw_path_query_or_content_columns()
    {
        var path = Path.Combine(_directory, "ranking.db");
        const string parentCanary = "Quarterly-Secret-Parent-Canary";
        const string fileCanary = "Quarterly-Secret-File-Canary";
        const string transientCanary = "Transient-Query-Snippet-Canary";
        var canary = $@"D:\{parentCanary}\{fileCanary}.md";
        await using (var store = new SqliteRankingBehaviorStore(path))
        {
            await store.RecordAsync(Feedback(canary, RankingActionType.Open) with
            {
                MatchSource = transientCanary
            });
        }

        var columns = new List<string>();
        await using (var connection = await OpenAsync(path))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('behavior_daily') ORDER BY cid;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.DoesNotContain(columns, value => value.Contains("path", StringComparison.OrdinalIgnoreCase) &&
                                                !value.Equals("path_hash", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, value => value.Contains("query", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, value => value.Contains("snippet", StringComparison.OrdinalIgnoreCase) ||
                                                value.Contains("content", StringComparison.OrdinalIgnoreCase));
        foreach (var databaseFile in Directory.GetFiles(_directory, "ranking.db*"))
        {
            var contents = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(databaseFile));
            Assert.DoesNotContain(parentCanary, contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fileCanary, contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(transientCanary, contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Legacy_path_hash_table_is_preserved_until_explicit_clear()
    {
        var path = Path.Combine(_directory, "ranking.db");
        Directory.CreateDirectory(_directory);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version = 1;
                CREATE TABLE behavior_daily (
                    day_utc INTEGER NOT NULL,
                    path_hash TEXT NOT NULL,
                    extension TEXT NOT NULL,
                    weight_sum REAL NOT NULL,
                    event_count INTEGER NOT NULL,
                    PRIMARY KEY(day_utc, path_hash, extension)
                );
                INSERT INTO behavior_daily(day_utc, path_hash, extension, weight_sum, event_count)
                VALUES(739480, 'LEGACY-HASH-CANARY', 'txt', 1.0, 1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteRankingBehaviorStore(path);
        _ = await store.ReadAsync(
            [new RankingIdentity(@"D:\docs\candidate.txt", "txt")],
            DateTimeOffset.Parse("2026-08-14T09:00:00Z"));

        Assert.Equal(1, await ReadInt32Async(path, "SELECT COUNT(*) FROM behavior_daily_legacy_v1;"));
        Assert.Equal(0, await ReadInt32Async(path, "SELECT COUNT(*) FROM behavior_daily;"));
        Assert.Equal(2, await ReadInt32Async(path, "PRAGMA user_version;"));

        await store.ClearAsync();

        Assert.Equal(0, await ReadInt32Async(path, "SELECT COUNT(*) FROM behavior_daily_legacy_v1;"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static RankingFeedback Feedback(string fullPath, RankingActionType action) => new(
        fullPath,
        Path.GetExtension(fullPath).TrimStart('.'),
        DesktopSearchMode.Hybrid,
        "name",
        action,
        BaselineRank: 4,
        PresentedRank: 1);

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> ReadBlobHexAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToHexString(Assert.IsType<byte[]>(await command.ExecuteScalarAsync()));
    }

    private static async Task<int> ReadInt32Async(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
