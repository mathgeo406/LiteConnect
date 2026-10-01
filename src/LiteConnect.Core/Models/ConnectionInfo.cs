namespace LiteConnect.Core.Models;

/// <summary>
/// A single remote connection entry (SSH, RDP, or VNC).
/// Protocol-specific settings are stored as nullable sub-objects
/// and serialized to JSON blobs in the database.
/// </summary>
public class ConnectionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }

    public string Name { get; set; } = string.Empty;
    public ProtocolType Protocol { get; set; }
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Port override; null means use the protocol default (22/3389/5900).</summary>
    public int? Port { get; set; }

    public int SortOrder { get; set; }

    // ── Credential hints (encrypted by SQLCipher at DB level) ──────────────────
    /// <summary>Null = inherit from group or prompt on connect.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }       // RDP
    public string? PrivateKeyPath { get; set; }  // SSH
    public string? KeyPassphrase { get; set; }   // SSH

    // ── Protocol-specific settings (null = not configured, use global defaults) ─
    public SshConnectionSettings? SshSettings { get; set; }
    public RdpConnectionSettings? RdpSettings { get; set; }
    public VncConnectionSettings? VncSettings { get; set; }
    public ProxySettings? ProxySettings { get; set; }

    // ── Recording ───────────────────────────────────────────────────────────────
    public bool AutoRecord { get; set; }
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.PlainText;

    /// <summary>Null = use the global recording directory setting.</summary>
    public string? RecordingDirectory { get; set; }

    /// <summary>Null = use the global file-pattern setting.</summary>
    public string? RecordingFilePattern { get; set; }

    // ── Metadata ────────────────────────────────────────────────────────────────
    public DateTimeOffset? LastConnected { get; set; }
    public int TimesConnected { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Effective port number (uses protocol default if <see cref="Port"/> is null).</summary>
    public int EffectivePort => Port ?? Protocol switch
    {
        ProtocolType.SSH => 22,
        ProtocolType.RDP => 3389,
        ProtocolType.VNC => 5900,
        _ => 0
    };

    public override string ToString() => $"{Name} ({Protocol}:{Hostname}:{EffectivePort})";
}

// ── Protocol-specific settings records ────────────────────────────────────────

/// <summary>SSH-specific connection settings. All fields nullable — null = global default.</summary>
public sealed class SshConnectionSettings
{
    public string? TerminalType { get; set; }      // e.g. "xterm-256color"
    public string? Encoding { get; set; }           // e.g. "UTF-8"
    public int? ScrollbackLines { get; set; }
    public bool? Compression { get; set; }
    public int? KeepaliveIntervalSeconds { get; set; }
    public int? ConnectTimeoutSeconds { get; set; }
    public string? PreferredCiphers { get; set; }
    public string? PreferredMacs { get; set; }
    public string? PreferredHostKeyAlgorithms { get; set; }
}

/// <summary>RDP-specific connection settings. All fields nullable — null = global default.</summary>
public sealed class RdpConnectionSettings
{
    // ── Display ─────────────────────────────────────────────────────────────────
    public string? ResolutionMode { get; set; }    // "Auto", "MatchMonitor", "Custom", "FullScreen"
    public int? DesktopWidth { get; set; }
    public int? DesktopHeight { get; set; }
    public int? ColorDepth { get; set; }           // 8, 15, 16, 24, 32
    public bool? SmartSizing { get; set; }
    public int? DpiScaling { get; set; }           // null/0 = Match local, 100, 125, 150, 200

    // ── RD Gateway ──────────────────────────────────────────────────────────────
    public bool? UseGateway { get; set; }
    public string? GatewayHostname { get; set; }
    public int? GatewayPort { get; set; }
    public string? GatewayUsername { get; set; }
    public string? GatewayPassword { get; set; }
    public string? GatewayDomain { get; set; }
    public string? GatewayAuthMethod { get; set; } // "Password", "SmartCard"
    public bool? GatewayBypassLocal { get; set; }

    // ── Local Resources ─────────────────────────────────────────────────────────
    public bool? RedirectClipboard { get; set; }
    public bool? RedirectDrives { get; set; }
    public string? RedirectedDriveLetters { get; set; } // null/"ALL" = all drives, or "C,D"
    public bool? RedirectPrinters { get; set; }
    public bool? RedirectAudio { get; set; }
    public int? AudioRedirectionMode { get; set; }      // 0 = Local, 1 = Remote, 2 = Do not play
    public int? AudioCaptureRedirectionMode { get; set; } // 0 = Do not record, 1 = Record from this PC
    public int? KeyboardHookMode { get; set; }          // 0 = Local, 1 = Remote, 2 = Full screen only
    public bool? RedirectSmartCards { get; set; }
    public bool? RedirectUsb { get; set; }

    // ── Experience / Performance ────────────────────────────────────────────────
    public string? ConnectionSpeed { get; set; }   // "Auto", "Modem", "BroadbandLow", "BroadbandHigh", "LAN"
    public bool? DesktopBackground { get; set; }
    public bool? FontSmoothing { get; set; }
    public bool? DesktopComposition { get; set; }
    public bool? VisualStyles { get; set; }
    public bool? PersistentBitmapCaching { get; set; }

    // ── Security & Advanced ─────────────────────────────────────────────────────
    public bool? UseNla { get; set; }
    public int? AuthenticationLevel { get; set; }  // 0 = No auth, 1 = Require, 2 = Warn (NLA default)
    public bool? EnableCredSsp { get; set; }
    public bool? ConsoleSession { get; set; }
    public string? StartProgram { get; set; }
    public string? WorkDir { get; set; }
    public string? LoadBalancingInfo { get; set; }
    public int? ConnectTimeoutSeconds { get; set; }
    public bool? AutoReconnect { get; set; }
    public int? MaxReconnectAttempts { get; set; }
    public int? ReconnectIntervalSeconds { get; set; }
}

/// <summary>VNC-specific connection settings. All fields nullable — null = global default.</summary>
public sealed class VncConnectionSettings
{
    public string? Encoding { get; set; }
    public bool? ViewOnly { get; set; }
    public int? ColorDepth { get; set; }
    public bool? SharedConnection { get; set; }
}

/// <summary>Proxy / jump-host settings applied to an SSH or VNC connection.</summary>
public sealed class ProxySettings
{
    public ProxyType Type { get; set; } = ProxyType.None;
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    // SSH jump-host fields (used when Type == JumpHost)
    public string? JumpHostName { get; set; }
    public int? JumpHostPort { get; set; }
    public string? JumpHostUsername { get; set; }
    public string? JumpHostPrivateKeyPath { get; set; }
}
