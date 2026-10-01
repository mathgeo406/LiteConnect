using System.Text;
using Microsoft.Data.Sqlite;

namespace LiteConnect.Core.Data;

/// <summary>
/// Centralizes SQLCipher connection opening, <c>PRAGMA key</c> application,
/// <c>PRAGMA rekey</c> rotation, and plaintext-to-SQLCipher migration.
/// </summary>
public static class SqlCipherConnectionHelper
{
    private static readonly byte[] SqlitePlaintextHeader =
        Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>
    /// Returns <see langword="true"/> if the file at <paramref name="dbPath"/> exists
    /// and starts with the unencrypted SQLite header (<c>"SQLite format 3\0"</c>).
    /// </summary>
    public static bool IsPlaintextSqliteFile(string dbPath)
    {
        if (!File.Exists(dbPath))
            return false;

        try
        {
            Span<byte> header = stackalloc byte[16];
            using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Read(header) < 16)
                return false;

            return header.SequenceEqual(SqlitePlaintextHeader);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a <see cref="SqliteConnection"/> to <paramref name="dbPath"/> and immediately
    /// executes <c>PRAGMA key</c> if <paramref name="sqlCipherKey"/> is provided.
    /// If the existing file is an unencrypted SQLite database from an earlier sprint,
    /// it is transparently migrated to an encrypted SQLCipher database first.
    /// </summary>
    public static async Task<SqliteConnection> OpenEncryptedConnectionAsync(
        string dbPath,
        string? sqlCipherKey,
        CancellationToken ct = default)
    {
        SQLitePCL.Batteries_V2.Init();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // If we have a key and the existing DB file is unencrypted, migrate it in-place
        if (!string.IsNullOrEmpty(sqlCipherKey) && IsPlaintextSqliteFile(dbPath))
        {
            await MigratePlaintextToSqlCipherAsync(dbPath, sqlCipherKey, ct);
        }

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = false // Disable pooling so Lock() deterministically closes the file & clears keys
        }.ToString();

        var connection = new SqliteConnection(cs);
        try
        {
            await connection.OpenAsync(ct);

            if (!string.IsNullOrEmpty(sqlCipherKey))
            {
                await ApplyPragmaKeyAsync(connection, sqlCipherKey, ct);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Executes <c>PRAGMA key = '...'</c> as the first statement on an open connection.
    /// </summary>
    public static async Task ApplyPragmaKeyAsync(
        SqliteConnection connection,
        string sqlCipherKey,
        CancellationToken ct = default)
    {
        string escaped = EscapeSqlLiteral(sqlCipherKey);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA key = '{escaped}';";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Re-encrypts an already-open SQLCipher database with <paramref name="newKey"/>
    /// using <c>PRAGMA rekey</c>.
    /// </summary>
    public static async Task RekeyAsync(
        SqliteConnection connection,
        string newKey,
        CancellationToken ct = default)
    {
        string escaped = EscapeSqlLiteral(newKey);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA rekey = '{escaped}';";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Verifies that the currently applied <c>PRAGMA key</c> can read the database schema.
    /// Returns <see langword="false"/> if the key is wrong (<c>SQLITE_NOTADB</c>).
    /// </summary>
    public static async Task<bool> VerifyDatabaseReadableAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// Migrates an unencrypted SQLite database to an encrypted SQLCipher database using <c>sqlcipher_export</c>.
    /// </summary>
    private static async Task MigratePlaintextToSqlCipherAsync(
        string dbPath,
        string sqlCipherKey,
        CancellationToken ct)
    {
        string tempEncryptedPath = dbPath + ".enc_tmp";
        if (File.Exists(tempEncryptedPath))
            File.Delete(tempEncryptedPath);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWrite,
            Pooling    = false
        }.ToString();

        await using (var plainConn = new SqliteConnection(cs))
        {
            await plainConn.OpenAsync(ct);

            string escapedPath = EscapeSqlLiteral(tempEncryptedPath);
            string escapedKey  = EscapeSqlLiteral(sqlCipherKey);

            await using var cmd = plainConn.CreateCommand();
            cmd.CommandText = $"""
                ATTACH DATABASE '{escapedPath}' AS encrypted KEY '{escapedKey}';
                SELECT sqlcipher_export('encrypted');
                DETACH DATABASE encrypted;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        File.Move(tempEncryptedPath, dbPath, overwrite: true);
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''");
}
