using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.App.Views;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Core.Recording;
using LiteConnect.Protocols.Ssh;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// ViewModel for an active SSH terminal tab (Sprints 5 &amp; 6).
/// Wraps <see cref="SshSession"/> and <see cref="ISessionRecorder"/>, binds to <see cref="SshTabView"/>,
/// and handles host key verification prompts, session recording (manual + auto), copy/paste,
/// clear terminal, reconnect, and disconnect.
/// </summary>
public sealed partial class SshSessionViewModel : ObservableObject, ISessionTabViewModel
{
    private readonly SshSession _session;
    private readonly ConnectionInfo _connectionInfo;
    private readonly IConnectionRepository? _repository;
    private readonly IAppEnvironment? _appEnvironment;
    private readonly ISessionRecorder _recorder;
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

    // ── Recording state (Sprint 6) ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRecording))]
    [NotifyPropertyChangedFor(nameof(ToggleRecordingMenuText))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseResumeButtonText))]
    private bool _isRecordingPaused;

    [ObservableProperty]
    private string _recordingStatusText = string.Empty;

    public bool IsNotRecording => !IsRecording;

    public string PauseResumeButtonText => IsRecordingPaused ? "▶ Resume" : "⏸ Pause";

    public string ToggleRecordingMenuText => IsRecording ? "⏹ Stop Recording" : "🔴 Start Recording…";

    /// <summary>Fires whenever recording status or byte size updates so MainViewModel can refresh the status bar.</summary>
    public event EventHandler? RecordingStatusUpdated;

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
    public ISessionRecorder Recorder => _recorder;

    // ── Commands ────────────────────────────────────────────────────────────────

    public IRelayCommand CloseCommand { get; }
    public IRelayCommand? ReconnectCommand => ReconnectSessionCommand;
    public IRelayCommand? DisconnectCommand => DisconnectSessionCommand;
    public IRelayCommand? FullScreenCommand => null;
    public IRelayCommand? PropertiesCommand => OpenPropertiesCommand;
    public IRelayCommand? ToggleRecordingCommand => ToggleSessionRecordingCommand;

    public SshSessionViewModel(
        SshSession session,
        ConnectionInfo connectionInfo,
        IRelayCommand closeTabCommand,
        IConnectionRepository? repository = null,
        IAppEnvironment? appEnvironment = null,
        ISessionRecorder? recorder = null)
    {
        _session        = session ?? throw new ArgumentNullException(nameof(session));
        _connectionInfo = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
        _repository     = repository;
        _appEnvironment = appEnvironment;
        _recorder       = recorder ?? new FileSessionRecorder();

        _displayName = string.IsNullOrWhiteSpace(connectionInfo.Name)
            ? connectionInfo.Hostname
            : connectionInfo.Name;

        SessionControl = (FrameworkElement)_session.GetSessionControl();
        CloseCommand   = new RelayCommand(() => closeTabCommand.Execute(this));

        _session.StateChanged          += OnSessionStateChanged;
        _session.NotificationRaised    += OnSessionNotificationRaised;
        _session.RawDataReceived       += OnSessionRawDataReceived;
        _session.HostKeyPromptCallback  = PromptHostKeyVerificationAsync;
        _recorder.StateChanged         += OnRecorderStateChanged;
    }

    // ── Session lifecycle ───────────────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            if (_connectionInfo.AutoRecord && !_recorder.IsRecording)
            {
                await StartAutoRecordingAsync(ct);
            }

            StatusMessage = $"Connecting to {_connectionInfo.Hostname}:{_connectionInfo.EffectivePort}…";
            await _session.ConnectAsync(_connectionInfo, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
        }
    }

    private async Task StartAutoRecordingAsync(CancellationToken ct = default)
    {
        try
        {
            string filePath = SessionRecordingPathResolver.ResolveFilePath(_connectionInfo, _appEnvironment);
            await _recorder.StartAsync(filePath, _connectionInfo.RecordingFormat, ct);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Auto-record failed to start: {ex.Message}";
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

    // ── Recording Commands (Sprint 6.4) ─────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleSessionRecordingAsync()
    {
        if (_recorder.IsRecording)
            await StopRecordingAsync();
        else
            await StartRecordingAsync();
    }

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (_recorder.IsRecording)
            return;

        string suggestedPath = SessionRecordingPathResolver.ResolveFilePath(_connectionInfo, _appEnvironment);
        string initialDir = Path.GetDirectoryName(suggestedPath)
                            ?? _appEnvironment?.LogDirectory
                            ?? AppContext.BaseDirectory;

        try
        {
            Directory.CreateDirectory(initialDir);
        }
        catch
        {
            // Ignore if directory cannot be pre-created
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = $"Record SSH Session — {DisplayName}",
            InitialDirectory = initialDir,
            FileName         = Path.GetFileName(suggestedPath),
            Filter           = "Plain Text Log (*.log;*.txt)|*.log;*.txt|Timestamped Log (*.log;*.txt)|*.log;*.txt|HTML Color Log (*.html)|*.html",
            FilterIndex      = _connectionInfo.RecordingFormat switch
            {
                RecordingFormat.Timestamped => 2,
                RecordingFormat.Html        => 3,
                _                           => 1
            },
            OverwritePrompt = false // FileSessionRecorder appends if file exists
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            var selectedFormat = dlg.FilterIndex switch
            {
                2 => RecordingFormat.Timestamped,
                3 => RecordingFormat.Html,
                _ => Path.GetExtension(dlg.FileName).Equals(".html", StringComparison.OrdinalIgnoreCase)
                    ? RecordingFormat.Html
                    : RecordingFormat.PlainText
            };

            try
            {
                await _recorder.StartAsync(dlg.FileName, selectedFormat);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not start recording: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!_recorder.IsRecording)
            return;

        string? savedPath = _recorder.CurrentLogPath;
        await _recorder.StopAsync();
        if (!string.IsNullOrEmpty(savedPath))
        {
            StatusMessage = $"Recording saved: {Path.GetFileName(savedPath)}";
        }
    }

    [RelayCommand]
    private void TogglePauseRecording()
    {
        if (!_recorder.IsRecording)
            return;

        if (_recorder.IsPaused)
            _recorder.Resume();
        else
            _recorder.Pause();
    }

    // ── Terminal Utility Commands ───────────────────────────────────────────────

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

            if (_repository is not null && dialog.Result.GroupId != Guid.Empty)
            {
                try
                {
                    await _repository.SaveConnectionAsync(dialog.Result);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save connection properties: {ex.Message}");
                }
            }
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

    // ── Events & Recording Tap (Sprint 6.2) ─────────────────────────────────────

    private void OnSessionRawDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_recorder.IsRecording && !_recorder.IsPaused)
        {
            _recorder.Write(data.Span, DateTimeOffset.Now);
        }
    }

    private void OnRecorderStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        void Apply()
        {
            IsRecording       = e.IsRecording;
            IsRecordingPaused = e.IsPaused;

            if (!e.IsRecording || string.IsNullOrEmpty(e.LogPath))
            {
                RecordingStatusText = string.Empty;
            }
            else
            {
                string fileName = Path.GetFileName(e.LogPath);
                string sizeText = SessionRecordingPathResolver.FormatFileSize(e.BytesWritten);
                RecordingStatusText = e.IsPaused
                    ? $"⏸ Paused — {fileName} ({sizeText})"
                    : $"🔴 Recording — {fileName} ({sizeText})";
            }

            RecordingStatusUpdated?.Invoke(this, EventArgs.Empty);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(Apply);
        else
            Apply();
    }

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
        _session.RawDataReceived    -= OnSessionRawDataReceived;
        _recorder.StateChanged      -= OnRecorderStateChanged;

        _recorder.Dispose();
        _session.Dispose();
    }
}
