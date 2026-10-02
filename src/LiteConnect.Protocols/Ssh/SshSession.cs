using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;
using LiteConnect.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;

// Alias to avoid conflict between LiteConnect.Core.Models.ConnectionInfo and Renci.SshNet.ConnectionInfo
using CoreConnectionInfo = LiteConnect.Core.Models.ConnectionInfo;
using SshNetConnectionInfo = Renci.SshNet.ConnectionInfo;
using SshNetProxyTypes = Renci.SshNet.ProxyTypes;

namespace LiteConnect.Protocols.Ssh;

/// <summary>
/// Decision returned by the user when prompted to verify an unknown or changed SSH host key.
/// </summary>
public enum HostKeyVerificationDecision
{
    Reject,
    AcceptOnce,
    AcceptAndRemember
}

/// <summary>
/// Details presented to the user in the Host Key Verification dialog.
/// </summary>
public sealed record HostKeyVerificationPrompt(
    string Hostname,
    int Port,
    string KeyAlgorithm,
    int KeyBitLength,
    string Sha256Fingerprint,
    bool IsKeyMismatch,
    string? PreviousFingerprint);

/// <summary>
/// Implements <see cref="IProtocolSession"/> for SSH2 connections using <c>SSH.NET</c>
/// and renders interactive VT100/xterm output via <see cref="TerminalControl"/>.
/// Supports password, PEM/OpenSSH/PPK private key, keyboard-interactive authentication,
/// HTTP/SOCKS4/SOCKS5 proxies, SSH jump hosts, local/remote/dynamic port forwarding,
/// and <c>KnownHosts</c> fingerprint verification.
/// </summary>
public sealed class SshSession : IProtocolSession
{
    private readonly IKnownHostsStore? _knownHostsStore;
    private readonly TerminalControl _terminalControl;

    private CoreConnectionInfo? _connectionInfo;
    private SshClient? _sshClient;
    private SshClient? _jumpClient;
    private ForwardedPortLocal? _jumpTunnel;
    private ShellStream? _shellStream;
    private readonly List<ForwardedPort> _activePortForwards = [];

    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private bool _userInitiatedDisconnect;
    private bool _disposed;

    public Guid SessionId { get; } = Guid.NewGuid();
    public string DisplayName { get; private set; } = "SSH Session";
    public SessionState State { get; private set; } = SessionState.Idle;
    public ProtocolType Protocol => ProtocolType.SSH;

    public bool CanReconnect =>
        _connectionInfo is not null &&
        State is SessionState.Disconnected or SessionState.Error;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<SessionNotificationEventArgs>? NotificationRaised;

    /// <summary>
    /// Raw SSH byte stream tap (invoked before/alongside terminal rendering) for Sprint 6 session recording.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? RawDataReceived;

    /// <summary>
    /// Optional callback invoked when a host key is unknown or mismatched.
    /// If null, unknown host keys are accepted for the session and mismatched keys are rejected.
    /// </summary>
    public Func<HostKeyVerificationPrompt, Task<HostKeyVerificationDecision>>? HostKeyPromptCallback { get; set; }

    public TerminalControl Terminal => _terminalControl;

    public SshSession(IKnownHostsStore? knownHostsStore = null)
    {
        _knownHostsStore = knownHostsStore;
        _terminalControl = new TerminalControl();

        _terminalControl.InputReceived   += OnTerminalInputReceived;
        _terminalControl.TerminalResized += OnTerminalResized;
        _terminalControl.TitleChanged    += (_, title) =>
        {
            if (!string.IsNullOrWhiteSpace(title))
                DisplayName = title;
        };
    }

    public object GetSessionControl() => _terminalControl;

    // ── ConnectAsync (5.1 & 5.2) ────────────────────────────────────────────────

