using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Protocols.Rdp;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// Root ViewModel bound to <c>MainWindow</c>.
/// Owns the tab strip and drives the Quick Connect bar.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConnectionRepository _repository;

    // ── Sub-ViewModels ──────────────────────────────────────────────────────────

    /// <summary>Connection tree panel ViewModel.</summary>
    public ConnectionTreeViewModel TreeViewModel { get; }

    public MainViewModel(ConnectionTreeViewModel treeViewModel, IConnectionRepository repository)
    {
        TreeViewModel = treeViewModel;
        _repository   = repository;

        TreeViewModel.ConnectionOpenRequested += (_, conn) => _ = OpenSessionAsync(conn);
    }

    // ── Tab collection ──────────────────────────────────────────────────────────

    /// <summary>All open session tabs (bound to the <c>TabControl</c>).</summary>
    public ObservableCollection<ISessionTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    private ISessionTabViewModel? _activeTab;

    partial void OnActiveTabChanged(ISessionTabViewModel? oldValue, ISessionTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
    }

    // ── Quick Connect bar ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickConnectCommand))]
    private string _quickConnectHost = string.Empty;

    [ObservableProperty]
    private string _quickConnectProtocol = "SSH";

    public IReadOnlyList<string> ProtocolOptions { get; } = ["SSH", "RDP", "VNC"];

    // ── Status bar ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = "Ready";

    public int OpenSessionCount => Tabs.Count;

    // ── Commands ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void CloseTab(ISessionTabViewModel? tab)
    {
        if (tab is null) return;

        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // Keep selection reasonable after close
        if (ActiveTab == tab)
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, idx - 1)]
                : null;

        try { tab.Dispose(); } catch { /* best-effort cleanup */ }

        OnPropertyChanged(nameof(OpenSessionCount));
        StatusText = Tabs.Count == 0 ? "Ready" : $"{Tabs.Count} session(s) open";
    }

    [RelayCommand(CanExecute = nameof(CanQuickConnect))]
    private async Task QuickConnectAsync()
    {
        var input = QuickConnectHost.Trim();
        if (string.IsNullOrEmpty(input)) return;

        string hostname = input;
        int? port = null;

        int colonIdx = input.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(input[(colonIdx + 1)..], out int parsedPort))
        {
            hostname = input[..colonIdx];
            port     = parsedPort;
        }

        var protocol = Enum.TryParse<ProtocolType>(QuickConnectProtocol, ignoreCase: true, out var p)
            ? p
            : ProtocolType.SSH;

        var adhoc = new ConnectionInfo
        {
            Name     = hostname,
            Hostname = hostname,
            Port     = port,
            Protocol = protocol
        };

        await OpenSessionAsync(adhoc);
    }

    private bool CanQuickConnect() => !string.IsNullOrWhiteSpace(QuickConnectHost);

    // ── Session factory / tab launcher ──────────────────────────────────────────

    /// <summary>
    /// Creates and opens a protocol session tab for <paramref name="connection"/>.
    /// </summary>
    public async Task OpenSessionAsync(ConnectionInfo connection)
    {
        if (connection.Protocol == ProtocolType.RDP)
        {
            var rdpSession = new RdpSession();
            var rdpVm      = new RdpSessionViewModel(rdpSession, connection, CloseTabCommand, _repository);
            AddTab(rdpVm);
            await rdpVm.ConnectAsync();
        }
        else
        {
            // SSH (Sprint 5) and VNC (Sprint 8) use PlaceholderTabViewModel for now
            var placeholder = new PlaceholderTabViewModel(connection, CloseTabCommand);
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
}
