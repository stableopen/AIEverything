namespace AIEverything.Content.Storage;

internal static class ContentSchema
{
    internal const string Version = "21";
    internal const string PolicyVersion = "machine-docx-blocks-v1";

    internal const string Sql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS documents (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            full_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
            name TEXT NOT NULL,
            extension TEXT NOT NULL COLLATE NOCASE,
            size INTEGER NOT NULL,
            modified_at INTEGER NOT NULL,
            fingerprint TEXT NOT NULL,
            content TEXT NOT NULL,
            location_map TEXT NULL,
            indexed_at INTEGER NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS content_fts USING fts5(
            title_tokens,
            body_tokens,
            tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TABLE IF NOT EXISTS candidate_scans (
            scan_id TEXT PRIMARY KEY,
            state TEXT NOT NULL,
            started_at INTEGER NOT NULL,
            completed_at INTEGER NULL,
            error TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS scan_candidates (
            scan_id TEXT NOT NULL REFERENCES candidate_scans(scan_id) ON DELETE CASCADE,
            full_path TEXT NOT NULL COLLATE NOCASE,
            fingerprint TEXT NOT NULL,
            PRIMARY KEY(scan_id, full_path)
        );

        CREATE TABLE IF NOT EXISTS index_queue (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            full_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
            name TEXT NOT NULL,
            extension TEXT NOT NULL COLLATE NOCASE,
            size INTEGER NOT NULL,
            modified_at INTEGER NOT NULL,
            fingerprint TEXT NOT NULL,
            priority INTEGER NOT NULL,
            max_bytes INTEGER NOT NULL,
            max_characters INTEGER NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            next_attempt_at INTEGER NULL,
            state TEXT NOT NULL DEFAULT 'pending'
        );

        CREATE TABLE IF NOT EXISTS index_failures (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            full_path TEXT NOT NULL COLLATE NOCASE,
            fingerprint TEXT NOT NULL,
            error_code TEXT NOT NULL,
            message TEXT NOT NULL,
            attempts INTEGER NOT NULL,
            failed_at INTEGER NOT NULL,
            UNIQUE(full_path, fingerprint)
        );

        CREATE TRIGGER IF NOT EXISTS documents_after_delete
        AFTER DELETE ON documents
        BEGIN
            DELETE FROM content_fts WHERE rowid = OLD.id;
        END;

        CREATE INDEX IF NOT EXISTS ix_documents_modified ON documents(modified_at);
        CREATE INDEX IF NOT EXISTS ix_queue_due ON index_queue(state, next_attempt_at, priority, id);
        CREATE INDEX IF NOT EXISTS ix_failures_path ON index_failures(full_path);
        CREATE INDEX IF NOT EXISTS ix_scan_candidates_path ON scan_candidates(full_path);

        INSERT OR REPLACE INTO settings(key, value) VALUES ('schema_version', '21');
        INSERT OR REPLACE INTO settings(key, value) VALUES ('policy_version', 'machine-docx-blocks-v1');
        INSERT OR IGNORE INTO settings(key, value) VALUES ('paused', 'false');
        INSERT OR IGNORE INTO settings(key, value) VALUES ('enabled', 'false');
        INSERT OR IGNORE INTO settings(key, value) VALUES ('disclosure_accepted', 'false');
        INSERT OR IGNORE INTO settings(key, value) VALUES ('sync_state', 'waiting_for_disclosure');
        UPDATE index_queue SET state = 'pending' WHERE state = 'processing';
        """;

    internal const string DropLegacySql = """
        DROP TRIGGER IF EXISTS documents_after_delete;
        DROP TABLE IF EXISTS content_fts;
        DROP TABLE IF EXISTS index_failures;
        DROP TABLE IF EXISTS index_queue;
        DROP TABLE IF EXISTS documents;
        DROP TABLE IF EXISTS roots;
        DROP TABLE IF EXISTS scan_candidates;
        DROP TABLE IF EXISTS candidate_scans;
        DROP TABLE IF EXISTS settings;
        """;
}