    public async Task ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connectionInfo);

        if (State is SessionState.Connecting or SessionState.Connected)
            throw new InvalidOperationException("SSH session is already connecting or connected.");

        _connectionInfo          = connectionInfo;
        _userInitiatedDisconnect = false;
        DisplayName              = string.IsNullOrWhiteSpace(connectionInfo.Name)
            ? connectionInfo.Hostname
            : connectionInfo.Name;

        var sshSettings = connectionInfo.SshSettings ?? new SshConnectionSettings();
        _terminalControl.Dispatcher.Invoke(() => _terminalControl.ApplySettings(sshSettings));

        SetState(SessionState.Connecting);
        CleanupClients();

        try
        {
            string targetHost = connectionInfo.Hostname.Trim();
            int targetPort    = connectionInfo.EffectivePort;
            var proxy         = connectionInfo.ProxySettings;

            string connectHost = targetHost;
            int connectPort    = targetPort;

            // 5.2: SSH Jump Host tunnel (if configured)
            if (proxy is { Type: ProxyType.SshJumpHost } && !string.IsNullOrWhiteSpace(proxy.JumpHostName))
            {
                _terminalControl.FeedText($"\x1b[90mConnecting to jump host {proxy.JumpHostName}:{proxy.JumpHostPort ?? 22}...\x1b[0m\r\n");

                var jumpConnInfo = BuildJumpHostConnectionInfo(proxy, sshSettings);
                _jumpClient = new SshClient(jumpConnInfo);
                AttachHostKeyVerification(_jumpClient, proxy.JumpHostName.Trim(), proxy.JumpHostPort ?? 22);

                await _jumpClient.ConnectAsync(ct);

                _jumpTunnel = new ForwardedPortLocal("127.0.0.1", (uint)0, targetHost, (uint)targetPort);
                _jumpClient.AddForwardedPort(_jumpTunnel);
                _jumpTunnel.Start();

                connectHost = "127.0.0.1";
                connectPort = (int)_jumpTunnel.BoundPort;
            }

            _terminalControl.FeedText($"\x1b[90mConnecting to {targetHost}:{targetPort}...\x1b[0m\r\n");

            var sshConnInfo = BuildTargetSshConnectionInfo(
                connectionInfo,
                sshSettings,
                proxy,
                connectHost,
                connectPort);

            _sshClient = new SshClient(sshConnInfo);

            // Configure Keep-Alive
            bool sendKeepalive = sshSettings.SendKeepalive ?? true;
            int keepaliveSecs  = sshSettings.KeepaliveIntervalSeconds ?? 60;
            if (sendKeepalive && keepaliveSecs > 0)
            {
                _sshClient.KeepAliveInterval = TimeSpan.FromSeconds(keepaliveSecs);
            }

            // 5.1: Host key verification against KnownHosts table
            AttachHostKeyVerification(_sshClient, targetHost, targetPort);

            _sshClient.ErrorOccurred += OnSshClientErrorOccurred;

            await _sshClient.ConnectAsync(ct);

            if (!_sshClient.IsConnected)
            {
                throw new InvalidOperationException("SSH client failed to establish a connection.");
            }

            // 5.2: Start configured Port Forwarding rules (Local / Remote / Dynamic)
            StartPortForwards(sshSettings.PortForwards);

            // Open interactive PTY ShellStream
            string termType = !string.IsNullOrWhiteSpace(sshSettings.TerminalType)
                ? sshSettings.TerminalType
                : "xterm-256color";

            int cols = _terminalControl.Buffer.Columns;
            int rows = _terminalControl.Buffer.Rows;

            _shellStream = _sshClient.CreateShellStream(
                termType,
                (uint)Math.Max(10, cols),
                (uint)Math.Max(4, rows),
                (uint)Math.Max(80, _terminalControl.ActualWidth),
                (uint)Math.Max(40, _terminalControl.ActualHeight),
                bufferSize: 65536);

            SetState(SessionState.Connected);

            // Execute optional post-connect startup command
            if (!string.IsNullOrWhiteSpace(sshSettings.StartupCommand))
            {
                byte[] cmdBytes = _terminalControl.TextEncoding.GetBytes(sshSettings.StartupCommand.Trim() + "\r");
                await _shellStream.WriteAsync(cmdBytes, ct);
                await _shellStream.FlushAsync(ct);
            }

            _readLoopCts  = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadShellLoopAsync(_readLoopCts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _terminalControl.FeedText($"\r\n\x1b[91mConnection failed: {ex.Message}\x1b[0m\r\n");
            RaiseNotification($"SSH connection failed: {ex.Message}", NotificationSeverity.Error);
            CleanupClients();
            SetState(SessionState.Error);
            throw;
        }
    }

    // ── DisconnectAsync ─────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        if (State is SessionState.Disconnected or SessionState.Idle)
            return;

        _userInitiatedDisconnect = true;
        SetState(SessionState.Disconnecting);

        try
        {
            if (_readLoopCts is not null)
            {
                await _readLoopCts.CancelAsync();
            }

            if (_readLoopTask is not null)
            {
                try { await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        }
        finally
        {
            CleanupClients();
            _terminalControl.FeedText("\r\n\x1b[90m[Session disconnected]\x1b[0m\r\n");
            SetState(SessionState.Disconnected);
        }
    }

    // ── Resize (SIGWINCH) ───────────────────────────────────────────────────────

    public void OnResize(int width, int height)
    {
        SendWindowChangeToShell(
            _terminalControl.Buffer.Columns,
            _terminalControl.Buffer.Rows,
            width,
            height);
    }

    private void OnTerminalResized(object? sender, TerminalResizeEventArgs e)
    {
        SendWindowChangeToShell(e.Columns, e.Rows, e.PixelWidth, e.PixelHeight);
    }

    private void SendWindowChangeToShell(int cols, int rows, int pixelWidth, int pixelHeight)
    {
        var stream = _shellStream;
        if (stream is null || State != SessionState.Connected)
            return;

        try
        {
            stream.ChangeWindowSize(
                (uint)Math.Max(2, cols),
                (uint)Math.Max(2, rows),
                (uint)Math.Max(0, pixelWidth),
                (uint)Math.Max(0, pixelHeight));
        }
        catch
        {
            // Ignore resize errors if channel is closing
        }
    }

    // ── Shell Read/Write Loop ───────────────────────────────────────────────────

    private void OnTerminalInputReceived(object? sender, byte[] data)
    {
        var stream = _shellStream;
        if (stream is null || State != SessionState.Connected)
            return;

        try
        {
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }
        catch (Exception ex)
        {
            RaiseNotification($"Write error: {ex.Message}", NotificationSeverity.Warning);
        }
    }

    private async Task ReadShellLoopAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[16384];
        var stream = _shellStream;
        if (stream is null)
            return;

        try
        {
            while (!ct.IsCancellationRequested && _sshClient is { IsConnected: true })
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytesRead <= 0)
                {
                    break;
                }

                // 1. Feed raw recording tap (Sprint 6 ready)
                if (RawDataReceived is not null)
                {
                    byte[] copy = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, copy, 0, bytesRead);
                    RawDataReceived.Invoke(this, copy);
                }

                // 2. Feed terminal emulator
                _terminalControl.FeedBytes(buffer.AsSpan(0, bytesRead));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (!_userInitiatedDisconnect)
            {
                _terminalControl.FeedText($"\r\n\x1b[91m[Connection lost: {ex.Message}]\x1b[0m\r\n");
            }
        }

        if (!_userInitiatedDisconnect && !_disposed)
        {
            await HandleUnexpectedDisconnectAsync();
        }
    }

    private async Task HandleUnexpectedDisconnectAsync()
    {
        CleanupClients();

        var settings = _connectionInfo?.SshSettings;
        bool autoReconnect = settings?.AutoReconnect ?? true;
        int maxAttempts    = settings?.MaxReconnectAttempts ?? 5;
        int intervalSecs   = settings?.ReconnectIntervalSeconds ?? 3;

        if (!autoReconnect || _connectionInfo is null || maxAttempts <= 0)
        {
            _terminalControl.FeedText("\r\n\x1b[90m[Remote host closed the connection]\x1b[0m\r\n");
            SetState(SessionState.Disconnected);
            return;
        }

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (_userInitiatedDisconnect || _disposed)
                return;

            _terminalControl.FeedText($"\r\n\x1b[93m[Reconnecting (attempt {attempt}/{maxAttempts}) in {intervalSecs}s...]\x1b[0m\r\n");
            RaiseNotification($"Connection dropped. Reconnecting ({attempt}/{maxAttempts})…", NotificationSeverity.Warning);

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, intervalSecs)));
            if (_userInitiatedDisconnect || _disposed)
                return;

            try
            {
                SetState(SessionState.Idle);
                await ConnectAsync(_connectionInfo);
                if (State == SessionState.Connected)
                {
                    RaiseNotification("Reconnected successfully.", NotificationSeverity.Info);
                    return;
                }
            }
            catch
            {
                // Continue retry loop
            }
        }

        SetState(SessionState.Disconnected);
    }

    private void OnSshClientErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        if (!_userInitiatedDisconnect)
        {
            RaiseNotification($"SSH error: {e.Exception.Message}", NotificationSeverity.Error);
        }
    }

    // ── SSH.NET ConnectionInfo & Authentication Builder ─────────────────────────

    private static SshNetConnectionInfo BuildTargetSshConnectionInfo(
        CoreConnectionInfo model,
        SshConnectionSettings sshSettings,
        ProxySettings? proxy,
        string connectHost,
        int connectPort)
    {
        string username = !string.IsNullOrWhiteSpace(model.Username)
            ? model.Username.Trim()
            : Environment.UserName;

        var authMethods = BuildAuthenticationMethods(
            username,
            model.Password,
            model.PrivateKeyPath,
            model.KeyPassphrase,
            sshSettings.AuthMethod);

        SshNetConnectionInfo connInfo;

        if (proxy is not null && proxy.Type is ProxyType.Http or ProxyType.Socks4 or ProxyType.Socks5 &&
            !string.IsNullOrWhiteSpace(proxy.Host))
        {
            var sshProxyType = proxy.Type switch
            {
                ProxyType.Http   => SshNetProxyTypes.Http,
                ProxyType.Socks4 => SshNetProxyTypes.Socks4,
                ProxyType.Socks5 => SshNetProxyTypes.Socks5,
                _                => SshNetProxyTypes.None
            };

            int proxyPort = proxy.Port ?? (proxy.Type == ProxyType.Http ? 8080 : 1080);

            connInfo = new SshNetConnectionInfo(
                connectHost,
                connectPort,
                username,
                sshProxyType,
                proxy.Host.Trim(),
                proxyPort,
                proxy.Username ?? string.Empty,
                proxy.Password ?? string.Empty,
                authMethods);
        }
        else
        {
            connInfo = new SshNetConnectionInfo(
                connectHost,
                connectPort,
                username,
                authMethods);
        }

        int timeoutSecs = sshSettings.ConnectTimeoutSeconds ?? 15;
        connInfo.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSecs, 3, 300));

        return connInfo;
    }

    private static SshNetConnectionInfo BuildJumpHostConnectionInfo(
        ProxySettings proxy,
        SshConnectionSettings sshSettings)
    {
        string jumpHost = proxy.JumpHostName!.Trim();
        int jumpPort    = proxy.JumpHostPort ?? 22;
        string jumpUser = !string.IsNullOrWhiteSpace(proxy.JumpHostUsername)
            ? proxy.JumpHostUsername.Trim()
            : Environment.UserName;

        var authMethods = BuildAuthenticationMethods(
            jumpUser,
            proxy.JumpHostPassword,
            proxy.JumpHostPrivateKeyPath,
            proxy.JumpHostKeyPassphrase,
            authMethodPreference: null);

        var connInfo = new SshNetConnectionInfo(jumpHost, jumpPort, jumpUser, authMethods)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(sshSettings.ConnectTimeoutSeconds ?? 15, 3, 300))
        };

        return connInfo;
    }

    private static AuthenticationMethod[] BuildAuthenticationMethods(
        string username,
        string? password,
        string? privateKeyPath,
        string? keyPassphrase,
        string? authMethodPreference)
    {
        var methods = new List<AuthenticationMethod>();

        bool hasKey = !string.IsNullOrWhiteSpace(privateKeyPath) && File.Exists(privateKeyPath);
        bool hasPwd = !string.IsNullOrEmpty(password);

        // 1. Private Key (PEM, OpenSSH, or PuTTY .ppk)
        if (hasKey && authMethodPreference is not "Password")
        {
            var keyFile = !string.IsNullOrEmpty(keyPassphrase)
                ? new PrivateKeyFile(privateKeyPath!, keyPassphrase)
                : new PrivateKeyFile(privateKeyPath!);
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
        }

        // 2. Password + Keyboard-Interactive
        if (hasPwd)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password!));

            var kbdInteractive = new KeyboardInteractiveAuthenticationMethod(username);
            kbdInteractive.AuthenticationPrompt += (_, args) =>
            {
                foreach (var prompt in args.Prompts)
                {
                    prompt.Response = password!;
                }
            };
            methods.Add(kbdInteractive);
        }

        // Fallback if neither password nor valid key file was supplied
        if (methods.Count == 0)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password ?? string.Empty));
        }

        return [.. methods];
    }

    // ── Port Forwarding (5.2) ───────────────────────────────────────────────────

    private void StartPortForwards(List<PortForwardRule>? rules)
    {
        if (rules is null || rules.Count == 0 || _sshClient is null)
            return;

        foreach (var rule in rules.Where(r => r.Enabled && r.BoundPort > 0))
        {
            try
            {
                string bindAddr = string.IsNullOrWhiteSpace(rule.BindAddress) ? "127.0.0.1" : rule.BindAddress.Trim();
                ForwardedPort port = rule.Type switch
                {
                    PortForwardType.Dynamic => new ForwardedPortDynamic(bindAddr, (uint)rule.BoundPort),
                    PortForwardType.Remote  => new ForwardedPortRemote(
                        bindAddr,
                        (uint)rule.BoundPort,
                        string.IsNullOrWhiteSpace(rule.TargetHost) ? "127.0.0.1" : rule.TargetHost.Trim(),
                        (uint)Math.Max(1, rule.TargetPort)),
                    _ => new ForwardedPortLocal(
                        bindAddr,
                        (uint)rule.BoundPort,
                        string.IsNullOrWhiteSpace(rule.TargetHost) ? "127.0.0.1" : rule.TargetHost.Trim(),
                        (uint)Math.Max(1, rule.TargetPort))
                };

                _sshClient.AddForwardedPort(port);
                port.Start();
                _activePortForwards.Add(port);

                _terminalControl.FeedText($"\x1b[90m[Tunnel active: {rule.Summary}]\x1b[0m\r\n");
            }
            catch (Exception ex)
            {
                _terminalControl.FeedText($"\x1b[93m[Tunnel failed ({rule.Summary}): {ex.Message}]\x1b[0m\r\n");
                RaiseNotification($"Port forward failed ({rule.Summary}): {ex.Message}", NotificationSeverity.Warning);
            }
        }
    }

    // ── Host Key Verification against KnownHosts (5.1) ──────────────────────────

    private void AttachHostKeyVerification(SshClient client, string hostname, int port)
    {
        client.HostKeyReceived += (_, e) =>
        {
            try
            {
                byte[] hash = SHA256.HashData(e.HostKey);
                string fingerprint = $"SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}";
                string keyType = e.HostKeyName ?? "ssh-unknown";

                if (_knownHostsStore is not null)
                {
                    var existing = _knownHostsStore
                        .GetKnownHostAsync(hostname, port, keyType)
                        .GetAwaiter()
                        .GetResult();

                    if (existing is not null && existing.Trusted &&
                        string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        // Known & trusted host key — update LastSeen timestamp silently
                        _knownHostsStore
                            .SaveKnownHostAsync(hostname, port, keyType, fingerprint, trusted: true)
                            .GetAwaiter()
                            .GetResult();
                        e.CanTrust = true;
                        return;
                    }

                    bool isMismatch = existing is not null &&
                        !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal);

                    if (HostKeyPromptCallback is not null)
                    {
                        var prompt = new HostKeyVerificationPrompt(
                            hostname,
                            port,
                            keyType,
                            e.KeyLength,
                            fingerprint,
                            isMismatch,
                            existing?.Fingerprint);

                        var decision = _terminalControl.Dispatcher.Invoke(
                            () => HostKeyPromptCallback(prompt).GetAwaiter().GetResult(),
                            DispatcherPriority.Normal);

                        if (decision == HostKeyVerificationDecision.Reject)
                        {
                            e.CanTrust = false;
                            return;
                        }

                        if (decision == HostKeyVerificationDecision.AcceptAndRemember)
                        {
                            _knownHostsStore
                                .SaveKnownHostAsync(hostname, port, keyType, fingerprint, trusted: true)
                                .GetAwaiter()
                                .GetResult();
                        }

                        e.CanTrust = true;
                        return;
                    }

                    // Default non-interactive fallback: reject mismatched keys, trust-on-first-use (TOFU) for new keys
                    if (isMismatch)
                    {
                        e.CanTrust = false;
                        return;
                    }

                    _knownHostsStore
                        .SaveKnownHostAsync(hostname, port, keyType, fingerprint, trusted: true)
                        .GetAwaiter()
                        .GetResult();
                }

                e.CanTrust = true;
            }
            catch
            {
                e.CanTrust = false;
            }
        };
    }

    // ── Cleanup & Helpers ───────────────────────────────────────────────────────

    private void CleanupClients()
    {
        foreach (var pf in _activePortForwards)
        {
            try { pf.Stop(); } catch { }
            try { pf.Dispose(); } catch { }
        }
        _activePortForwards.Clear();

        if (_shellStream is not null)
        {
            try { _shellStream.Dispose(); } catch { }
            _shellStream = null;
        }

        if (_sshClient is not null)
        {
            _sshClient.ErrorOccurred -= OnSshClientErrorOccurred;
            try { if (_sshClient.IsConnected) _sshClient.Disconnect(); } catch { }
            try { _sshClient.Dispose(); } catch { }
            _sshClient = null;
        }

        if (_jumpTunnel is not null)
        {
            try { _jumpTunnel.Stop(); } catch { }
            try { _jumpTunnel.Dispose(); } catch { }
            _jumpTunnel = null;
        }

        if (_jumpClient is not null)
        {
            try { if (_jumpClient.IsConnected) _jumpClient.Disconnect(); } catch { }
            try { _jumpClient.Dispose(); } catch { }
            _jumpClient = null;
        }
    }

    private void SetState(SessionState next)
    {
        if (State == next) return;
        var prev = State;
        State = next;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(prev, next));
    }

    private void RaiseNotification(string message, NotificationSeverity severity)
    {
        NotificationRaised?.Invoke(this, new SessionNotificationEventArgs(message, severity));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _userInitiatedDisconnect = true;

        try { _readLoopCts?.Cancel(); } catch { }
        try { _readLoopCts?.Dispose(); } catch { }
        CleanupClients();
    }
}
