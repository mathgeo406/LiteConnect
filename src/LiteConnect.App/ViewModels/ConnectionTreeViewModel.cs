using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using System.Collections.ObjectModel;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// ViewModel for the left-panel connection tree.
/// Loads from <see cref="IConnectionRepository"/> on startup,
/// exposes the observable tree for XAML binding, and handles all
/// tree-mutation commands (new folder, new connection, edit, delete, move).
/// </summary>
public sealed partial class ConnectionTreeViewModel : ObservableObject
{
    private readonly IConnectionRepository _repository;

    /// <summary>Raised when the user double-clicks a connection node to open a session tab.</summary>
    public event EventHandler<ConnectionInfo>? ConnectionOpenRequested;

    // ── Tree state ──────────────────────────────────────────────────────────────

    /// <summary>Root-level nodes bound to the TreeView.</summary>
    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewConnectionInGroupCommand))]
    private TreeNodeViewModel? _selectedNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchQuery))]
    private string _searchQuery = string.Empty;

    public bool HasSearchQuery => !string.IsNullOrEmpty(SearchQuery);

    // ── Loading ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _loadError = string.Empty;
    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    // ── Constructor ─────────────────────────────────────────────────────────────

    public ConnectionTreeViewModel(IConnectionRepository repository)
    {
        _repository = repository;
    }

    // ── Load ────────────────────────────────────────────────────────────────────

    /// <summary>Load (or reload) the full connection tree from the repository.</summary>
    [RelayCommand]
    public async Task LoadTreeAsync()
    {
        IsLoading = true;
        LoadError = string.Empty;

        try
        {
            var root = await _repository.LoadTreeAsync();
            RootNodes.Clear();
            foreach (var group in root.Groups.OrderBy(g => g.SortOrder))
                RootNodes.Add(BuildNode(group, parent: null));

            // Top-level connections (if any are outside a group)
            foreach (var conn in root.Connections.OrderBy(c => c.SortOrder))
                RootNodes.Add(new TreeNodeViewModel(conn, parent: null));
        }
        catch (Exception ex)
        {
            LoadError = $"Failed to load connections: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static TreeNodeViewModel BuildNode(ConnectionGroup group, TreeNodeViewModel? parent)
    {
        var node = new TreeNodeViewModel(group, parent);

        foreach (var subGroup in group.Groups.OrderBy(g => g.SortOrder))
            node.Children.Add(BuildNode(subGroup, node));

        foreach (var conn in group.Connections.OrderBy(c => c.SortOrder))
            node.Children.Add(new TreeNodeViewModel(conn, node));

        return node;
    }

    // ── Search ──────────────────────────────────────────────────────────────────

    partial void OnSearchQueryChanged(string value)
    {
        foreach (var node in RootNodes) node.ApplyFilter(value);
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    // ── New Group ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewGroupAsync()
    {
        var parentGroup = SelectedNode?.IsGroup == true
            ? SelectedNode.Group
            : null; // top-level

        var group = new ConnectionGroup
        {
            Name      = "New Folder",
            ParentId  = parentGroup?.Id,
            SortOrder = parentGroup is null
                ? RootNodes.Count
                : (parentGroup.Groups.Count + parentGroup.Connections.Count)
        };

        await _repository.SaveGroupAsync(group);
        await LoadTreeAsync();

        // Select the newly created node and put it in edit mode
        var newNode = FindNode(group.Id, isGroup: true);
        if (newNode is not null)
        {
            SelectedNode      = newNode;
            newNode.IsEditing = true;
        }
    }

    // ── New Connection ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        Guid? defaultGroupId = SelectedNode switch
        {
            { IsGroup: true, Group: not null } => SelectedNode.Group.Id,
            { IsConnection: true, Connection: not null } => SelectedNode.Connection.GroupId,
            _ => null
        };
        await OpenConnectionPropertiesAsync(connection: null, defaultGroupId: defaultGroupId);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGroup))]
    private async Task NewConnectionInGroupAsync()
    {
        var parentGroup = SelectedNode!.Group!;
        await OpenConnectionPropertiesAsync(connection: null, defaultGroupId: parentGroup.Id);
    }

    private bool HasSelectedGroup() => SelectedNode?.IsGroup == true;

    // ── Edit ────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasSelectedNode))]
    private async Task EditNodeAsync()
    {
        if (SelectedNode is null) return;

        if (SelectedNode.IsGroup)
        {
            // Inline rename
            SelectedNode.IsEditing = true;
        }
        else
        {
            await OpenConnectionPropertiesAsync(SelectedNode.Connection!);
        }
    }

    private bool HasSelectedNode() => SelectedNode is not null;

    /// <summary>Called from XAML when an inline rename is committed (Enter / LostFocus).</summary>
    [RelayCommand]
    private async Task CommitRenameAsync(TreeNodeViewModel? node)
    {
        if (node is null || !node.IsEditing) return;
        node.IsEditing = false;

        if (node.IsGroup && node.Group is not null)
        {
            node.Group.Name      = node.DisplayName;
            node.Group.UpdatedAt = DateTimeOffset.UtcNow;
            await _repository.SaveGroupAsync(node.Group);
        }
    }

    [RelayCommand]
    private void CancelRename(TreeNodeViewModel? node)
    {
        if (node is null) return;
        node.IsEditing = false;
        // Revert display name to stored name
        if (node.IsGroup) node.DisplayName = node.Group!.Name;
        else              node.DisplayName = node.Connection!.Name;
    }

    // ── Delete ──────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasSelectedNode))]
    private async Task DeleteNodeAsync()
    {
        if (SelectedNode is null) return;

        if (SelectedNode.IsGroup)
            await _repository.DeleteGroupAsync(SelectedNode.Group!.Id, recursive: true);
        else
            await _repository.DeleteConnectionAsync(SelectedNode.Connection!.Id);

        await LoadTreeAsync();
    }

    // ── Open (double-click) ─────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenConnection(TreeNodeViewModel? node)
    {
        if (node?.IsConnection != true || node.Connection is null) return;
        ConnectionOpenRequested?.Invoke(this, node.Connection);
    }

    // ── Properties dialog ───────────────────────────────────────────────────────

    private async Task OpenConnectionPropertiesAsync(
        ConnectionInfo? connection, Guid? defaultGroupId = null)
    {
        // If no group exists yet and creating a new connection without a group, ensure a default group exists
        if (connection is null && (defaultGroupId is null || defaultGroupId == Guid.Empty))
        {
            var firstGroup = RootNodes.FirstOrDefault(n => n.IsGroup)?.Group;
            if (firstGroup is null)
            {
                firstGroup = new ConnectionGroup { Name = "Connections", SortOrder = 0 };
                await _repository.SaveGroupAsync(firstGroup);
            }
            defaultGroupId = firstGroup.Id;
        }

        var dialog = new Views.ConnectionPropertiesDialog(connection, defaultGroupId);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            await _repository.SaveConnectionAsync(dialog.Result);
            await LoadTreeAsync();
        }
    }

    // ── Move (future drag-drop hook) ────────────────────────────────────────────

    public async Task MoveConnectionAsync(Guid connectionId, Guid targetGroupId, int sortOrder)
    {
        await _repository.MoveConnectionAsync(connectionId, targetGroupId, sortOrder);
        await LoadTreeAsync();
    }

    public async Task MoveGroupAsync(Guid groupId, Guid targetParentId, int sortOrder)
    {
        await _repository.MoveGroupAsync(groupId, targetParentId, sortOrder);
        await LoadTreeAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private TreeNodeViewModel? FindNode(Guid id, bool isGroup)
    {
        return Search(RootNodes);

        TreeNodeViewModel? Search(IEnumerable<TreeNodeViewModel> nodes)
        {
            foreach (var n in nodes)
            {
                if (isGroup  && n.IsGroup      && n.Group!.Id      == id) return n;
                if (!isGroup && n.IsConnection && n.Connection!.Id == id) return n;
                var found = Search(n.Children);
                if (found is not null) return found;
            }
            return null;
        }
    }
}
