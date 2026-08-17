using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AIEverything.Desktop.Ranking;

public sealed class SqliteRankingBehaviorStore : IRankingBehaviorStore, IAsyncDisposable
{
    private const int SchemaVersion = 2;
    private const int RetentionDays = 30;
    private const int SaltLength = 32;
    private const double HalfLifeDays = 30;
    private const string LegacyTableName = "behavior_daily_legacy_v1";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS behavior_meta (
            id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
            salt BLOB NOT NULL CHECK(length(salt) = 32)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS behavior_daily (
            day_utc INTEGER NOT NULL,
            file_key BLOB NOT NULL,
            directory_key BLOB NOT NULL,
            extension TEXT NOT NULL,
            weight_sum REAL NOT NULL,
            event_count INTEGER NOT NULL,
            PRIMARY KEY(day_utc, file_key, directory_key, extension)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS ix_behavior_daily_file_day
            ON behavior_daily(file_key, day_utc);
        CREATE INDEX IF NOT EXISTS ix_behavior_daily_directory_day
            ON behavior_daily(directory_key, day_utc);
        CREATE INDEX IF NOT EXISTS ix_behavior_daily_extension_day
            ON behavior_daily(extension, day_utc);
        """;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _salt;
    private bool _initialized;
    private bool _disposed;

    public SqliteRankingBehaviorStore(string databasePath, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("Ranking database path must be absolute.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<IReadOnlyDictionary<string, BehaviorAffinity>> ReadAsync(
        IReadOnlyList<RankingIdentity> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return EmptyAffinities();
        }

        return await InGateAsync(async connection =>
        {
            var today = UtcDay(now);
            var cutoff = today - (RetentionDays - 1);
            await PruneAsync(connection, cutoff, cancellationToken);
            var keys = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.FullPath))
                .Select(candidate => CreateCandidateKey(candidate, GetSalt()))
                .ToArray();
            if (keys.Length == 0)
            {
                return EmptyAffinities();
            }

            var fileKeys = keys.Select(key => key.FileKey).Distinct(StringComparer.Ordinal).ToArray();
            var directoryKeys = keys.Select(key => key.DirectoryKey).Distinct(StringComparer.Ordinal).ToArray();
            var extensions = keys.Select(key => key.Extension).Distinct(StringComparer.Ordinal).ToArray();
            await using var command = connection.CreateCommand();
            var fileParameters = AddBlobParameters(command, "$file", fileKeys);
            var directoryParameters = AddBlobParameters(command, "$directory", directoryKeys);
            var extensionParameters = AddTextParameters(command, "$extension", extensions);
            command.CommandText = $"""
                SELECT day_utc, file_key, directory_key, extension, weight_sum
                FROM behavior_daily
                WHERE day_utc >= $cutoff
                  AND (file_key IN ({string.Join(',', fileParameters)})
                       OR directory_key IN ({string.Join(',', directoryParameters)})
                       OR extension IN ({string.Join(',', extensionParameters)}));
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);

            var fileScores = new Dictionary<string, double>(StringComparer.Ordinal);
            var directoryScores = new Dictionary<string, double>(StringComparer.Ordinal);
            var extensionScores = new Dictionary<string, double>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var ageDays = Math.Max(0, today - reader.GetInt32(0));
                var decayedWeight = reader.GetDouble(4) * Math.Pow(2, -ageDays / HalfLifeDays);
                AddScore(fileScores, Convert.ToHexString(reader.GetFieldValue<byte[]>(1)), decayedWeight);
                AddScore(directoryScores, Convert.ToHexString(reader.GetFieldValue<byte[]>(2)), decayedWeight);
                AddScore(extensionScores, reader.GetString(3), decayedWeight);
            }

