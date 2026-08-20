using System.Globalization;
using System.Text;
using AIEverything.Content.Errors;
using AIEverything.Content.Text;
using Microsoft.Data.Sqlite;

namespace AIEverything.Desktop.Mail;

public sealed class MailSearchModule : IMailSearchModule, IAsyncDisposable
{
    public const int MaximumMessages = 100;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly IMailSource _source;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _initializationLock = new();
    private Task? _initializationTask;

    public MailSearchModule(string databasePath, IMailSource source)
    {
        if (!Path.IsPathFullyQualified(databasePath))
            throw new ArgumentException("Database path must be absolute.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIEverything",
        "mail.db");

    public async Task<MailIndexStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await InGateAsync(connection => ReadStatusAsync(connection, cancellationToken), cancellationToken);
    }

    public async Task<MailCommandResult> ExecuteAsync(
        MailIndexCommand command,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        switch (command)
        {
            case MailIndexCommand.EnableAndSynchronize:
                await SetEnabledAsync(true, cancellationToken);
                return await SynchronizeCoreAsync(cancellationToken);
            case MailIndexCommand.Synchronize:
                return await SynchronizeCoreAsync(cancellationToken);
            case MailIndexCommand.Disable:
                await SetEnabledAsync(false, cancellationToken);
                return new MailCommandResult(await GetStatusAsync(cancellationToken));
            case MailIndexCommand.Clear:
                await ClearCoreAsync(cancellationToken);
                return new MailCommandResult(await GetStatusAsync(cancellationToken));
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    public async Task<IReadOnlyList<MailSearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || limit is < 1 or > 100)
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken);
        string match;
        try
        {
            match = ContentTokenizer.BuildMatchQuery(query);
        }
        catch (ContentIndexException)
        {
            return [];
        }

