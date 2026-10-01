using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Core.Serialization;
using LiteConnect.Core.Services;
using Microsoft.Data.Sqlite;

namespace LiteConnect.Core.Data;

/// <summary>
/// SQLite-backed implementation of <see cref="IConnectionRepository"/>.
/// Uses a lazily-opened, long-lived <see cref="SqliteConnection"/> for the
/// lifetime of this instance. Automatically applies the active SQLCipher key
/// and closes the connection when the credential store is locked or re-keyed.
/// </summary>
public sealed class SqliteConnectionRepository
    : IConnectionRepository, IAsyncDisposable, IDisposable
{
    private readonly IAppEnvironment _environment;
    private readonly ICredentialStore? _credentialStore;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteConnectionRepository(
        IAppEnvironment environment,
        ICredentialStore? credentialStore = null)
    {
        _environment     = environment ?? throw new ArgumentNullException(nameof(environment));
        _credentialStore = credentialStore;

        if (_credentialStore is SqlCipherCredentialStore sqlCipherStore)
        {
            sqlCipherStore.Locked      += OnStoreLockedOrRekeying;
            sqlCipherStore.BeforeRekey += OnStoreLockedOrRekeying;
        }
    }

    private void OnStoreLockedOrRekeying(object? sender, EventArgs e)
    {
        CloseConnection();
    }

    /// <summary>
    /// Closes the underlying SQLCipher connection immediately (e.g. on lock or before rekey).
    /// </summary>
    public void CloseConnection()
    {
        if (_connection is not null)
        {
            try { _connection.Close(); } catch { }
            _connection.Dispose();
            _connection = null;
        }
    }

    // -------------------------------------------------------------------------
    // Connection management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Lazily opens the underlying SQLite/SQLCipher connection the first time it is needed.
    /// </summary>
    private async Task<SqliteConnection> EnsureOpenAsync(CancellationToken ct = default)
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
            return _connection;

        string? keyHex = (_credentialStore as SqlCipherCredentialStore)?.GetSqlCipherKeyHex();
        _connection = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath,
            keyHex,
            ct);

        return _connection;
    }

    // -------------------------------------------------------------------------
    // IConnectionRepository — LoadTreeAsync
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<ConnectionGroup> LoadTreeAsync(CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);

        // ── Load all groups ──────────────────────────────────────────────────
        var groupMap = new Dictionary<Guid, ConnectionGroup>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, Name, ParentId, SortOrder, IsExpanded,
                       DefaultUsername, DefaultDomain, DefaultProtocol,
                       CreatedAt, UpdatedAt
                FROM   ConnectionGroups
                ORDER BY SortOrder;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var g = MapGroupRow(reader);
                groupMap[g.Id] = g;
            }
        }

        // ── Load all connections ─────────────────────────────────────────────
        var connectionsByGroup = new Dictionary<Guid, List<ConnectionInfo>>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, GroupId, Name, Protocol, Hostname, Port, SortOrder,
                       Username, Password, Domain, PrivateKeyPath, KeyPassphrase,
                       RdpSettings, SshSettings, VncSettings, ProxySettings,
                       AutoRecord, RecordingFormat, RecordingDirectory, RecordingFilePattern,
                       LastConnected, TimesConnected, Notes, CreatedAt, UpdatedAt
                FROM   Connections
                ORDER BY SortOrder;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var c = MapConnectionRow(reader);
                if (!connectionsByGroup.TryGetValue(c.GroupId, out var list))
                {
                    list = [];
                    connectionsByGroup[c.GroupId] = list;
                }
                list.Add(c);
            }
        }

        // ── Build the tree in memory ─────────────────────────────────────────
        // Synthetic root — never persisted.
        var root = new ConnectionGroup
        {
            Id = Guid.Empty,
            Name = "Root",
            ParentId = null,
        };

        foreach (var group in groupMap.Values)
        {
            // Attach connections to their group.
            if (connectionsByGroup.TryGetValue(group.Id, out var conns))
                group.Connections.AddRange(conns);

            if (group.ParentId is { } parentId && groupMap.TryGetValue(parentId, out var parent))
                parent.Groups.Add(group);
            else
                root.Groups.Add(group);
        }

        // Sort children (already sorted by DB ORDER BY, but keep defensive sort).
        SortTree(root);

        return root;
    }

    // -------------------------------------------------------------------------
    // IConnectionRepository — Save
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task SaveConnectionAsync(ConnectionInfo connection, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            INSERT OR REPLACE INTO Connections (
                Id, GroupId, Name, Protocol, Hostname, Port, SortOrder,
                Username, Password, Domain, PrivateKeyPath, KeyPassphrase,
                RdpSettings, SshSettings, VncSettings, ProxySettings,
                AutoRecord, RecordingFormat, RecordingDirectory, RecordingFilePattern,
                LastConnected, TimesConnected, Notes, CreatedAt, UpdatedAt)
            VALUES (
                @id, @groupId, @name, @protocol, @hostname, @port, @sortOrder,
                @username, @password, @domain, @privateKeyPath, @keyPassphrase,
                @rdpSettings, @sshSettings, @vncSettings, @proxySettings,
                @autoRecord, @recordingFormat, @recordingDirectory, @recordingFilePattern,
                @lastConnected, @timesConnected, @notes, @createdAt, @updatedAt);
            """;

        cmd.Parameters.AddWithValue("@id",                   connection.Id.ToString());
        cmd.Parameters.AddWithValue("@groupId",              connection.GroupId.ToString());
        cmd.Parameters.AddWithValue("@name",                 connection.Name);
        cmd.Parameters.AddWithValue("@protocol",             connection.Protocol.ToString());
        cmd.Parameters.AddWithValue("@hostname",             connection.Hostname);
        cmd.Parameters.AddWithValue("@port",                 (object?)connection.Port ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sortOrder",            connection.SortOrder);
        cmd.Parameters.AddWithValue("@username",             (object?)connection.Username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@password",             (object?)connection.Password ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@domain",               (object?)connection.Domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@privateKeyPath",       (object?)connection.PrivateKeyPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@keyPassphrase",        (object?)connection.KeyPassphrase ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rdpSettings",          (object?)ConnectionSettingsSerializer.Serialize(connection.RdpSettings) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sshSettings",          (object?)ConnectionSettingsSerializer.Serialize(connection.SshSettings) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vncSettings",          (object?)ConnectionSettingsSerializer.Serialize(connection.VncSettings) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@proxySettings",        (object?)ConnectionSettingsSerializer.Serialize(connection.ProxySettings) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@autoRecord",           connection.AutoRecord ? 1 : 0);
        cmd.Parameters.AddWithValue("@recordingFormat",      connection.RecordingFormat.ToString());
        cmd.Parameters.AddWithValue("@recordingDirectory",   (object?)connection.RecordingDirectory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recordingFilePattern", (object?)connection.RecordingFilePattern ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastConnected",        (object?)connection.LastConnected?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@timesConnected",       connection.TimesConnected);
        cmd.Parameters.AddWithValue("@notes",                (object?)connection.Notes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt",            connection.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt",            connection.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SaveGroupAsync(ConnectionGroup group, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            INSERT OR REPLACE INTO ConnectionGroups (
                Id, Name, ParentId, SortOrder, IsExpanded,
                DefaultUsername, DefaultDomain, DefaultProtocol,
                CreatedAt, UpdatedAt)
            VALUES (
                @id, @name, @parentId, @sortOrder, @isExpanded,
                @defaultUsername, @defaultDomain, @defaultProtocol,
                @createdAt, @updatedAt);
            """;

        cmd.Parameters.AddWithValue("@id",              group.Id.ToString());
        cmd.Parameters.AddWithValue("@name",            group.Name);
        cmd.Parameters.AddWithValue("@parentId",        (object?)group.ParentId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sortOrder",       group.SortOrder);
        cmd.Parameters.AddWithValue("@isExpanded",      group.IsExpanded ? 1 : 0);
        cmd.Parameters.AddWithValue("@defaultUsername", (object?)group.DefaultUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultDomain",   (object?)group.DefaultDomain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@defaultProtocol", (object?)group.DefaultProtocol?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt",       group.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt",       group.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IConnectionRepository — Delete
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task DeleteConnectionAsync(Guid id, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Connections WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteGroupAsync(Guid id, bool recursive = false, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);

        if (!recursive)
        {
            // Reparent immediate children to their grandparent before deleting.
            await using var getParentCmd = conn.CreateCommand();
            getParentCmd.CommandText = "SELECT ParentId FROM ConnectionGroups WHERE Id = @id;";
            getParentCmd.Parameters.AddWithValue("@id", id.ToString());
            var parentIdObj = await getParentCmd.ExecuteScalarAsync(ct);
            var parentId = parentIdObj is string s ? s : null;

            // Move child groups to grandparent.
            await using var moveGroupsCmd = conn.CreateCommand();
            moveGroupsCmd.CommandText = "UPDATE ConnectionGroups SET ParentId = @parentId WHERE ParentId = @id;";
            moveGroupsCmd.Parameters.AddWithValue("@parentId", (object?)parentId ?? DBNull.Value);
            moveGroupsCmd.Parameters.AddWithValue("@id", id.ToString());
            await moveGroupsCmd.ExecuteNonQueryAsync(ct);

            // Move connections to grandparent group — if no grandparent the
            // caller is responsible for ensuring a valid target exists; here
            // we simply set GroupId to the grandparent (or keep them orphaned
            // until the caller cleans up).
            if (parentId is not null)
            {
                await using var moveConnsCmd = conn.CreateCommand();
                moveConnsCmd.CommandText = "UPDATE Connections SET GroupId = @parentId WHERE GroupId = @id;";
                moveConnsCmd.Parameters.AddWithValue("@parentId", parentId);
                moveConnsCmd.Parameters.AddWithValue("@id", id.ToString());
                await moveConnsCmd.ExecuteNonQueryAsync(ct);
            }
        }

        // CASCADE in the schema handles recursive deletion of children when
        // recursive == true (SQLite foreign-key cascades are on by default when
        // PRAGMA foreign_keys = ON, but since we only rely on DELETE CASCADE we
        // can also issue the delete directly — SQLite will cascade).
        await using var delCmd = conn.CreateCommand();
        // Enable FK enforcement for cascade to work.
        delCmd.CommandText = """
            PRAGMA foreign_keys = ON;
            DELETE FROM ConnectionGroups WHERE Id = @id;
            """;
        delCmd.Parameters.AddWithValue("@id", id.ToString());
        await delCmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IConnectionRepository — Move
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task MoveConnectionAsync(
        Guid connectionId, Guid targetGroupId, int sortOrder, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Connections
            SET    GroupId = @groupId, SortOrder = @sortOrder
            WHERE  Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id",        connectionId.ToString());
        cmd.Parameters.AddWithValue("@groupId",   targetGroupId.ToString());
        cmd.Parameters.AddWithValue("@sortOrder", sortOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task MoveGroupAsync(
        Guid groupId, Guid targetParentId, int sortOrder, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE ConnectionGroups
            SET    ParentId = @parentId, SortOrder = @sortOrder
            WHERE  Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id",        groupId.ToString());
        cmd.Parameters.AddWithValue("@parentId",  targetParentId.ToString());
        cmd.Parameters.AddWithValue("@sortOrder", sortOrder);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IConnectionRepository — Search
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConnectionInfo>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        var conn = await EnsureOpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = """
            SELECT Id, GroupId, Name, Protocol, Hostname, Port, SortOrder,
                   Username, Password, Domain, PrivateKeyPath, KeyPassphrase,
                   RdpSettings, SshSettings, VncSettings, ProxySettings,
                   AutoRecord, RecordingFormat, RecordingDirectory, RecordingFilePattern,
                   LastConnected, TimesConnected, Notes, CreatedAt, UpdatedAt
            FROM   Connections
            WHERE  Name     LIKE @q
                OR Hostname LIKE @q
                OR Username LIKE @q
            ORDER BY Name;
            """;

        cmd.Parameters.AddWithValue("@q", $"%{query}%");

        var results = new List<ConnectionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(MapConnectionRow(reader));

        return results;
    }

    // -------------------------------------------------------------------------
    // Private helpers — row mappers
    // -------------------------------------------------------------------------

    private static ConnectionGroup MapGroupRow(SqliteDataReader r)
    {
        var group = new ConnectionGroup
        {
            Id        = Guid.Parse(r.GetString(r.GetOrdinal("Id"))),
            Name      = r.GetString(r.GetOrdinal("Name")),
            SortOrder = r.GetInt32(r.GetOrdinal("SortOrder")),
            IsExpanded = r.GetInt32(r.GetOrdinal("IsExpanded")) != 0,
            CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
            UpdatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("UpdatedAt"))),
        };

        var parentOrd = r.GetOrdinal("ParentId");
        if (!r.IsDBNull(parentOrd))
            group.ParentId = Guid.Parse(r.GetString(parentOrd));

        var usernameOrd = r.GetOrdinal("DefaultUsername");
        if (!r.IsDBNull(usernameOrd))
            group.DefaultUsername = r.GetString(usernameOrd);

        var domainOrd = r.GetOrdinal("DefaultDomain");
        if (!r.IsDBNull(domainOrd))
            group.DefaultDomain = r.GetString(domainOrd);

        var protoOrd = r.GetOrdinal("DefaultProtocol");
        if (!r.IsDBNull(protoOrd) &&
            Enum.TryParse<ProtocolType>(r.GetString(protoOrd), out var proto))
            group.DefaultProtocol = proto;

        return group;
    }

    private static ConnectionInfo MapConnectionRow(SqliteDataReader r)
    {
        var c = new ConnectionInfo
        {
            Id              = Guid.Parse(r.GetString(r.GetOrdinal("Id"))),
            GroupId         = Guid.Parse(r.GetString(r.GetOrdinal("GroupId"))),
            Name            = r.GetString(r.GetOrdinal("Name")),
            Protocol        = Enum.Parse<ProtocolType>(r.GetString(r.GetOrdinal("Protocol"))),
            Hostname        = r.GetString(r.GetOrdinal("Hostname")),
            SortOrder       = r.GetInt32(r.GetOrdinal("SortOrder")),
            AutoRecord      = r.GetInt32(r.GetOrdinal("AutoRecord")) != 0,
            TimesConnected  = r.GetInt32(r.GetOrdinal("TimesConnected")),
            CreatedAt       = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
            UpdatedAt       = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("UpdatedAt"))),
        };

        // Nullable int columns
        var portOrd = r.GetOrdinal("Port");
        if (!r.IsDBNull(portOrd)) c.Port = r.GetInt32(portOrd);

        // Nullable string columns — credential hints
        ReadNullableString(r, "Username",       v => c.Username       = v);
        ReadNullableString(r, "Password",       v => c.Password       = v);
        ReadNullableString(r, "Domain",         v => c.Domain         = v);
        ReadNullableString(r, "PrivateKeyPath", v => c.PrivateKeyPath = v);
        ReadNullableString(r, "KeyPassphrase",  v => c.KeyPassphrase  = v);

        // JSON settings blobs
        ReadNullableString(r, "SshSettings",   v => c.SshSettings   = ConnectionSettingsSerializer.Deserialize<SshConnectionSettings>(v));
        ReadNullableString(r, "RdpSettings",   v => c.RdpSettings   = ConnectionSettingsSerializer.Deserialize<RdpConnectionSettings>(v));
        ReadNullableString(r, "VncSettings",   v => c.VncSettings   = ConnectionSettingsSerializer.Deserialize<VncConnectionSettings>(v));
        ReadNullableString(r, "ProxySettings", v => c.ProxySettings = ConnectionSettingsSerializer.Deserialize<ProxySettings>(v));

        // RecordingFormat
        var rfOrd = r.GetOrdinal("RecordingFormat");
        if (!r.IsDBNull(rfOrd) &&
            Enum.TryParse<RecordingFormat>(r.GetString(rfOrd), out var rf))
            c.RecordingFormat = rf;

        ReadNullableString(r, "RecordingDirectory",   v => c.RecordingDirectory   = v);
        ReadNullableString(r, "RecordingFilePattern", v => c.RecordingFilePattern = v);

        // LastConnected
        var lcOrd = r.GetOrdinal("LastConnected");
        if (!r.IsDBNull(lcOrd))
            c.LastConnected = DateTimeOffset.Parse(r.GetString(lcOrd));

        ReadNullableString(r, "Notes", v => c.Notes = v);

        return c;
    }

    private static void ReadNullableString(
        SqliteDataReader reader, string column, Action<string> setter)
    {
        var ord = reader.GetOrdinal(column);
        if (!reader.IsDBNull(ord))
            setter(reader.GetString(ord));
    }

    // -------------------------------------------------------------------------
    // Private helpers — tree sorting
    // -------------------------------------------------------------------------

    private static void SortTree(ConnectionGroup group)
    {
        group.Groups.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        group.Connections.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        foreach (var child in group.Groups)
            SortTree(child);
    }

    // -------------------------------------------------------------------------
    // IDisposable / IAsyncDisposable
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
