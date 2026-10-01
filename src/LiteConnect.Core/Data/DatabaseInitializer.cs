using LiteConnect.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace LiteConnect.Core.Data;

/// <summary>
/// Creates the SQLite / SQLCipher database schema and seeds default rows
/// on first run. Safe to call on every application start — all DDL uses
/// <c>CREATE TABLE IF NOT EXISTS</c> and seed statements use
/// <c>INSERT OR IGNORE</c>.
/// </summary>
public sealed class DatabaseInitializer(IAppEnvironment environment)
{
    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens the database at <see cref="IAppEnvironment.DatabasePath"/>,
    /// applies the SQLCipher key (if supplied), creates tables, and seeds
    /// default rows.
    /// </summary>
    /// <param name="sqlCipherKey">
    /// Raw hex key string for SQLCipher. Pass <see langword="null"/> or empty to
    /// open as a plain (unencrypted) SQLite file — the default for Sprint 2.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(string? sqlCipherKey = null, CancellationToken ct = default)
    {
        await using var connection = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            environment.DatabasePath,
            sqlCipherKey,
            ct);

        await CreateSchemaAsync(connection, ct);
        await SeedGlobalDefaultsAsync(connection, ct);
        await SeedAppSettingsAsync(connection, ct);
    }

    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    private static async Task CreateSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS ConnectionGroups (
                Id               TEXT PRIMARY KEY,
                Name             TEXT NOT NULL,
                ParentId         TEXT,
                SortOrder        INTEGER DEFAULT 0,
                IsExpanded       INTEGER DEFAULT 1,
                DefaultUsername  TEXT,
                DefaultDomain    TEXT,
                DefaultProtocol  TEXT,
                CreatedAt        TEXT NOT NULL,
                UpdatedAt        TEXT NOT NULL,
                FOREIGN KEY (ParentId) REFERENCES ConnectionGroups(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Connections (
                Id                   TEXT PRIMARY KEY,
                GroupId              TEXT NOT NULL,
                Name                 TEXT NOT NULL,
                Protocol             TEXT NOT NULL,
                Hostname             TEXT NOT NULL,
                Port                 INTEGER,
                SortOrder            INTEGER DEFAULT 0,
                Username             TEXT,
                Password             TEXT,
                Domain               TEXT,
                PrivateKeyPath       TEXT,
                KeyPassphrase        TEXT,
                RdpSettings          TEXT,
                SshSettings          TEXT,
                VncSettings          TEXT,
                ProxySettings        TEXT,
                AutoRecord           INTEGER DEFAULT 0,
                RecordingFormat      TEXT DEFAULT 'PlainText',
                RecordingDirectory   TEXT,
                RecordingFilePattern TEXT,
                LastConnected        TEXT,
                TimesConnected       INTEGER DEFAULT 0,
                Notes                TEXT,
                CreatedAt            TEXT NOT NULL,
                UpdatedAt            TEXT NOT NULL,
                FOREIGN KEY (GroupId) REFERENCES ConnectionGroups(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS GlobalDefaults (
                Protocol  TEXT PRIMARY KEY,
                Settings  TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AppSettings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KnownHosts (
                Hostname    TEXT NOT NULL,
                Port        INTEGER NOT NULL DEFAULT 22,
                KeyType     TEXT NOT NULL,
                Fingerprint TEXT NOT NULL,
                FirstSeen   TEXT NOT NULL,
                LastSeen    TEXT NOT NULL,
                Trusted     INTEGER DEFAULT 1,
                PRIMARY KEY (Hostname, Port, KeyType)
            );

            CREATE INDEX IF NOT EXISTS IX_Connections_GroupId
                ON Connections(GroupId);

            CREATE INDEX IF NOT EXISTS IX_Connections_Hostname
                ON Connections(Hostname);

            CREATE INDEX IF NOT EXISTS IX_ConnectionGroups_ParentId
                ON ConnectionGroups(ParentId);
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Seed data
    // -------------------------------------------------------------------------

    private static async Task SeedGlobalDefaultsAsync(SqliteConnection conn, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        const string sql = """
            INSERT OR IGNORE INTO GlobalDefaults (Protocol, Settings, UpdatedAt)
            VALUES ('SSH', '{}', @now),
                   ('RDP', '{}', @now),
                   ('VNC', '{}', @now);
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SeedAppSettingsAsync(SqliteConnection conn, CancellationToken ct)
    {
        // All rows use INSERT OR IGNORE so existing user customisation is preserved.
        const string sql = """
            INSERT OR IGNORE INTO AppSettings (Key, Value) VALUES
                ('schema_version',         '1'),
                ('encryption_mode',        'dpapi'),
                ('master_password_hash',   ''),
                ('master_password_salt',   ''),
                ('default_log_directory',  'logs/'),
                ('default_log_pattern',    '{connection}_{date}_{time}.log'),
                ('theme',                  'dark'),
                ('auto_reconnect',         'true'),
                ('auto_lock_minutes',      '30');
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