        return await InGateAsync(async connection =>
        {
            if (!await ReadEnabledAsync(connection, cancellationToken))
            {
                return (IReadOnlyList<MailSearchHit>)[];
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.store_id,m.entry_id,m.subject,m.sender,m.recipients,
                       m.mail_time,m.folder,m.body,m.attachments,bm25(mail_fts) AS rank
                FROM mail_fts
                JOIN messages m ON m.id=mail_fts.rowid
                WHERE mail_fts MATCH $query
                ORDER BY rank,m.mail_time DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", match);
            command.Parameters.AddWithValue("$limit", limit);
            var hits = new List<MailSearchHit>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new MailSearchHit(
                    new MailIdentity(reader.GetString(0), reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    reader.GetString(6),
                    BuildSnippet(reader.GetString(7), query),
                    reader.GetString(8),
                    reader.GetDouble(9)));
            }

            return hits;
        }, cancellationToken);
    }

    public Task OpenAsync(MailIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.StoreId) || string.IsNullOrWhiteSpace(identity.EntryId))
            throw new ArgumentException("Outlook mail identity is incomplete.", nameof(identity));
        return _source.OpenAsync(identity, cancellationToken);
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        lock (_initializationLock)
        {
            _initializationTask ??= InitializeCoreAsync();
            return _initializationTask.WaitAsync(cancellationToken);
        }
    }

    private async Task InitializeCoreAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS settings(
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS messages(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    store_id TEXT NOT NULL,
                    entry_id TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    sender TEXT NOT NULL,
                    recipients TEXT NOT NULL,
                    mail_time INTEGER NOT NULL,
                    folder TEXT NOT NULL,
                    body TEXT NOT NULL,
                    attachments TEXT NOT NULL,
                    indexed_at INTEGER NOT NULL,
                    UNIQUE(store_id,entry_id)
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS mail_fts USING fts5(
                    search_tokens,
                    tokenize='unicode61 remove_diacritics 2'
                );
                INSERT OR IGNORE INTO settings(key,value) VALUES('enabled','false');
                INSERT OR IGNORE INTO settings(key,value) VALUES('last_skipped','0');
                """;
            await command.ExecuteNonQueryAsync();
        }, CancellationToken.None);
    }

    private async Task<MailCommandResult> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Enabled)
        {
            return new MailCommandResult(status);
        }

        MailReadBatch batch;
        try
        {
            batch = await _source.ReadRecentAsync(MaximumMessages, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await SetSyncFailureAsync(exception.Message, cancellationToken);
            return new MailCommandResult(await GetStatusAsync(cancellationToken));
        }

        var messages = batch.Messages
            .Where(IsValid)
            .OrderByDescending(message => message.Timestamp)
            .DistinctBy(message => (message.Identity.StoreId, message.Identity.EntryId))
            .Take(MaximumMessages)
            .ToArray();
        await InGateAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var scan = connection.CreateCommand())
            {
                scan.Transaction = (SqliteTransaction)transaction;
                scan.CommandText = "CREATE TEMP TABLE IF NOT EXISTS current_mail_ids(store_id TEXT,entry_id TEXT,PRIMARY KEY(store_id,entry_id));DELETE FROM current_mail_ids;";
                await scan.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var message in messages)
            {
                await UpsertAsync(connection, (SqliteTransaction)transaction, message, cancellationToken);
            }

            await using (var prune = connection.CreateCommand())
            {
                prune.Transaction = (SqliteTransaction)transaction;
                prune.CommandText = """
                    DELETE FROM mail_fts WHERE rowid IN(
                        SELECT id FROM messages WHERE NOT EXISTS(
                            SELECT 1 FROM current_mail_ids c
                            WHERE c.store_id=messages.store_id AND c.entry_id=messages.entry_id));
                    DELETE FROM messages WHERE NOT EXISTS(
                        SELECT 1 FROM current_mail_ids c
                        WHERE c.store_id=messages.store_id AND c.entry_id=messages.entry_id);
                    INSERT OR REPLACE INTO settings(key,value) VALUES('last_sync_at',$now);
                    INSERT OR REPLACE INTO settings(key,value) VALUES('last_skipped',$skipped);
                    DELETE FROM settings WHERE key='last_error';
                    """;
                prune.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    .ToString(CultureInfo.InvariantCulture));
                prune.Parameters.AddWithValue("$skipped", batch.SkippedMessages.ToString(CultureInfo.InvariantCulture));
                await prune.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

        return new MailCommandResult(
            await GetStatusAsync(cancellationToken),
            batch.Messages.Count,
            messages.Length,
            batch.SkippedMessages);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MailMessageSnapshot message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO current_mail_ids(store_id,entry_id) VALUES($store,$entry);
            INSERT INTO messages(store_id,entry_id,subject,sender,recipients,mail_time,folder,body,attachments,indexed_at)
            VALUES($store,$entry,$subject,$sender,$recipients,$time,$folder,$body,$attachments,$now)
            ON CONFLICT(store_id,entry_id) DO UPDATE SET
                subject=excluded.subject,sender=excluded.sender,recipients=excluded.recipients,
                mail_time=excluded.mail_time,folder=excluded.folder,body=excluded.body,
                attachments=excluded.attachments,indexed_at=excluded.indexed_at;
            """;
        command.Parameters.AddWithValue("$store", message.Identity.StoreId);
        command.Parameters.AddWithValue("$entry", message.Identity.EntryId);
        command.Parameters.AddWithValue("$subject", message.Subject);
        command.Parameters.AddWithValue("$sender", message.Sender);
        command.Parameters.AddWithValue("$recipients", message.Recipients);
        command.Parameters.AddWithValue("$time", message.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$folder", message.Folder);
        command.Parameters.AddWithValue("$body", message.Body);
        command.Parameters.AddWithValue("$attachments", message.AttachmentNames);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = "SELECT id FROM messages WHERE store_id=$store AND entry_id=$entry;";
        command.Parameters.AddWithValue("$store", message.Identity.StoreId);
        command.Parameters.AddWithValue("$entry", message.Identity.EntryId);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        command.Parameters.Clear();
        command.CommandText = "DELETE FROM mail_fts WHERE rowid=$id;INSERT INTO mail_fts(rowid,search_tokens) VALUES($id,$tokens);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$tokens", ContentTokenizer.TokenizeForIndex(string.Join(' ',
            message.Subject, message.Sender, message.Recipients, message.Body, message.AttachmentNames)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES('enabled',$enabled);";
            command.Parameters.AddWithValue("$enabled", enabled ? "true" : "false");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private Task ClearCoreAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM mail_fts;DELETE FROM messages;DELETE FROM settings WHERE key IN('last_sync_at','last_error');INSERT OR REPLACE INTO settings(key,value) VALUES('last_skipped','0');";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private Task SetSyncFailureAsync(string message, CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES('last_error',$error);";
            command.Parameters.AddWithValue("$error", Truncate(message, 300));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private async Task<MailIndexStatus> ReadStatusAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key,value FROM settings;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                settings[reader.GetString(0)] = reader.GetString(1);
            }
        }

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM messages;";
        var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        DateTimeOffset? lastSync = settings.TryGetValue("last_sync_at", out var rawSync) &&
                       long.TryParse(rawSync, CultureInfo.InvariantCulture, out var sync)
            ? DateTimeOffset.FromUnixTimeMilliseconds(sync)
            : null;
        var skipped = settings.TryGetValue("last_skipped", out var rawSkipped) &&
                      int.TryParse(rawSkipped, CultureInfo.InvariantCulture, out var parsedSkipped)
            ? parsedSkipped
            : 0;
        return new MailIndexStatus(
            settings.GetValueOrDefault("enabled") == "true",
            count,
            lastSync,
            settings.GetValueOrDefault("last_error"),
            skipped,
            _databasePath);
    }

    private static async Task<bool> ReadEnabledAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='enabled';";
        return string.Equals(
            Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture),
            "true",
            StringComparison.Ordinal);
    }

    private async Task<T> InGateAsync<T>(
        Func<SqliteConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            return await action(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task InGateAsync(
        Func<SqliteConnection, Task> action,
        CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await action(connection);
            return true;
        }, cancellationToken);

    private static bool IsValid(MailMessageSnapshot message) =>
        !string.IsNullOrWhiteSpace(message.Identity.StoreId) &&
        !string.IsNullOrWhiteSpace(message.Identity.EntryId);

    private static string BuildSnippet(string body, string query)
    {
        var compact = string.Join(' ', body.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (compact.Length <= 220)
        {
            return compact;
        }

        var index = compact.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            index = 0;
        }

        var start = Math.Max(0, index - 70);
        var length = Math.Min(220, compact.Length - start);
        return (start > 0 ? "…" : string.Empty) + compact.Substring(start, length) +
               (start + length < compact.Length ? "…" : string.Empty);
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    public async ValueTask DisposeAsync()
    {
        if (_initializationTask is not null)
        {
            try
            {
                await _initializationTask;
            }
            catch
            {
            }
        }

        _gate.Dispose();
    }
}
