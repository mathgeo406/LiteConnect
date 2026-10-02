using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Protocols.Rdp;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// ViewModel for an active RDP session tab (Sprint 3.5).
/// Wraps <see cref="RdpSession"/>, exposes tab header status, context menu commands,
/// disconnect/reconnect overlay state, and resize handling.
/// </summary>
public sealed partial class RdpSessionViewModel : ObservableObject, ISessionTabViewModel
{
    private readonly RdpSession _session;
    private readonly ConnectionInfo _connectionInfo;
    private readonly IConnectionRepository? _repository;
    private bool _disposed;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsDisconnectedOrError))]
    [NotifyPropertyChangedFor(nameof(IsConnectingOrReconnecting))]
    private SessionState _state = SessionState.Disconnected;

    [ObservableProperty]
    private string _statusMessage = "Connecting…";

    /// <summary>
    /// Status icon shown in the tab header:
    /// 🟢 connected, 🔴 disconnected/error, 🟡 connecting/reconnecting.
    /// </summary>
    public string StatusIcon => State switch
    {
        SessionState.Connected => "🟢",
        SessionState.Connecting or SessionState.Disconnecting => "🟡",
        _ => "🔴"
    };

    public bool IsConnected => State == SessionState.Connected;
    public bool IsDisconnectedOrError => State is SessionState.Disconnected or SessionState.Error;
    public bool IsConnectingOrReconnecting => State == SessionState.Connecting;

    /// <summary>The WPF <see cref="FrameworkElement"/> (<c>WindowsFormsHost</c>) rendering the RDP session.</summary>
    public FrameworkElement SessionControl { get; }

    // ── Commands ────────────────────────────────────────────────────────────────

    public IRelayCommand CloseCommand { get; }
    public IRelayCommand? ReconnectCommand => ReconnectSessionCommand;
    public IRelayCommand? DisconnectCommand => DisconnectSessionCommand;
    public IRelayCommand? FullScreenCommand => EnterFullScreenCommand;
    public IRelayCommand? PropertiesCommand => OpenPropertiesCommand;
    public IRelayCommand? ToggleRecordingCommand => null;

    // ── Constructor ─────────────────────────────────────────────────────────────

    public RdpSessionViewModel(
        RdpSession session,
        ConnectionInfo connectionInfo,
        IRelayCommand closeTabCommand,
        IConnectionRepository? repository = null)
    {
        _session        = session ?? throw new ArgumentNullException(nameof(session));
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _repository     = repository;

        _displayName = string.IsNullOrWhiteSpace(connectionInfo.Name)
            ? connectionInfo.Hostname
            : connectionInfo.Name;

        SessionControl = (FrameworkElement)_session.GetSessionControl();
        CloseCommand   = new RelayCommand(() => closeTabCommand.Execute(this));

        _session.StateChanged       += OnSessionStateChanged;
        _session.NotificationRaised += OnSessionNotificationRaised;
    }

    // ── Session lifecycle ───────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            State         = SessionState.Connecting;
            StatusMessage = $"Connecting to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}…";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            await _session.ConnectAsync(_connectionInfo, ct);
        }
        catch (Exception ex)
        {
            State         = SessionState.Error;
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanReconnectSession))]
    private async Task ReconnectSessionAsync()
    {
        try
        {
            State         = SessionState.Connecting;
            StatusMessage = $"Reconnecting to {_connectionInfo.Hostname}…";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            await _session.ReconnectAsync();
        }
        catch (Exception ex)
        {
            State         = SessionState.Error;
            StatusMessage = $"Reconnect failed: {ex.Message}";
        }
    }

    private bool CanReconnectSession() => State is SessionState.Disconnected or SessionState.Error;

    [RelayCommand(CanExecute = nameof(CanDisconnectSession))]
    private async Task DisconnectSessionAsync()
    {
        await _session.DisconnectAsync();
    }

    private bool CanDisconnectSession() =>
        State is SessionState.Connected or SessionState.Connecting;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void EnterFullScreen()
    {
        _session.SetFullScreen(true);
    }

    [RelayCommand]
    private async Task OpenPropertiesAsync()
    {
        var dialog = new Views.ConnectionPropertiesDialog(_connectionInfo);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            DisplayName = string.IsNullOrWhiteSpace(dialog.Result.Name)
                ? dialog.Result.Hostname
                : dialog.Result.Name;

            if (_repository is not null)
                await _repository.SaveConnectionAsync(dialog.Result);
        }
    }

    // ── Resize (3.5) ────────────────────────────────────────────────────────────

    public void OnResize(int width, int height)
    {
        _session.OnResize(width, height);
    }

    // ── Event handlers ──────────────────────────────────────────────────────────

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        void Apply()
        {
            State = e.Current;
            ReconnectSessionCommand.NotifyCanExecuteChanged();
            DisconnectSessionCommand.NotifyCanExecuteChanged();
            EnterFullScreenCommand.NotifyCanExecuteChanged();

            StatusMessage = e.Current switch
            {
                SessionState.Connecting   => $"Connecting to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}…",
                SessionState.Connected    => $"Connected to {_connectionInfo.Hostname}",
                SessionState.Disconnected => _session.LastDisconnectReason ?? "Session disconnected.",
                SessionState.Error        => _session.LastDisconnectReason ?? "Connection error.",
                _                         => string.Empty
            };
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Apply);
        else
            Apply();
    }

    private void OnSessionNotificationRaised(object? sender, SessionNotificationEventArgs e)
    {
        void Apply() => StatusMessage = e.Message;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Apply);
        else
            Apply();
    }

    // ── IDisposable ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session.StateChanged       -= OnSessionStateChanged;
        _session.NotificationRaised -= OnSessionNotificationRaised;
        _session.Dispose();
    }
}
