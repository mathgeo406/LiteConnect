using LiteConnect.Core.Models;

namespace LiteConnect.Core.Abstractions;

/// <summary>
/// Persists and retrieves the connection tree (groups + connections).
/// All operations are async; the backing store is SQLCipher.
/// </summary>
public interface IConnectionRepository
{
    /// <summary>Load the full tree. Returns the virtual root <see cref="ConnectionGroup"/>.</summary>
    Task<ConnectionGroup> LoadTreeAsync(CancellationToken ct = default);

    /// <summary>Insert or update a connection (upsert by Id).</summary>
    Task SaveConnectionAsync(ConnectionInfo connection, CancellationToken ct = default);

    /// <summary>Insert or update a group (upsert by Id).</summary>
    Task SaveGroupAsync(ConnectionGroup group, CancellationToken ct = default);

    /// <summary>Delete a connection by Id.</summary>
    Task DeleteConnectionAsync(Guid id, CancellationToken ct = default);

    /// <summary>Delete a group and, if <paramref name="recursive"/> is true, all its descendants.</summary>
    Task DeleteGroupAsync(Guid id, bool recursive = false, CancellationToken ct = default);

    /// <summary>Move a connection to a different group and set its sort position.</summary>
    Task MoveConnectionAsync(Guid connectionId, Guid targetGroupId, int sortOrder, CancellationToken ct = default);

    /// <summary>Move a group to a different parent and set its sort position.</summary>
    Task MoveGroupAsync(Guid groupId, Guid targetParentId, int sortOrder, CancellationToken ct = default);

    /// <summary>Full-text search across Name, Hostname, and Username fields.</summary>
    Task<IReadOnlyList<ConnectionInfo>> SearchAsync(string query, CancellationToken ct = default);
}
