using System.Globalization;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Services;
using Microsoft.Data.Sqlite;

namespace LiteConnect.Core.Data;

/// <summary>
/// SQLCipher-backed implementation of <see cref="IKnownHostsStore"/> using the <c>KnownHosts</c> table.
/// </summary>
public sealed class SqliteKnownHostsStore : IKnownHostsStore
{
    private readonly IAppEnvironment _environment;
    private readonly ICredentialStore? _credentialStore;

    public SqliteKnownHostsStore(IAppEnvironment environment, ICredentialStore? credentialStore = null)
    {
        _environment     = environment ?? throw new ArgumentNullException(nameof(environment));
        _credentialStore = credentialStore;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        string? keyHex = (_credentialStore as SqlCipherCredentialStore)?.GetSqlCipherKeyHex();
        return await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath,
            keyHex,
            ct);
    }

    /// <inheritdoc />
    public async Task<KnownHostEntry?> GetKnownHostAsync(
        string hostname,
        int port,
        string keyType,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT Hostname, Port, KeyType, Fingerprint, FirstSeen, LastSeen, Trusted
            FROM   KnownHosts
            WHERE  Hostname = @host AND Port = @port AND KeyType = @keyType
            LIMIT  1;
            """;
        cmd.Parameters.AddWithValue("@host",    hostname.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@port",    port);
        cmd.Parameters.AddWithValue("@keyType", keyType);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        string host      = reader.GetString(0);
        int p            = reader.GetInt32(1);
        string kt        = reader.GetString(2);
        string fp        = reader.GetString(3);
        string firstStr  = reader.GetString(4);
        string lastStr   = reader.GetString(5);
        bool trusted     = reader.GetInt64(6) != 0;

        var firstSeen = DateTimeOffset.TryParse(firstStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fs)
            ? fs
            : DateTimeOffset.UtcNow;
        var lastSeen = DateTimeOffset.TryParse(lastStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ls)
            ? ls
            : DateTimeOffset.UtcNow;

        return new KnownHostEntry(host, p, kt, fp, firstSeen, lastSeen, trusted);
    }

    /// <inheritdoc />
    public async Task SaveKnownHostAsync(
        string hostname,
        int port,
        string keyType,
        string fingerprint,
        bool trusted = true,
        CancellationToken ct = default)
    {
        string normalizedHost = hostname.Trim().ToLowerInvariant();
        string now            = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var existing = await GetKnownHostAsync(normalizedHost, port, keyType, ct);
        string firstSeen = existing?.FirstSeen.ToString("O", CultureInfo.InvariantCulture) ?? now;

        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO KnownHosts (Hostname, Port, KeyType, Fingerprint, FirstSeen, LastSeen, Trusted)
            VALUES (@host, @port, @keyType, @fp, @firstSeen, @lastSeen, @trusted)
            ON CONFLICT(Hostname, Port, KeyType) DO UPDATE SET
                Fingerprint = excluded.Fingerprint,
                LastSeen    = excluded.LastSeen,
                Trusted     = excluded.Trusted;
            """;
        cmd.Parameters.AddWithValue("@host",      normalizedHost);
        cmd.Parameters.AddWithValue("@port",      port);
        cmd.Parameters.AddWithValue("@keyType",   keyType);
        cmd.Parameters.AddWithValue("@fp",        fingerprint);
        cmd.Parameters.AddWithValue("@firstSeen", firstSeen);
        cmd.Parameters.AddWithValue("@lastSeen",  now);
        cmd.Parameters.AddWithValue("@trusted",   trusted ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task RemoveKnownHostAsync(
        string hostname,
        int port,
        string keyType,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            DELETE FROM KnownHosts
            WHERE Hostname = @host AND Port = @port AND KeyType = @keyType;
            """;
        cmd.Parameters.AddWithValue("@host",    hostname.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@port",    port);
        cmd.Parameters.AddWithValue("@keyType", keyType);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
