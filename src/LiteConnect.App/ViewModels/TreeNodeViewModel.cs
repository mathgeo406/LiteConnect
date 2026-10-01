using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.Core.Models;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// A node in the connection tree — wraps either a <see cref="ConnectionGroup"/>
/// or a <see cref="ConnectionInfo"/> with presentation state (expanded, selected).
/// </summary>
public sealed partial class TreeNodeViewModel : ObservableObject
{
    // ── Discrimination ──────────────────────────────────────────────────────────

    public bool IsGroup      { get; }
    public bool IsConnection => !IsGroup;

    /// <summary>The underlying group (null when this node is a connection).</summary>
    public ConnectionGroup? Group { get; }

    /// <summary>The underlying connection (null when this node is a group).</summary>
    public ConnectionInfo? Connection { get; }

    // ── Presentation ────────────────────────────────────────────────────────────

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private bool   _isExpanded;
    [ObservableProperty] private bool   _isSelected;
    [ObservableProperty] private bool   _isEditing;
    [ObservableProperty] private bool   _isVisible = true;

    public string Icon => IsGroup
        ? (IsExpanded ? "📂" : "📁")
        : Connection!.Protocol switch
        {
            ProtocolType.SSH => "🐧",
            ProtocolType.RDP => "🖥️",
            ProtocolType.VNC => "🖱️",
            _                => "🔌"
        };

    /// <summary>Child nodes (groups and connections inside this group node).</summary>
    public List<TreeNodeViewModel> Children { get; } = [];

    /// <summary>Parent node, or null if this is a root-level node.</summary>
    public TreeNodeViewModel? Parent { get; set; }

    // ── Constructors ────────────────────────────────────────────────────────────

    public TreeNodeViewModel(ConnectionGroup group, TreeNodeViewModel? parent = null)
    {
        IsGroup      = true;
        Group        = group;
        Parent       = parent;
        _displayName = group.Name;
        _isExpanded  = group.IsExpanded;
    }

    public TreeNodeViewModel(ConnectionInfo connection, TreeNodeViewModel? parent = null)
    {
        IsGroup     = false;
        Connection  = connection;
        Parent      = parent;
        _displayName = connection.Name;
        _isExpanded  = false;
    }

    // ── Property notifications that affect icon ─────────────────────────────────

    partial void OnIsExpandedChanged(bool value)
    {
        if (IsGroup) OnPropertyChanged(nameof(Icon));
    }

    // ── Filter / search ─────────────────────────────────────────────────────────

    /// <summary>
    /// Apply a visibility filter recursively.
    /// Returns true if this node (or any descendant) matches.
    /// </summary>
    public bool ApplyFilter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            IsVisible = true;
            foreach (var child in Children) child.ApplyFilter(query);
            return true;
        }

        var q = query.Trim();
        bool selfMatches = IsGroup
            ? DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            : (DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (Connection!.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
               (Connection.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        bool anyChildMatches = false;
        foreach (var child in Children)
            anyChildMatches |= child.ApplyFilter(q);

        IsVisible = selfMatches || anyChildMatches;

        // Auto-expand groups with matching children
        if (IsGroup && anyChildMatches) IsExpanded = true;

        return IsVisible;
    }
}
