namespace LiteConnect.Core.Models;

/// <summary>
/// A tree node that represents a folder in the connection manager.
/// Can contain child groups and child connections.
/// The virtual root has <see cref="ParentId"/> == null and <see cref="Id"/> == <see cref="Guid.Empty"/>.
/// </summary>
public class ConnectionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Null for root-level groups.</summary>
    public Guid? ParentId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>Whether the tree node is expanded in the UI.</summary>
    public bool IsExpanded { get; set; } = true;

    // Inherited defaults — null means "inherit from parent or global"
    public string? DefaultUsername { get; set; }
    public string? DefaultDomain { get; set; }
    public ProtocolType? DefaultProtocol { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ── Navigation properties (populated by the repository, not stored in DB) ──

    /// <summary>Child sub-groups.</summary>
    public List<ConnectionGroup> Groups { get; set; } = [];

    /// <summary>Connections directly inside this group.</summary>
    public List<ConnectionInfo> Connections { get; set; } = [];
}
