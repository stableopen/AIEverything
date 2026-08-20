using System.Diagnostics;
using System.Text.Json;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Extraction;
using AIEverything.Content.Text;
using Microsoft.Data.Sqlite;

namespace AIEverything.Content.Storage;

public sealed record DatabaseInitializationResult(bool Migrated, string? BackupPath);
public sealed record CandidateScanCommitResult(int Candidates, int Queued, int Removed);

public sealed class ContentIndexStore : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ContentIndexStore(string databasePath)
    {
        if (!Path.IsPathFullyQualified(databasePath))
            throw new ArgumentException("Database path must be absolute.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
    }

    public async Task<DatabaseInitializationResult> InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var legacy = File.Exists(_databasePath) && !await IsCurrentSchemaAsync(cancellationToken);
        string? backup = null;
        if (legacy)
        {
            backup = BuildBackupPath();
            File.Copy(_databasePath, backup, overwrite: false);
        }

        try
        {
            await InGateAsync(async connection =>
            {
                if (legacy)
                {
                    await using var drop = connection.CreateCommand();
                    drop.CommandText = ContentSchema.DropLegacySql;
                    await drop.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var command = connection.CreateCommand();
                command.CommandText = ContentSchema.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }, cancellationToken);
        }
        catch
        {
            if (legacy && backup is not null)
            {
                SqliteConnection.ClearAllPools();
                File.Copy(backup, _databasePath, overwrite: true);
            }
            throw;
        }

        return new DatabaseInitializationResult(legacy, backup);
    }

    public Task ConfigureAsync(
        bool disclosureAccepted,
        bool enabled,
        CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await SetSettingAsync(connection, (SqliteTransaction)transaction, "disclosure_accepted",
                disclosureAccepted ? "true" : "false", cancellationToken);
            await SetSettingAsync(connection, (SqliteTransaction)transaction, "enabled",
                enabled ? "true" : "false", cancellationToken);
            await SetSettingAsync(connection, (SqliteTransaction)transaction, "sync_state",
                !disclosureAccepted ? "waiting_for_disclosure" : enabled ? "waiting_for_everything" : "filename_only",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    public Task<string> BeginCandidateScanAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            var scanId = Guid.NewGuid().ToString("N");
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO candidate_scans(scan_id,state,started_at) VALUES($id,'staging',$now);";
            command.Parameters.AddWithValue("$id", scanId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken);
            return scanId;
        }, cancellationToken);

    public Task StageCandidateAsync(
        string scanId,
        FileCandidate candidate,
        CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO scan_candidates(scan_id,full_path,fingerprint)
                VALUES($scan,$path,$fingerprint)
                ON CONFLICT(scan_id,full_path) DO UPDATE SET fingerprint=excluded.fingerprint;
                """;
            command.Parameters.AddWithValue("$scan", scanId);
            command.Parameters.AddWithValue("$path", candidate.FullPath);
            command.Parameters.AddWithValue("$fingerprint", candidate.Fingerprint);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<CandidateScanCommitResult> CommitCandidateScanAsync(
        string scanId,
        CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await EnsureStagingScanAsync(connection, (SqliteTransaction)transaction, scanId, cancellationToken);
            var candidates = await ScalarIntAsync(connection, (SqliteTransaction)transaction,
                "SELECT COUNT(*) FROM scan_candidates WHERE scan_id=$scan;", scanId, cancellationToken);
            var queued = await ScalarIntAsync(connection, (SqliteTransaction)transaction,
                """
                SELECT COUNT(*) FROM index_queue q
                JOIN scan_candidates s ON s.full_path=q.full_path AND s.scan_id=$scan;
                """, scanId, cancellationToken);
            var removed = await ScalarIntAsync(connection, (SqliteTransaction)transaction,
                """
                SELECT COUNT(*) FROM documents d
                WHERE NOT EXISTS(SELECT 1 FROM scan_candidates s WHERE s.scan_id=$scan AND s.full_path=d.full_path);
                """, scanId, cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM documents
                WHERE NOT EXISTS(SELECT 1 FROM scan_candidates s WHERE s.scan_id=$scan AND s.full_path=documents.full_path);
                DELETE FROM index_queue
                WHERE NOT EXISTS(SELECT 1 FROM scan_candidates s WHERE s.scan_id=$scan AND s.full_path=index_queue.full_path);
                DELETE FROM index_failures
                WHERE NOT EXISTS(SELECT 1 FROM scan_candidates s WHERE s.scan_id=$scan AND s.full_path=index_failures.full_path);
                UPDATE candidate_scans SET state='committed',completed_at=$now,error=NULL WHERE scan_id=$scan;
                UPDATE settings SET value='ready' WHERE key='sync_state';
                INSERT OR REPLACE INTO settings(key,value) VALUES('last_scan_completed_at',$nowText);
                """;
            command.Parameters.AddWithValue("$scan", scanId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$nowText", now.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CandidateScanCommitResult(candidates, queued, removed);
        }, cancellationToken);

    public Task AbortCandidateScanAsync(
        string scanId,
        string reason,
        CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE candidate_scans SET state='aborted',completed_at=$now,error=$error WHERE scan_id=$scan;
                UPDATE settings SET value='waiting_for_everything' WHERE key='sync_state';
                INSERT OR REPLACE INTO settings(key,value) VALUES('last_sync_error',$error);
                """;
            command.Parameters.AddWithValue("$scan", scanId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$error", Truncate(reason, 500));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    public Task EnqueueAsync(FileCandidate candidate, CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO index_queue(full_path,name,extension,size,modified_at,fingerprint,priority,
                                        max_bytes,max_characters,attempts,next_attempt_at,state)
                SELECT $path,$name,$extension,$size,$modified,$fingerprint,$priority,$maxBytes,$maxCharacters,
                       0,NULL,'pending'
                WHERE NOT EXISTS(SELECT 1 FROM documents WHERE full_path=$path AND fingerprint=$fingerprint)
                  AND NOT EXISTS(SELECT 1 FROM index_failures WHERE full_path=$path AND fingerprint=$fingerprint)
                ON CONFLICT(full_path) DO UPDATE SET
                    name=excluded.name,extension=excluded.extension,size=excluded.size,
                    modified_at=excluded.modified_at,fingerprint=excluded.fingerprint,
                    priority=excluded.priority,max_bytes=excluded.max_bytes,max_characters=excluded.max_characters,
                    attempts=CASE WHEN index_queue.fingerprint=excluded.fingerprint THEN index_queue.attempts ELSE 0 END,
                    next_attempt_at=CASE WHEN index_queue.fingerprint=excluded.fingerprint THEN index_queue.next_attempt_at ELSE NULL END,
                    state='pending';
                """;
            AddCandidateParameters(command, candidate);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<QueueLease?> LeaseNextAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var select = connection.CreateCommand();
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = """
                SELECT id,full_path,name,extension,size,modified_at,fingerprint,attempts,max_bytes,max_characters
                FROM index_queue WHERE state='pending' AND (next_attempt_at IS NULL OR next_attempt_at<=$now)
                ORDER BY priority,id LIMIT 1;
                """;
            select.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            QueueLease? lease = null;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                    lease = new QueueLease(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetInt64(4),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)), reader.GetString(6),
                        reader.GetInt32(7), reader.GetInt64(8), reader.GetInt32(9));
            }
            if (lease is not null)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = "UPDATE index_queue SET state='processing' WHERE id=$id;";
                update.Parameters.AddWithValue("$id", lease.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return lease;
        }, cancellationToken);

    public Task CompleteAsync(QueueLease lease, ExtractionResult extraction, CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var locationMap = SerializeLocationMap(extraction);
            var id = await UpsertDocumentAsync(connection, (SqliteTransaction)transaction, lease, extraction.Text, locationMap, cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM content_fts WHERE rowid=$id;
                INSERT INTO content_fts(rowid,title_tokens,body_tokens) VALUES($id,$title,$body);
                DELETE FROM index_queue WHERE id=$queueId;
                DELETE FROM index_failures WHERE full_path=$path;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", ContentTokenizer.TokenizeForIndex(lease.Name));
            command.Parameters.AddWithValue("$body", ContentTokenizer.TokenizeForIndex(extraction.Text));
            command.Parameters.AddWithValue("$queueId", lease.Id);
            command.Parameters.AddWithValue("$path", lease.FullPath);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    public Task FailAsync(QueueLease lease, string code, string message, DateTimeOffset? retryAt,
        CancellationToken cancellationToken) => InGateAsync(async connection =>
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var attempts = lease.Attempts + 1;
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        if (attempts >= 3 || retryAt is null)
        {
            command.CommandText = """
                INSERT INTO index_failures(full_path,fingerprint,error_code,message,attempts,failed_at)
                VALUES($path,$fingerprint,$code,$message,$attempts,$now)
                ON CONFLICT(full_path,fingerprint) DO UPDATE SET error_code=excluded.error_code,
                    message=excluded.message,attempts=excluded.attempts,failed_at=excluded.failed_at;
                DELETE FROM index_queue WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$fingerprint", lease.Fingerprint);
            command.Parameters.AddWithValue("$code", code);
            command.Parameters.AddWithValue("$message", Truncate(message, 500));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        else
        {
            command.CommandText = "UPDATE index_queue SET attempts=$attempts,next_attempt_at=$next,state='pending' WHERE id=$id;";
            command.Parameters.AddWithValue("$next", retryAt.Value.ToUnixTimeMilliseconds());
        }
        command.Parameters.AddWithValue("$path", lease.FullPath);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$id", lease.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }, cancellationToken);

    public Task RemoveDocumentAsync(string fullPath, CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM documents WHERE full_path=$path; DELETE FROM index_queue WHERE full_path=$path; DELETE FROM index_failures WHERE full_path=$path;";
            command.Parameters.AddWithValue("$path", Path.GetFullPath(fullPath));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<ContentSearchResponse> SearchAsync(ContentSearchRequest request, CancellationToken cancellationToken)
    {
        ValidateSearch(request);
        var stopwatch = Stopwatch.StartNew();
        var result = await InGateAsync(async connection =>
        {
            var match = ContentTokenizer.BuildMatchQuery(request.Query);
            var field = request.Field switch
            {
                ContentSearchField.Title => "title_tokens",
                ContentSearchField.Body => "body_tokens",
                _ => "content_fts"
            };
            var where = $"{field} MATCH $match";
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM content_fts JOIN documents d ON d.id=content_fts.rowid WHERE {where};";
            count.Parameters.AddWithValue("$match", match);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT d.name,d.full_path,d.extension,d.size,d.modified_at,d.content,d.location_map,
                       bm25(content_fts,8.0,1.0) AS rank
                FROM content_fts JOIN documents d ON d.id=content_fts.rowid
                WHERE {where} ORDER BY rank,d.name COLLATE NOCASE LIMIT $limit OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$limit", request.Limit);
            command.Parameters.AddWithValue("$offset", request.Offset);
            var terms = ContentTokenizer.GetQueryTerms(request.Query);
            var items = new List<ContentSearchItem>();
            var missingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var fullPath = reader.GetString(1);
                    if (!File.Exists(fullPath))
                    {
                        missingPaths.Add(fullPath);
                        continue;
                    }
                    var content = reader.GetString(5);
                    var extension = reader.GetString(2);
                    var blocks = reader.IsDBNull(6)
                        ? null
                        : DeserializeLocationMap(content, reader.GetString(6));
                    var locations = request.Field == ContentSearchField.Title
                        ? new[] { new SourceLocationHit(1, 1, string.Empty) }
                        : SourceLocationResolver.Resolve(content, extension, terms, blocks);
                    if (locations.Count == 0) locations = [new SourceLocationHit(1, 1, BuildSnippet(content, terms))];
                    foreach (var location in locations)
                    {
                        items.Add(new ContentSearchItem(reader.GetString(0), fullPath, extension,
                            reader.GetInt64(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                            location.Snippet, -reader.GetDouble(7),
                            request.Field != ContentSearchField.Body && MatchesAny(reader.GetString(0), terms),
                            request.Field != ContentSearchField.Title,
                            location.StartLine, location.EndLine, location.HeadingPath, location.JsonPath,
                            location.LocationLabel));
                    }
                }
            }
            foreach (var missingPath in missingPaths)
            {
                await using var cleanup = connection.CreateCommand();
                cleanup.CommandText = "DELETE FROM documents WHERE full_path=$path; DELETE FROM index_queue WHERE full_path=$path; DELETE FROM index_failures WHERE full_path=$path;";
                cleanup.Parameters.AddWithValue("$path", missingPath);
                await cleanup.ExecuteNonQueryAsync(cancellationToken);
            }
            return (total: Math.Max(0, total - missingPaths.Count),
                Items: (IReadOnlyList<ContentSearchItem>)items);
        }, cancellationToken);
        stopwatch.Stop();
        return new ContentSearchResponse(request.Query, result.total, result.Items.Count, request.Offset,
            request.Limit, stopwatch.Elapsed.TotalMilliseconds, result.Items);
    }

    public Task<ContentIndexStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            var paused = await GetSettingAsync(connection, "paused", cancellationToken) == "true";
            var enabled = await GetSettingAsync(connection, "enabled", cancellationToken) == "true";
            var disclosed = await GetSettingAsync(connection, "disclosure_accepted", cancellationToken) == "true";
            var syncState = await GetSettingAsync(connection, "sync_state", cancellationToken) ?? "unknown";
            var error = await GetSettingAsync(connection, "last_sync_error", cancellationToken);
            var lastSynchronizedAt = await GetSettingAsync(
                connection, "last_scan_completed_at", cancellationToken);
            return new ContentIndexStatus(
                Ready: true, Paused: paused, RootCount: 0,
                IndexedDocuments: await ScalarIntAsync(connection, "SELECT COUNT(*) FROM documents;", cancellationToken),
                QueuedDocuments: await ScalarIntAsync(connection, "SELECT COUNT(*) FROM index_queue;", cancellationToken),
                FailedDocuments: await ScalarIntAsync(connection, "SELECT COUNT(*) FROM index_failures;", cancellationToken),
                LastIndexedAt: await LastIndexedAsync(connection, cancellationToken), DatabasePath: _databasePath,
                Message: error ?? syncState, Enabled: enabled, DisclosureAccepted: disclosed,
                SyncState: syncState, DatabaseBytes: GetDatabaseBytes(),
                CorruptFailures: await CountFailuresAsync(connection, ContentErrorCodes.CorruptDocument, cancellationToken),
                UnsupportedOrEncryptedFailures: await CountFailuresAsync(connection, ContentErrorCodes.UnsupportedOrEncryptedDocument, cancellationToken) +
                    await CountFailuresAsync(connection, ContentErrorCodes.UnsupportedFileType, cancellationToken) +
                    await CountFailuresAsync(connection, ContentErrorCodes.UnsupportedEncoding, cancellationToken),
                TooLargeFailures: await CountFailuresAsync(connection, ContentErrorCodes.FileTooLarge, cancellationToken),
                TimeoutFailures: await CountFailuresAsync(connection, ContentErrorCodes.ExtractionTimeout, cancellationToken),
                AccessDeniedFailures: await CountFailuresAsync(connection, ContentErrorCodes.AccessDenied, cancellationToken),
                LastSynchronizedAt: long.TryParse(
                    lastSynchronizedAt,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var timestamp)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : null);
        }, cancellationToken);

    public Task ClearFailuresAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM index_failures;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<IReadOnlyList<ContentIndexFailure>> ListFailuresAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT full_path,error_code,message,attempts,failed_at FROM index_failures ORDER BY failed_at DESC LIMIT 200;";
            var result = new List<ContentIndexFailure>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new ContentIndexFailure(string.Empty, reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetInt32(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
            return (IReadOnlyList<ContentIndexFailure>)result;
        }, cancellationToken);

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES('paused',$value);";
            command.Parameters.AddWithValue("$value", paused ? "true" : "false");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public async Task<bool> GetPausedAsync(CancellationToken cancellationToken) =>
        await InGateAsync(async connection =>
            string.Equals(await GetSettingAsync(connection, "paused", cancellationToken), "true", StringComparison.Ordinal), cancellationToken);

    public Task<bool> IntegrityCheckAsync(CancellationToken cancellationToken) =>
        InGateAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase);
        }, cancellationToken);

    private async Task<bool> IsCurrentSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key='schema_version';";
            return string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), ContentSchema.Version,
                StringComparison.Ordinal);
        }
        catch (SqliteException) { return false; }
    }

    private string BuildBackupPath()
    {
        var directory = Path.Combine(Path.GetDirectoryName(_databasePath)!, "backups");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"content-v019-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
    }

    private async Task<T> InGateAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { await using var connection = await OpenAsync(token); return await operation(connection); }
        finally { _gate.Release(); }
    }

    private async Task InGateAsync(Func<SqliteConnection, Task> operation, CancellationToken token) =>
        await InGateAsync(async connection => { await operation(connection); return true; }, token);

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(token); await using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; await pragma.ExecuteNonQueryAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task EnsureStagingScanAsync(SqliteConnection connection, SqliteTransaction transaction,
        string scanId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state FROM candidate_scans WHERE scan_id=$scan;";
        command.Parameters.AddWithValue("$scan", scanId);
        if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(token)), "staging", StringComparison.Ordinal))
            throw new InvalidOperationException("Candidate scan is not open for commit.");
    }

    private static async Task<long> UpsertDocumentAsync(SqliteConnection connection, SqliteTransaction transaction,
        QueueLease lease, string content, string? locationMap, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO documents(full_path,name,extension,size,modified_at,fingerprint,content,location_map,indexed_at)
            VALUES($path,$name,$extension,$size,$modified,$fingerprint,$content,$locationMap,$indexed)
            ON CONFLICT(full_path) DO UPDATE SET name=excluded.name,extension=excluded.extension,
                size=excluded.size,modified_at=excluded.modified_at,fingerprint=excluded.fingerprint,
                content=excluded.content,location_map=excluded.location_map,indexed_at=excluded.indexed_at
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$path", lease.FullPath);
        command.Parameters.AddWithValue("$name", lease.Name);
        command.Parameters.AddWithValue("$extension", lease.Extension);
        command.Parameters.AddWithValue("$size", lease.Size);
        command.Parameters.AddWithValue("$modified", lease.ModifiedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$fingerprint", lease.Fingerprint);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$locationMap", (object?)locationMap ?? DBNull.Value);
        command.Parameters.AddWithValue("$indexed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return Convert.ToInt64(await command.ExecuteScalarAsync(token));
    }

    private static string? SerializeLocationMap(ExtractionResult extraction)
    {
        if (extraction.Blocks is not { Count: > 0 })
        {
            return null;
        }

        var offset = 0;
        var entries = new List<LocationMapEntry>(extraction.Blocks.Count);
        foreach (var block in extraction.Blocks)
        {
            var start = extraction.Text.IndexOf(block.Text, offset, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            entries.Add(new LocationMapEntry(
                block.Ordinal, start, block.Text.Length, block.LocationLabel, block.HeadingPath));
            offset = start + block.Text.Length;
        }

        return entries.Count == 0 ? null : JsonSerializer.Serialize(entries);
    }

    private static IReadOnlyList<ExtractedTextBlock>? DeserializeLocationMap(
        string content,
        string json)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<LocationMapEntry[]>(json);
            if (entries is null || entries.Any(entry =>
                    entry.Start < 0 || entry.Length < 0 ||
                    entry.Start > content.Length - entry.Length ||
                    string.IsNullOrWhiteSpace(entry.LocationLabel)))
            {
                return null;
            }

            return entries.Select(entry => new ExtractedTextBlock(
                entry.Ordinal,
                content.Substring(entry.Start, entry.Length),
                entry.LocationLabel,
                entry.HeadingPath)).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LocationMapEntry(
        int Ordinal,
        int Start,
        int Length,
        string LocationLabel,
        string? HeadingPath);

    private static void AddCandidateParameters(SqliteCommand command, FileCandidate value)
    {
        command.Parameters.AddWithValue("$path", value.FullPath);
        command.Parameters.AddWithValue("$name", value.Name);
        command.Parameters.AddWithValue("$extension", value.Extension.TrimStart('.').ToLowerInvariant());
        command.Parameters.AddWithValue("$size", value.Size);
        command.Parameters.AddWithValue("$modified", value.ModifiedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$fingerprint", value.Fingerprint);
        command.Parameters.AddWithValue("$priority", value.Priority);
        command.Parameters.AddWithValue("$maxBytes", value.MaxBytes);
        command.Parameters.AddWithValue("$maxCharacters", value.MaxCharacters);
    }

    private static async Task SetSettingAsync(SqliteConnection connection, SqliteTransaction transaction,
        string key, string value, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO settings(key,value) VALUES($key,$value);";
        command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string?> GetSettingAsync(SqliteConnection connection, string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key); return Convert.ToString(await command.ExecuteScalarAsync(token));
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken token)
    { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt32(await command.ExecuteScalarAsync(token)); }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, string scan, CancellationToken token)
    { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$scan", scan); return Convert.ToInt32(await command.ExecuteScalarAsync(token)); }

    private static async Task<int> CountFailuresAsync(
        SqliteConnection connection,
        string code,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM index_failures WHERE error_code=$code;";
        command.Parameters.AddWithValue("$code", code);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static async Task<DateTimeOffset?> LastIndexedAsync(SqliteConnection connection, CancellationToken token)
    { await using var command = connection.CreateCommand(); command.CommandText = "SELECT MAX(indexed_at) FROM documents;"; var value = await command.ExecuteScalarAsync(token); return value is null or DBNull ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value)); }

    private long GetDatabaseBytes() => new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);

    private static void ValidateSearch(ContentSearchRequest request)
    { ArgumentNullException.ThrowIfNull(request); if (string.IsNullOrWhiteSpace(request.Query) || request.Limit is < 1 or > 100 || request.Offset < 0) throw new ArgumentException("Search request is invalid.", nameof(request)); }
    private static bool MatchesAny(string text, IReadOnlyList<string> terms) => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static string BuildSnippet(string content, IReadOnlyList<string> terms)
    { var index = terms.Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min(); var start = Math.Max(0, index - 80); var length = Math.Min(240, content.Length - start); return content.Substring(start, length).Trim(); }
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    public async ValueTask DisposeAsync() { await Task.CompletedTask; _gate.Dispose(); }
}