            var result = new Dictionary<string, BehaviorAffinity>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                var score = GetScore(fileScores, key.FileKey) +
                            0.30 * GetScore(directoryScores, key.DirectoryKey) +
                            0.10 * GetScore(extensionScores, key.Extension);
                if (score > 0)
                {
                    result[key.Identity.FullPath] = new BehaviorAffinity(score, "最近常用");
                }
            }

            return (IReadOnlyDictionary<string, BehaviorAffinity>)result;
        }, cancellationToken);
    }

    public async ValueTask RecordAsync(
        RankingFeedback feedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (feedback.Mode == DesktopSearchMode.Content)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(feedback.FullPath) ||
            !Enum.IsDefined(feedback.Mode) ||
            !Enum.IsDefined(feedback.Action) ||
            feedback.BaselineRank < 1 ||
            feedback.PresentedRank < 1)
        {
            throw new ArgumentException("Ranking feedback is invalid.", nameof(feedback));
        }

        var weight = ActionWeight(feedback);
        if (weight <= 0)
        {
            return;
        }

        await InGateAsync(async connection =>
        {
            var today = UtcDay(_timeProvider.GetUtcNow());
            await PruneAsync(connection, today - (RetentionDays - 1), cancellationToken);
            var key = CreateCandidateKey(
                new RankingIdentity(feedback.FullPath, feedback.Extension), GetSalt());
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO behavior_daily(
                    day_utc, file_key, directory_key, extension, weight_sum, event_count)
                VALUES($day, $file, $directory, $extension, $weight, 1)
                ON CONFLICT(day_utc, file_key, directory_key, extension) DO UPDATE SET
                    weight_sum = behavior_daily.weight_sum + excluded.weight_sum,
                    event_count = behavior_daily.event_count + 1;
                """;
            command.Parameters.AddWithValue("$day", today);
            command.Parameters.AddWithValue("$file", Convert.FromHexString(key.FileKey));
            command.Parameters.AddWithValue("$directory", Convert.FromHexString(key.DirectoryKey));
            command.Parameters.AddWithValue("$extension", key.Extension);
            command.Parameters.AddWithValue("$weight", weight);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await InGateAsync(async connection =>
        {
            var nextSalt = RandomNumberGenerator.GetBytes(SaltLength);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM behavior_daily;
                UPDATE behavior_meta SET salt = $salt WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$salt", nextSalt);
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (await TableExistsAsync(connection, LegacyTableName, cancellationToken))
            {
                await using var clearLegacy = connection.CreateCommand();
                clearLegacy.Transaction = (SqliteTransaction)transaction;
                clearLegacy.CommandText = $"DELETE FROM {LegacyTableName};";
                await clearLegacy.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            if (_salt is not null)
            {
                CryptographicOperations.ZeroMemory(_salt);
            }
            _salt = nextSalt;
        }, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
            if (_salt is not null)
            {
                CryptographicOperations.ZeroMemory(_salt);
                _salt = null;
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task<T> InGateAsync<T>(
        Func<SqliteConnection, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            if (!_initialized)
            {
                await InitializeAsync(connection, cancellationToken);
                _initialized = true;
            }

            return await operation(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InGateAsync(
        Func<SqliteConnection, Task> operation,
        CancellationToken cancellationToken) =>
        await InGateAsync(async connection =>
        {
            await operation(connection);
            return true;
        }, cancellationToken);

    private async Task InitializeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version > SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported ranking database schema version {version}.");
        }

        if (await IsLegacyBehaviorTableAsync(connection, cancellationToken))
        {
            if (await TableExistsAsync(connection, LegacyTableName, cancellationToken))
            {
                throw new InvalidDataException("A preserved legacy ranking table already exists.");
            }

            await using var preserve = connection.CreateCommand();
            preserve.CommandText = $"ALTER TABLE behavior_daily RENAME TO {LegacyTableName};";
            await preserve.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = SchemaSql;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var setVersion = connection.CreateCommand())
        {
            setVersion.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            await setVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        var newSalt = RandomNumberGenerator.GetBytes(SaltLength);
        await using (var insertSalt = connection.CreateCommand())
        {
            insertSalt.CommandText = "INSERT OR IGNORE INTO behavior_meta(id, salt) VALUES(1, $salt);";
            insertSalt.Parameters.AddWithValue("$salt", newSalt);
            await insertSalt.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var readSalt = connection.CreateCommand();
        readSalt.CommandText = "SELECT salt FROM behavior_meta WHERE id = 1;";
        var storedSalt = (byte[]?)await readSalt.ExecuteScalarAsync(cancellationToken);
        if (storedSalt is not { Length: SaltLength })
        {
            throw new InvalidDataException("Ranking behavior salt is missing or invalid.");
        }

        _salt = storedSalt;
        CryptographicOperations.ZeroMemory(newSalt);
    }

    private static async Task<bool> IsLegacyBehaviorTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "behavior_daily", cancellationToken))
        {
            return false;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('behavior_daily');";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns.Contains("path_hash") && !columns.Contains("file_key");
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PruneAsync(
        SqliteConnection connection,
        int cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM behavior_daily WHERE day_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private byte[] GetSalt() => _salt ?? throw new InvalidOperationException(
        "Ranking behavior store has not been initialized.");

    private static CandidateKey CreateCandidateKey(RankingIdentity identity, byte[] salt)
    {
        var normalizedPath = NormalizePath(identity.FullPath);
        var directory = Path.GetDirectoryName(normalizedPath) ?? string.Empty;
        return new CandidateKey(
            identity,
            Convert.ToHexString(HashKey(salt, "file", normalizedPath)),
            Convert.ToHexString(HashKey(salt, "directory", directory)),
            NormalizeExtension(identity.Extension));
    }

    private static byte[] HashKey(byte[] salt, string kind, string value)
    {
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"{kind}\0{value}"));
    }

    private static string NormalizePath(string fullPath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            normalized = fullPath;
        }

        return normalized
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToUpperInvariant();
    }

    private static IReadOnlyList<string> AddBlobParameters(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<string> values)
    {
        var parameters = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var name = $"{prefix}{index}";
            command.Parameters.AddWithValue(name, Convert.FromHexString(values[index]));
            parameters[index] = name;
        }

        return parameters;
    }

    private static IReadOnlyList<string> AddTextParameters(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<string> values)
    {
        var parameters = new string[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var name = $"{prefix}{index}";
            command.Parameters.AddWithValue(name, values[index]);
            parameters[index] = name;
        }

        return parameters;
    }

    private static void AddScore(IDictionary<string, double> scores, string key, double value) =>
        scores[key] = (scores.TryGetValue(key, out var current) ? current : 0) + value;

    private static double GetScore(IReadOnlyDictionary<string, double> scores, string key) =>
        scores.TryGetValue(key, out var value) ? value : 0;

    private static IReadOnlyDictionary<string, BehaviorAffinity> EmptyAffinities() =>
        new Dictionary<string, BehaviorAffinity>(StringComparer.OrdinalIgnoreCase);

    private static double ActionWeight(RankingFeedback feedback) => feedback.Action switch
    {
        RankingActionType.Open => 1 + (feedback.PreviewedBeforeAction ? 0.25 : 0),
        RankingActionType.CopyReference => 1 + (feedback.PreviewedBeforeAction ? 0.25 : 0),
        RankingActionType.Locate => 0.5,
        RankingActionType.PreviewConfirmed => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(feedback))
    };

    private static int UtcDay(DateTimeOffset value) =>
        DateOnly.FromDateTime(value.UtcDateTime).DayNumber;

    private static string NormalizeExtension(string extension) =>
        (extension ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();

    private sealed record CandidateKey(
        RankingIdentity Identity,
        string FileKey,
        string DirectoryKey,
        string Extension);
}
