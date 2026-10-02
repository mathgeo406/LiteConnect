using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.App.Views;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.ImportExport;
using LiteConnect.Core.Models;
using LiteConnect.Core.Serialization;
using LiteConnect.Core.Services;
using LiteConnect.Protocols.Rdp;
using LiteConnect.Protocols.Ssh;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// Root ViewModel bound to <c>MainWindow</c>.
/// Owns the tab strip, Quick Connect bar (with recent connections history),
/// Export/Import commands, and global tab navigation shortcuts.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxRecentConnections = 10;

    private readonly IConnectionRepository _repository;
    private readonly ICredentialStore _credentialStore;
    private readonly IKnownHostsStore? _knownHostsStore;
    private readonly IAppEnvironment? _appEnvironment;
    private readonly ISessionRecorderFactory? _recorderFactory;
    private readonly IExportService _exportService;
    private readonly IImportService _importService;

    // ── Sub-ViewModels ──────────────────────────────────────────────────────────

    /// <summary>Connection tree panel ViewModel.</summary>
    public ConnectionTreeViewModel TreeViewModel { get; }

    public MainViewModel(
        ConnectionTreeViewModel treeViewModel,
        IConnectionRepository repository,
        ICredentialStore credentialStore,
        IKnownHostsStore? knownHostsStore = null,
        IAppEnvironment? appEnvironment = null,
        ISessionRecorderFactory? recorderFactory = null,
        IExportService? exportService = null,
        IImportService? importService = null)
    {
        TreeViewModel    = treeViewModel;
        _repository      = repository;
        _credentialStore = credentialStore;
        _knownHostsStore = knownHostsStore;
        _appEnvironment  = appEnvironment;
        _recorderFactory = recorderFactory;

        var defaultImportExport = new ConnectionExportImportService(repository, appEnvironment);
        _exportService = exportService ?? defaultImportExport;
        _importService = importService ?? defaultImportExport;

        TreeViewModel.ConnectionOpenRequested += (_, conn) => _ = OpenSessionAsync(conn);
        LoadRecentConnections();
    }

    // ── Tab collection ──────────────────────────────────────────────────────────

    /// <summary>All open session tabs (bound to the <c>TabControl</c>).</summary>
    public ObservableCollection<ISessionTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private ISessionTabViewModel? _activeTab;

    partial void OnActiveTabChanged(ISessionTabViewModel? oldValue, ISessionTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            if (oldValue is SshSessionViewModel oldSsh)
                oldSsh.RecordingStatusUpdated -= OnActiveTabRecordingUpdated;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            if (newValue is SshSessionViewModel newSsh)
                newSsh.RecordingStatusUpdated += OnActiveTabRecordingUpdated;
        }

        RefreshRecordingStatusBar();
    }

    private void OnActiveTabRecordingUpdated(object? sender, EventArgs e)
    {
        RefreshRecordingStatusBar();
    }

    private void RefreshRecordingStatusBar()
    {
        if (ActiveTab is SshSessionViewModel sshTab && sshTab.IsRecording)
        {
            IsActiveTabRecording      = true;
            ActiveRecordingStatusText = sshTab.RecordingStatusText;
        }
        else
        {
            IsActiveTabRecording      = false;
            ActiveRecordingStatusText = string.Empty;
        }
    }

    // ── Quick Connect bar & Recent Connections (Sprint 8.1) ─────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickConnectCommand))]
    private string _quickConnectHost = string.Empty;

    [ObservableProperty]
    private string _quickConnectProtocol = "SSH";

    public IReadOnlyList<string> ProtocolOptions { get; } = ["SSH", "RDP", "VNC"];

    public ObservableCollection<string> RecentQuickConnections { get; } = [];

    // ── View visibility toggles ─────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isTreePanelVisible = true;

    [ObservableProperty]
    private bool _isStatusBarVisible = true;

    // ── Status bar ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isActiveTabRecording;

    [ObservableProperty]
    private string _activeRecordingStatusText = string.Empty;

    public int OpenSessionCount => Tabs.Count;

    // ── Commands ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleActiveTabRecording()
    {
        ActiveTab?.ToggleRecordingCommand?.Execute(null);
    }

    [RelayCommand]
    private void CloseActiveTab()
    {
        if (ActiveTab is not null)
            CloseTab(ActiveTab);
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count <= 1 || ActiveTab is null)
            return;

        int idx = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(idx + 1) % Tabs.Count];
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Tabs.Count <= 1 || ActiveTab is null)
            return;

        int idx = Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(idx - 1 + Tabs.Count) % Tabs.Count];
    }

    [RelayCommand]
    private void SelectTabByIndex(string? indexParam)
    {
        if (!int.TryParse(indexParam, out int oneBased) || oneBased < 1)
            return;

        int zeroBased = oneBased - 1;
        if (zeroBased < Tabs.Count)
            ActiveTab = Tabs[zeroBased];
    }

    public void MoveTab(ISessionTabViewModel source, ISessionTabViewModel target)
    {
        if (ReferenceEquals(source, target))
            return;

        int oldIndex = Tabs.IndexOf(source);
        int newIndex = Tabs.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
            return;

        Tabs.Move(oldIndex, newIndex);
        ActiveTab = source;
    }

    [RelayCommand]
    private void CloseTab(ISessionTabViewModel? tab)
    {
        if (tab is null) return;

        if (tab is SshSessionViewModel sshVm)
            sshVm.RecordingStatusUpdated -= OnActiveTabRecordingUpdated;

        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // Keep selection reasonable after close
        if (ActiveTab == tab)
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, idx - 1)]
                : null;

        try { tab.Dispose(); } catch { /* best-effort cleanup */ }

        OnPropertyChanged(nameof(OpenSessionCount));
        RefreshRecordingStatusBar();
        StatusText = Tabs.Count == 0 ? "Ready" : $"{Tabs.Count} session(s) open";
    }

    [RelayCommand(CanExecute = nameof(CanQuickConnect))]
    private async Task QuickConnectAsync()
    {
        var fallbackProto = Enum.TryParse<ProtocolType>(QuickConnectProtocol, ignoreCase: true, out var p)
            ? p
            : ProtocolType.SSH;

        if (!QuickConnectParser.TryParse(QuickConnectHost, fallbackProto, out var target) || target is null)
            return;

        if (target.ProtocolAutoDetected)
        {
            QuickConnectProtocol = target.Protocol.ToString();
        }

        AddRecentConnection(QuickConnectHost.Trim());

        var adhoc = new ConnectionInfo
        {
            Name     = target.Hostname,
            Hostname = target.Hostname,
            Port     = target.Port,
            Username = target.Username,
            Domain   = target.Domain,
            Protocol = target.Protocol
        };

        await OpenSessionAsync(adhoc);
    }

    private bool CanQuickConnect() => !string.IsNullOrWhiteSpace(QuickConnectHost);

    // ── Export / Import Commands (Sprint 7.5) ───────────────────────────────────

    [RelayCommand]
    private void ExportConnections()
    {
        IReadOnlyList<Guid>? selectedIds = null;
        string? selectedLabel = null;

        if (TreeViewModel.SelectedNode is { } node)
        {
            selectedLabel = node.DisplayName;
            var ids = new List<Guid>();
            CollectNodeConnectionIds(node, ids);
            if (ids.Count > 0)
                selectedIds = ids;
        }

        var dlg = new ExportConnectionsDialog(_exportService, selectedIds, selectedLabel);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            dlg.Owner = owner;

        if (dlg.ShowDialog() == true)
        {
            StatusText = "Connections exported successfully";
        }
    }

    [RelayCommand]
    private async Task ImportConnectionsAsync()
    {
        var dlg = new ImportConnectionsDialog(_importService);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            dlg.Owner = owner;

        if (dlg.ShowDialog() == true && dlg.Result is { } res)
        {
            await TreeViewModel.LoadTreeAsync();
            StatusText = $"Imported {res.ImportedCount} connection(s), overwritten {res.OverwrittenCount}, skipped {res.SkippedCount}";
        }
    }

    private static void CollectNodeConnectionIds(TreeNodeViewModel node, List<Guid> ids)
    {
        if (!node.IsGroup && node.Connection is not null)
        {
            ids.Add(node.Connection.Id);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectNodeConnectionIds(child, ids);
        }
    }

    [RelayCommand]
    private void OpenSecuritySettings()
    {
        if (_credentialStore is not SqlCipherCredentialStore sqlCipherStore)
            return;

        var dlg = new SecuritySettingsDialog(sqlCipherStore);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            dlg.Owner = owner;

        dlg.ShowDialog();
    }

    // ── Session factory / tab launcher (Sprint 3.5, 4.7, 5.1, 6.2) ──────────────

    /// <summary>
    /// Resolves effective credentials (Connection → Group → Global) and opens a protocol session tab.
    /// </summary>
    public async Task OpenSessionAsync(ConnectionInfo connection)
    {
        var effectiveConnection = await CredentialResolver.ResolveEffectiveAsync(
            connection,
            _repository,
            _credentialStore);

        if (effectiveConnection.Protocol == ProtocolType.RDP)
        {
            var rdpSession = new RdpSession();
            var rdpVm      = new RdpSessionViewModel(rdpSession, effectiveConnection, CloseTabCommand, _repository);
            AddTab(rdpVm);
            await rdpVm.ConnectAsync();
        }
        else if (effectiveConnection.Protocol == ProtocolType.SSH)
        {
            var sshSession = new SshSession(_knownHostsStore);
            var recorder   = _recorderFactory?.CreateRecorder();
            var sshVm      = new SshSessionViewModel(
                sshSession,
                effectiveConnection,
                CloseTabCommand,
                _repository,
                _appEnvironment,
                recorder);
            AddTab(sshVm);
            await sshVm.ConnectAsync();
        }
        else
        {
            var placeholder = new PlaceholderTabViewModel(effectiveConnection, CloseTabCommand);
            AddTab(placeholder);
        }
    }

    /// <summary>
    /// Add a tab and make it active.
    /// </summary>
    public void AddTab(ISessionTabViewModel tab)
    {
        Tabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(OpenSessionCount));
        StatusText = $"{Tabs.Count} session(s) open";
    }

    // ── Recent Quick Connections persistence ────────────────────────────────────

    private string GetRecentConnectionsFilePath()
    {
        string dir = _appEnvironment?.DataDirectory ?? Path.Combine(AppContext.BaseDirectory, "data");
        return Path.Combine(dir, "recent_connections.json");
    }

    private void LoadRecentConnections()
    {
        try
        {
            string path = GetRecentConnectionsFilePath();
            if (!File.Exists(path))
                return;

            string json = File.ReadAllText(path);
            var list = ConnectionSettingsSerializer.Deserialize<List<string>>(json);
            if (list is null)
                return;

            RecentQuickConnections.Clear();
            foreach (var item in list.Take(MaxRecentConnections))
            {
                if (!string.IsNullOrWhiteSpace(item))
                    RecentQuickConnections.Add(item);
            }
        }
        catch
        {
            // Ignore corrupt recent history file
        }
    }

    private void AddRecentConnection(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return;

        for (int i = RecentQuickConnections.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentQuickConnections[i], entry, StringComparison.OrdinalIgnoreCase))
                RecentQuickConnections.RemoveAt(i);
        }

        RecentQuickConnections.Insert(0, entry);
        while (RecentQuickConnections.Count > MaxRecentConnections)
        {
            RecentQuickConnections.RemoveAt(RecentQuickConnections.Count - 1);
        }

        try
        {
            string path = GetRecentConnectionsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? json = ConnectionSettingsSerializer.Serialize(RecentQuickConnections.ToList());
            if (json is not null)
                File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
