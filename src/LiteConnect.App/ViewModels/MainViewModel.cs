using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.App.Views;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Core.Services;
using LiteConnect.Protocols.Rdp;
using LiteConnect.Protocols.Ssh;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// Root ViewModel bound to <c>MainWindow</c>.
/// Owns the tab strip, drives the Quick Connect bar, and exposes the active tab's recording status.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConnectionRepository _repository;
    private readonly ICredentialStore _credentialStore;
    private readonly IKnownHostsStore? _knownHostsStore;
    private readonly IAppEnvironment? _appEnvironment;
    private readonly ISessionRecorderFactory? _recorderFactory;

    // ── Sub-ViewModels ──────────────────────────────────────────────────────────

    /// <summary>Connection tree panel ViewModel.</summary>
    public ConnectionTreeViewModel TreeViewModel { get; }

    public MainViewModel(
        ConnectionTreeViewModel treeViewModel,
        IConnectionRepository repository,
        ICredentialStore credentialStore,
        IKnownHostsStore? knownHostsStore = null,
        IAppEnvironment? appEnvironment = null,
        ISessionRecorderFactory? recorderFactory = null)
    {
        TreeViewModel    = treeViewModel;
        _repository      = repository;
        _credentialStore = credentialStore;
        _knownHostsStore = knownHostsStore;
        _appEnvironment  = appEnvironment;
        _recorderFactory = recorderFactory;

        TreeViewModel.ConnectionOpenRequested += (_, conn) => _ = OpenSessionAsync(conn);
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
        var input = QuickConnectHost.Trim();
        if (string.IsNullOrEmpty(input)) return;

        string? username = null;
        string hostPart  = input;

        int atIdx = hostPart.IndexOf('@');
        if (atIdx > 0 && atIdx < hostPart.Length - 1)
        {
            username = hostPart[..atIdx];
            hostPart = hostPart[(atIdx + 1)..];
        }

        string hostname = hostPart;
        int? port = null;

        int colonIdx = hostPart.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(hostPart[(colonIdx + 1)..], out int parsedPort))
        {
            hostname = hostPart[..colonIdx];
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
            Username = username,
            Protocol = protocol
        };

        await OpenSessionAsync(adhoc);
    }

    private bool CanQuickConnect() => !string.IsNullOrWhiteSpace(QuickConnectHost);

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
            // VNC (Sprint 8) uses PlaceholderTabViewModel for now
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
}
