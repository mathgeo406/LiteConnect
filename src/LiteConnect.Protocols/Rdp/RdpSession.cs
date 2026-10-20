using System.ComponentModel;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;

namespace LiteConnect.Protocols.Rdp;

/// <summary>
/// Implements an RDP remote desktop session using the Windows built-in
/// <see cref="AxMsRdpClient9NotSafeForScripting"/> ActiveX control hosted inside
/// a WPF <see cref="WindowsFormsHost"/>.
/// </summary>
public sealed class RdpSession : IProtocolSession
{
    private WindowsFormsHost? _host;
    private AxMsRdpClient9NotSafeForScripting? _rdpClient;
    private ConnectionInfo? _lastConnectionInfo;
    private SessionState _state = SessionState.Disconnected;
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempts;
    private bool _userInitiatedDisconnect;
    private bool _disposed;
    private bool _nlaRetryPending;

    public Guid SessionId { get; } = Guid.NewGuid();

    public string DisplayName { get; private set; } = "RDP Session";

    /// <summary>The most recent disconnect or error message, if any.</summary>
    public string? LastDisconnectReason { get; private set; }

    /// <summary>Whether the RDP control is currently in full-screen mode.</summary>
    public bool IsFullScreen { get; private set; }

    /// <summary>The <see cref="ConnectionInfo"/> associated with this session.</summary>
    public ConnectionInfo? ConnectionInfo => _lastConnectionInfo;

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            var previous = _state;
            _state = value;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(previous, value));
        }
    }

    public ProtocolType Protocol => ProtocolType.RDP;

    public bool CanReconnect =>
        _lastConnectionInfo is not null &&
        State is SessionState.Disconnected or SessionState.Error;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionNotificationEventArgs>? NotificationRaised;
    public event EventHandler<bool>? FullScreenChanged;

    // ── Control hosting ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public object GetSessionControl()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureHostCreated();
        return _host!;
    }

    private void EnsureHostCreated()
    {
        if (_host is not null && _rdpClient is not null)
            return;

        _rdpClient = new AxMsRdpClient9NotSafeForScripting
        {
            Dock = DockStyle.Fill
        };

        // Wire ActiveX events (Sprint 3.4)
        _rdpClient.Connecting             += OnAxConnecting;
        _rdpClient.ConnectedEvent         += OnAxConnected;
        _rdpClient.Disconnected           += OnAxDisconnected;
        _rdpClient.FatalError             += OnAxFatalError;
        _rdpClient.RequestGoFullScreen    += OnAxRequestGoFullScreen;
        _rdpClient.RequestLeaveFullScreen += OnAxRequestLeaveFullScreen;

        _host = new WindowsFormsHost();

        ((ISupportInitialize)_rdpClient).BeginInit();
        _host.Child = _rdpClient;
        ((ISupportInitialize)_rdpClient).EndInit();

        // Force handle creation so the underlying COM OCX is instantiated immediately
        _rdpClient.CreateControl();
    }

    // ── Connect / Disconnect / Reconnect ────────────────────────────────────────

    /// <inheritdoc />
    public Task ConnectAsync(ConnectionInfo connectionInfo, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connectionInfo);

        if (State is SessionState.Connected or SessionState.Connecting)
            throw new InvalidOperationException("RDP session is already connected or connecting.");

        ct.ThrowIfCancellationRequested();

        CancelPendingReconnect();
        _userInitiatedDisconnect = false;
        _reconnectAttempts       = 0;
        LastDisconnectReason     = null;
        _lastConnectionInfo      = connectionInfo;

        DisplayName = string.IsNullOrWhiteSpace(connectionInfo.Name)
            ? connectionInfo.Hostname
            : connectionInfo.Name;

        EnsureHostCreated();

        try
        {
            State = SessionState.Connecting;

            ApplyConnectionSettings(_rdpClient!, connectionInfo);

            // Enable NLA fallback retry: if the first Connect fires an immediate
            // disconnect (reason 1/4), OnAxDisconnected will retry with CredSSP off.
            var rdpSettings = connectionInfo.RdpSettings ?? new RdpConnectionSettings();
            bool useNla = rdpSettings.UseNla ?? true;
            _nlaRetryPending = useNla;

            _rdpClient!.Connect();

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            LastDisconnectReason = ex.Message;
            State = SessionState.Error;
            RaiseNotification($"RDP connection failed: {ex.Message}", NotificationSeverity.Error);
            throw;
        }
    }

    /// <summary>
    /// Reconnects using the last <see cref="ConnectionInfo"/>.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        if (_lastConnectionInfo is null)
            throw new InvalidOperationException("Cannot reconnect: no previous connection info.");

        if (State is SessionState.Connected or SessionState.Connecting)
            await DisconnectAsync();

        await ConnectAsync(_lastConnectionInfo, ct);
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        if (_disposed || _rdpClient is null)
            return Task.CompletedTask;

        CancelPendingReconnect();
        _userInitiatedDisconnect = true;

        try
        {
            if (_rdpClient.IsHandleCreated && _rdpClient.Connected != 0)
            {
                _rdpClient.Disconnect();
            }
        }
        catch (Exception ex)
        {
            RaiseNotification($"Error while disconnecting RDP session: {ex.Message}", NotificationSeverity.Warning);
        }
        finally
        {
            LastDisconnectReason = "Disconnected by user.";
            State = SessionState.Disconnected;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enters or leaves RDP full-screen mode.
    /// </summary>
    public void SetFullScreen(bool fullScreen)
    {
        if (_disposed || _rdpClient is null || !_rdpClient.IsHandleCreated)
            return;

        try
        {
            _rdpClient.FullScreenTitle = DisplayName;
            _rdpClient.FullScreen      = fullScreen;
            IsFullScreen               = fullScreen;
            FullScreenChanged?.Invoke(this, fullScreen);
        }
        catch (Exception ex)
        {
            RaiseNotification($"Failed to toggle full screen: {ex.Message}", NotificationSeverity.Warning);
        }
    }

    // ── ActiveX Event Handlers (Sprint 3.4) ─────────────────────────────────────

    private void OnAxConnecting(object? sender, EventArgs e)
    {
        State = SessionState.Connecting;
    }

    private void OnAxConnected(object? sender, EventArgs e)
    {
        _reconnectAttempts   = 0;
        LastDisconnectReason = null;
        State                = SessionState.Connected;

        // In Auto or dynamic mode, adjust remote desktop resolution to match container size on connect.
        // Schedule after brief delay to allow WPF layout to measure the host container.
        if (_rdpClient is not null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (_disposed || _rdpClient is null || State != SessionState.Connected)
                        return;

                    try
                    {
                        var rdpSettings = _lastConnectionInfo?.RdpSettings;
                        string mode = rdpSettings?.ResolutionMode ?? "Auto";
                        if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                            _host is { ActualWidth: >= 200, ActualHeight: >= 200 })
                        {
                            uint scale = (uint)(rdpSettings?.DpiScaling is > 0 ? rdpSettings.DpiScaling.Value : 100);
                            _rdpClient.UpdateSessionDisplaySettings(
                                (uint)_host.ActualWidth,
                                (uint)_host.ActualHeight,
                                scale);
                        }
                    }
                    catch { /* Best effort if server does not support dynamic resolution changes */ }
                });
            });
        }
    }

    private void OnAxDisconnected(object? sender, int discReason)
    {
        if (_disposed) return;

        // NLA/CredSSP fallback: if we get an immediate disconnect with reason 1 or 4
        // ("An internal error has occurred") and haven't retried yet, retry once
        // with CredSSP disabled and AuthenticationLevel=0 for non-NLA servers (e.g. xrdp).
        if (!_userInitiatedDisconnect && _nlaRetryPending && discReason is 1 or 4 &&
            _rdpClient is not null && _lastConnectionInfo is not null)
        {
            _nlaRetryPending = false;
            _ = ScheduleNlaFallbackRetryAsync();
            return;
        }
        _nlaRetryPending = false;

        string reason = DescribeDisconnectReason(discReason);
        LastDisconnectReason = reason;

        // Check whether we should auto-reconnect (unexpected drop, not user-initiated or clean logoff)
        bool isNormalClose = _userInitiatedDisconnect || discReason is 0 or 1 or 2 or 3;
        var rdp = _lastConnectionInfo?.RdpSettings;
        bool autoReconnect = rdp?.AutoReconnect ?? true;
        int maxAttempts    = rdp?.MaxReconnectAttempts ?? 5;
        int intervalSec    = rdp?.ReconnectIntervalSeconds ?? 3;

        if (!isNormalClose && autoReconnect && _reconnectAttempts < maxAttempts)
        {
            _reconnectAttempts++;
            State = SessionState.Connecting;
            RaiseNotification(
                $"Connection lost ({reason}). Reconnecting (attempt {_reconnectAttempts}/{maxAttempts})…",
                NotificationSeverity.Warning);

            _ = ScheduleAutoReconnectAsync(intervalSec);
            return;
        }

        State = SessionState.Disconnected;
        if (!isNormalClose)
        {
            RaiseNotification($"Disconnected: {reason}", NotificationSeverity.Warning);
        }
    }

    private void OnAxFatalError(object? sender, int errorCode)
    {
        if (_disposed) return;

        CancelPendingReconnect();
        LastDisconnectReason = $"Fatal RDP error (code {errorCode}).";
        State                = SessionState.Error;
        RaiseNotification(LastDisconnectReason, NotificationSeverity.Error);
    }

    private void OnAxRequestGoFullScreen(object? sender, EventArgs e)
    {
        IsFullScreen = true;
        FullScreenChanged?.Invoke(this, true);
    }

    private void OnAxRequestLeaveFullScreen(object? sender, EventArgs e)
    {
        IsFullScreen = false;
        FullScreenChanged?.Invoke(this, false);
    }

    private async Task ScheduleAutoReconnectAsync(int delaySeconds)
    {
        CancelPendingReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delaySeconds)), cts.Token);
            if (_disposed || cts.IsCancellationRequested || _rdpClient is null || _lastConnectionInfo is null)
                return;

            ApplyConnectionSettings(_rdpClient, _lastConnectionInfo);
            _rdpClient.Connect();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by user disconnect or disposal
        }
        catch (Exception ex)
        {
            LastDisconnectReason = $"Auto-reconnect failed: {ex.Message}";
            State                = SessionState.Error;
            RaiseNotification(LastDisconnectReason, NotificationSeverity.Error);
        }
    }

    private async Task ScheduleNlaFallbackRetryAsync()
    {
        try
        {
            // Brief delay to let the ActiveX control fully settle after disconnect
            await Task.Delay(150);
            if (_disposed || _rdpClient is null || _lastConnectionInfo is null)
                return;

            RaiseNotification(
                "NLA/CredSSP negotiation failed. Retrying without NLA…",
                NotificationSeverity.Warning);

            // Re-apply settings with CredSSP disabled
            ApplyConnectionSettings(_rdpClient, _lastConnectionInfo);
            var adv = _rdpClient.AdvancedSettings;
            adv.EnableCredSspSupport = false;
            adv.AuthenticationLevel  = 0u;

            _rdpClient.Connect();
        }
        catch (Exception ex)
        {
            LastDisconnectReason = $"NLA fallback retry failed: {ex.Message}";
            State                = SessionState.Error;
            RaiseNotification(LastDisconnectReason, NotificationSeverity.Error);
        }
    }

    private void CancelPendingReconnect()
    {
        if (_reconnectCts is not null)
        {
            try { _reconnectCts.Cancel(); } catch { }
            _reconnectCts.Dispose();
            _reconnectCts = null;
        }
    }

    private string DescribeDisconnectReason(int discReason)
    {
        if (_rdpClient is not null)
        {
            uint ext = _rdpClient.ExtendedDisconnectReason;
            string desc = _rdpClient.GetErrorDescription((uint)discReason, ext);
            if (!string.IsNullOrWhiteSpace(desc))
                return desc.Trim();
        }

        return discReason switch
        {
            0 => "No information available.",
            1 => "Local disconnection.",
            2 => "Remote disconnection by user.",
            3 => "Remote disconnection by server.",
            260 => "DNS name lookup failure.",
            262 => "Out of memory.",
            264 => "Connection timed out.",
            516 => "Windows Sockets connect failed.",
            518 => "Out of memory.",
            520 => "Host not found error.",
            772 => "Windows Sockets send call failed.",
            1028 => "Windows Sockets recv call failed.",
            1286 => "Invalid security data.",
            1540 => "GetHostByName call failed.",
            1796 => "Timeout occurred.",
            2052 => "Bad IP address specified.",
            2308 => "Socket closed.",
            2566 => "Internal security error.",
            2822 => "Encryption error.",
            3078 => "Decryption error.",
            _ => $"Disconnected (code {discReason})."
        };
    }

    // ── Settings mapping (3.1, 3.2, 3.3, 3.6) ───────────────────────────────────

    /// <summary>
    /// Maps <see cref="ConnectionInfo"/> and <see cref="RdpConnectionSettings"/> onto the
    /// <see cref="AxMsRdpClient9NotSafeForScripting"/> ActiveX control properties.
    /// </summary>
    internal void ApplyConnectionSettings(AxMsRdpClient9NotSafeForScripting client, ConnectionInfo info)
    {
        var rdp = info.RdpSettings ?? new RdpConnectionSettings();
        var adv = client.AdvancedSettings;
        var sec = client.SecuredSettings;

        // 1. Server, Port, Username, Domain (3.1)
        client.Server = info.Hostname;
        adv.RDPPort   = info.EffectivePort;

        if (!string.IsNullOrWhiteSpace(info.Username))
            client.UserName = info.Username;

        if (!string.IsNullOrWhiteSpace(info.Domain))
            client.Domain = info.Domain;

        // 2. ClearTextPassword via IMsTscNonScriptable (3.1)
        if (!string.IsNullOrEmpty(info.Password))
            client.SetClearTextPassword(info.Password);

        // 3. Color depth & Resolution (3.1 & 3.6)
        client.ColorDepth = NormalizeColorDepth(rdp.ColorDepth ?? 32);

        string resMode = rdp.ResolutionMode ?? "Auto";
        if (resMode.Equals("FullScreen", StringComparison.OrdinalIgnoreCase))
        {
            var screen = Screen.PrimaryScreen?.Bounds;
            if (screen is { Width: > 0, Height: > 0 })
            {
                client.DesktopWidth  = screen.Value.Width;
                client.DesktopHeight = screen.Value.Height;
            }
            client.FullScreenTitle = DisplayName;
            client.FullScreen      = true;
        }
        else if (resMode.Equals("MatchMonitor", StringComparison.OrdinalIgnoreCase))
        {
            // Set initial resolution to the monitor's bounds
            var screen = Screen.PrimaryScreen?.Bounds;
            if (screen is { Width: > 0, Height: > 0 })
            {
                client.DesktopWidth  = screen.Value.Width;
                client.DesktopHeight = screen.Value.Height;
            }
        }
        else if (rdp.DesktopWidth is > 0 && rdp.DesktopHeight is > 0)
        {
            client.DesktopWidth  = rdp.DesktopWidth.Value;
            client.DesktopHeight = rdp.DesktopHeight.Value;
        }
        else if (_host is { ActualWidth: >= 200, ActualHeight: >= 200 })
        {
            client.DesktopWidth  = (int)_host.ActualWidth;
            client.DesktopHeight = (int)_host.ActualHeight;
        }
        else
        {
            // In Auto mode: default initial connection size to the full primary monitor resolution
            // so remote desktop has ample space even before dynamic update kicks in
            var screen = Screen.PrimaryScreen?.Bounds;
            client.DesktopWidth  = screen is { Width: > 0 } ? screen.Value.Width : 1920;
            client.DesktopHeight = screen is { Height: > 0 } ? screen.Value.Height : 1080;
        }

        // 4. Smart sizing (3.1)
        adv.SmartSizing = rdp.SmartSizing ?? true;

        // 5. Security: NLA, AuthenticationLevel, EnableCredSspSupport (3.1 & 3.6)
        bool useNla       = rdp.UseNla ?? true;
        bool enableCredSsp = rdp.EnableCredSsp ?? useNla;
        uint authLevel    = rdp.AuthenticationLevel.HasValue
            ? (uint)Math.Clamp(rdp.AuthenticationLevel.Value, 0, 2)
            : (useNla ? 2u : 0u);

        adv.EnableCredSspSupport = enableCredSsp;
        adv.AuthenticationLevel  = authLevel;

        // 6. Local resource redirection (3.3 & 3.6)
        ApplyResourceRedirection(client, adv, sec, rdp);

        // 7. Experience / Performance settings (3.6)
        ApplyExperienceSettings(adv, rdp);

        // 8. Advanced settings: Console, Startup Program, Timeouts, AutoReconnect (3.6)
        ApplyAdvancedSettings(adv, sec, rdp);

        // 9. RD Gateway configuration (3.2 & 3.6)
        ApplyGatewaySettings(client, rdp);
    }

    /// <summary>
    /// Configures local resource redirection (Clipboard, Drives, Printers, Audio, Smart Cards, Keyboard, USB) (Sprint 3.3).
    /// </summary>
    internal static void ApplyResourceRedirection(
        AxMsRdpClient9NotSafeForScripting client,
        RdpAdvancedSettingsWrapper adv,
        RdpSecuredSettingsWrapper sec,
        RdpConnectionSettings rdp)
    {
        // Clipboard (default: true)
        adv.RedirectClipboard = rdp.RedirectClipboard ?? true;

        // Printers (default: false)
        adv.RedirectPrinters = rdp.RedirectPrinters ?? false;

        // Smart Cards (default: false)
        adv.RedirectSmartCards = rdp.RedirectSmartCards ?? false;

        // Drives & DriveCollection (default: false)
        client.ConfigureDriveCollection(rdp.RedirectDrives ?? false, rdp.RedirectedDriveLetters);

        // Audio playback: 0 = Play on this computer (Local), 1 = Play on remote, 2 = Do not play
        int audioMode = rdp.AudioRedirectionMode ?? (rdp.RedirectAudio == false ? 2 : 0);
        adv.AudioRedirectionMode = (uint)Math.Clamp(audioMode, 0, 2);
        sec.AudioRedirectionMode = Math.Clamp(audioMode, 0, 2);

        // Audio recording: 0 = Do not record, 1 = Record from this computer
        adv.AudioCaptureRedirectionMode = (rdp.AudioCaptureRedirectionMode ?? 0) == 1;

        // Keyboard shortcuts: 0 = Local, 1 = Remote, 2 = Full screen only (default)
        sec.KeyboardHookMode = Math.Clamp(rdp.KeyboardHookMode ?? 2, 0, 2);

        // USB / RemoteFX
        if (rdp.RedirectUsb.HasValue)
            adv.RedirectDirectX = rdp.RedirectUsb.Value;
    }

    /// <summary>
    /// Configures experience and performance settings (Connection Speed, Wallpaper, Font Smoothing, Composition, Visual Styles, Bitmap Caching) (Sprint 3.6).
    /// </summary>
    internal static void ApplyExperienceSettings(RdpAdvancedSettingsWrapper adv, RdpConnectionSettings rdp)
    {
        // NetworkConnectionType: 1=Modem, 2=BroadbandLow, 4=BroadbandHigh, 6=LAN, 7=Auto
        uint connType = (rdp.ConnectionSpeed ?? "Auto") switch
        {
            "Modem"         => 1u,
            "BroadbandLow"  => 2u,
            "BroadbandHigh" => 4u,
            "LAN"           => 6u,
            _               => 7u
        };
        adv.NetworkConnectionType = connType;

        int perfFlags = 0;

        if (rdp.DesktopBackground == false)
            perfFlags |= RdpGatewayConstants.PerfDisableWallpaper;

        if (rdp.FontSmoothing == true)
            perfFlags |= RdpGatewayConstants.PerfEnableFontSmoothing;

        if (rdp.DesktopComposition == true)
            perfFlags |= RdpGatewayConstants.PerfEnableDesktopComposition;

        if (rdp.VisualStyles == false)
            perfFlags |= RdpGatewayConstants.PerfDisableTheming;

        if (perfFlags != 0)
            adv.PerformanceFlags = perfFlags;

        // Persistent bitmap caching (default: true)
        adv.BitmapPersistence = (rdp.PersistentBitmapCaching ?? true) ? 1 : 0;
    }

    /// <summary>
    /// Configures advanced settings (Console session, Startup program, Load balancing, Timeouts, Auto-reconnect) (Sprint 3.6).
    /// </summary>
    internal static void ApplyAdvancedSettings(
        RdpAdvancedSettingsWrapper adv,
        RdpSecuredSettingsWrapper sec,
        RdpConnectionSettings rdp)
    {
        adv.ConnectToAdministerServer = rdp.ConsoleSession ?? false;

        if (!string.IsNullOrWhiteSpace(rdp.StartProgram))
            sec.StartProgram = rdp.StartProgram;

        if (!string.IsNullOrWhiteSpace(rdp.WorkDir))
            sec.WorkDir = rdp.WorkDir;

        if (!string.IsNullOrWhiteSpace(rdp.LoadBalancingInfo))
            adv.LoadBalanceInfo = rdp.LoadBalancingInfo;

        if (rdp.ConnectTimeoutSeconds is > 0)
            adv.SingleConnectionTimeout = rdp.ConnectTimeoutSeconds.Value;

        bool autoReconnect = rdp.AutoReconnect ?? true;
        adv.EnableAutoReconnect  = autoReconnect;
        adv.MaxReconnectAttempts = rdp.MaxReconnectAttempts ?? 20;
    }

    /// <summary>
    /// Configures RD Gateway transport settings (<c>TransportSettings2</c>) on the ActiveX control (Sprint 3.2 &amp; 3.6).
    /// </summary>
    internal static void ApplyGatewaySettings(AxMsRdpClient9NotSafeForScripting client, RdpConnectionSettings rdp)
    {
        var transport = client.TransportSettings;

        bool gatewayEnabled = (rdp.UseGateway ?? !string.IsNullOrWhiteSpace(rdp.GatewayHostname))
                              && !string.IsNullOrWhiteSpace(rdp.GatewayHostname);

        if (!gatewayEnabled)
        {
            transport.GatewayUsageMethod = RdpGatewayConstants.ProxyModeNoneDirect;
            return;
        }

        // Include custom port if specified and non-default (default HTTPS gateway port is 443)
        string gatewayHost = rdp.GatewayPort is > 0 and not 443
            ? $"{rdp.GatewayHostname!.Trim()}:{rdp.GatewayPort.Value}"
            : rdp.GatewayHostname!.Trim();

        transport.GatewayHostname           = gatewayHost;
        // Bypass local addresses (ProxyModeDetect = 2) vs always direct through gateway (ProxyModeDirect = 1)
        transport.GatewayUsageMethod        = (rdp.GatewayBypassLocal ?? true)
            ? RdpGatewayConstants.ProxyModeDetect
            : RdpGatewayConstants.ProxyModeDirect;
        transport.GatewayProfileUsageMethod = RdpGatewayConstants.ProfileModeExplicit;

        transport.GatewayCredsSource = string.Equals(rdp.GatewayAuthMethod, "SmartCard", StringComparison.OrdinalIgnoreCase)
            ? RdpGatewayConstants.CredsSourceSmartCard
            : RdpGatewayConstants.CredsSourceUserPass;

        bool hasSeparateCredentials =
            !string.IsNullOrWhiteSpace(rdp.GatewayUsername) ||
            !string.IsNullOrEmpty(rdp.GatewayPassword) ||
            !string.IsNullOrWhiteSpace(rdp.GatewayDomain);

        if (hasSeparateCredentials)
        {
            transport.GatewayCredSharing = 0u;

            if (!string.IsNullOrWhiteSpace(rdp.GatewayUsername))
                transport.GatewayUsername = rdp.GatewayUsername;

            if (!string.IsNullOrWhiteSpace(rdp.GatewayDomain))
                transport.GatewayDomain = rdp.GatewayDomain;

            if (!string.IsNullOrEmpty(rdp.GatewayPassword))
                transport.GatewayPassword = rdp.GatewayPassword;
        }
        else
        {
            // Share target server credentials with the RD Gateway
            transport.GatewayCredSharing = 1u;
        }
    }

    private static int NormalizeColorDepth(int depth) => depth switch
    {
        8 or 15 or 16 or 24 or 32 => depth,
        _ => 32
    };

    // ── Resize (3.5) ────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void OnResize(int width, int height)
    {
        if (_disposed || _rdpClient is null || width <= 0 || height <= 0)
            return;

        try
        {
            if (State == SessionState.Connected)
            {
                var rdpSettings = _lastConnectionInfo?.RdpSettings;
                string mode = rdpSettings?.ResolutionMode ?? "Auto";
                // In Auto mode, dynamically adapt remote desktop resolution to the tab viewport
                if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    uint scale = (uint)(rdpSettings?.DpiScaling is > 0 ? rdpSettings.DpiScaling.Value : 100);
                    _rdpClient.UpdateSessionDisplaySettings((uint)width, (uint)height, scale);
                }
            }
            else if (State == SessionState.Disconnected)
            {
                _rdpClient.DesktopWidth  = width;
                _rdpClient.DesktopHeight = height;
            }
        }
        catch
        {
            // Ignore resize errors when OCX is transitioning states
        }
    }

    private void RaiseNotification(string message, NotificationSeverity severity)
        => NotificationRaised?.Invoke(this, new SessionNotificationEventArgs(message, severity));

    // ── IDisposable ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelPendingReconnect();

        if (_rdpClient is not null)
        {
            _rdpClient.Connecting             -= OnAxConnecting;
            _rdpClient.ConnectedEvent         -= OnAxConnected;
            _rdpClient.Disconnected           -= OnAxDisconnected;
            _rdpClient.FatalError             -= OnAxFatalError;
            _rdpClient.RequestGoFullScreen    -= OnAxRequestGoFullScreen;
            _rdpClient.RequestLeaveFullScreen -= OnAxRequestLeaveFullScreen;

            try
            {
                if (_rdpClient.IsHandleCreated && _rdpClient.Connected != 0)
                    _rdpClient.Disconnect();
            }
            catch { /* best-effort teardown */ }

            _rdpClient.Dispose();
            _rdpClient = null;
        }

        _host?.Dispose();
        _host = null;
    }
}
