using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.App.Views;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Protocols.Ssh;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// ViewModel for an active SSH terminal tab (Sprint 5).
/// Wraps <see cref="SshSession"/>, binds to <see cref="SshTabView"/>, and handles
/// host key verification prompts, copy/paste, clear terminal, reconnect, and disconnect.
/// </summary>
public sealed partial class SshSessionViewModel : ObservableObject, ISessionTabViewModel
{
    private readonly SshSession _session;
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
    private SessionState _state = SessionState.Idle;

    [ObservableProperty]
    private string _statusMessage = "Connecting…";

    public string StatusIcon => State switch
    {
        SessionState.Connected => "🟢",
        SessionState.Connecting or SessionState.Disconnecting => "🟡",
        _ => "🔴"
    };

    public bool IsConnected => State == SessionState.Connected;
    public bool IsDisconnectedOrError => State is SessionState.Disconnected or SessionState.Error;

    /// <summary>The WPF <see cref="FrameworkElement"/> (<c>TerminalControl</c>) rendering the SSH session.</summary>
    public FrameworkElement SessionControl { get; }

    public SshSession Session => _session;

    // ── Commands ────────────────────────────────────────────────────────────────

    public IRelayCommand CloseCommand { get; }
    public IRelayCommand? ReconnectCommand => ReconnectSessionCommand;
    public IRelayCommand? DisconnectCommand => DisconnectSessionCommand;
    public IRelayCommand? FullScreenCommand => null;
    public IRelayCommand? PropertiesCommand => OpenPropertiesCommand;

    public SshSessionViewModel(
        SshSession session,
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
        _session.HostKeyPromptCallback = PromptHostKeyVerificationAsync;
    }

    // ── Session lifecycle ───────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            StatusMessage = $"Connecting to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}…";
            await _session.ConnectAsync(_connectionInfo, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanReconnectSession))]
    private async Task ReconnectSessionAsync()
    {
        try
        {
            StatusMessage = $"Reconnecting to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}…";
            await _session.ConnectAsync(_connectionInfo);
        }
        catch (Exception ex)
        {
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

    [RelayCommand]
    private void CopySelection()
    {
        _session.Terminal.CopySelectionToClipboard();
    }

    [RelayCommand]
    private void PasteClipboard()
    {
        _session.Terminal.PasteFromClipboard();
    }

    [RelayCommand]
    private void ClearTerminal()
    {
        lock (_session.Terminal.Buffer.SyncRoot)
        {
            _session.Terminal.Buffer.EraseInDisplay(3);
            _session.Terminal.Buffer.SetCursorPosition(0, 0);
        }
        _session.Terminal.InvalidateVisual();
    }

    [RelayCommand]
    private async Task OpenPropertiesAsync()
    {
        var dialog = new ConnectionPropertiesDialog(_connectionInfo);
        if (System.Windows.Application.Current?.MainWindow is { } owner)
            dialog.Owner = owner;

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            DisplayName = string.IsNullOrWhiteSpace(dialog.Result.Name)
                ? dialog.Result.Hostname
                : dialog.Result.Name;

            _session.Terminal.ApplySettings(dialog.Result.SshSettings);

            if (_repository is not null)
                await _repository.SaveConnectionAsync(dialog.Result);
        }
    }

    // ── Host Key Verification Prompt ────────────────────────────────────────────

    private Task<HostKeyVerificationDecision> PromptHostKeyVerificationAsync(HostKeyVerificationPrompt prompt)
    {
        var dlg = new HostKeyVerificationDialog(prompt);
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner)
            dlg.Owner = owner;

        dlg.ShowDialog();
        return Task.FromResult(dlg.Decision);
    }

    // ── Events ──────────────────────────────────────────────────────────────────

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        void Apply()
        {
            State = e.Current;
            ReconnectSessionCommand.NotifyCanExecuteChanged();
            DisconnectSessionCommand.NotifyCanExecuteChanged();

            StatusMessage = e.Current switch
            {
                SessionState.Connecting   => $"Connecting to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}…",
                SessionState.Connected    => $"Connected to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}",
                SessionState.Disconnected => "Disconnected",
                SessionState.Error        => "Connection error",
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _session.StateChanged       -= OnSessionStateChanged;
        _session.NotificationRaised -= OnSessionNotificationRaised;
        _session.Dispose();
    }
}
