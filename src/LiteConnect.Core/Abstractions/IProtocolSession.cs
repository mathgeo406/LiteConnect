using LiteConnect.Core.Models;

namespace LiteConnect.Core.Abstractions;

/// <summary>
/// Represents an active or idle protocol session (SSH, RDP, VNC).
/// Each tab hosts one <see cref="IProtocolSession"/>.
/// </summary>
public interface IProtocolSession : IDisposable
{
    /// <summary>Unique session identifier (stable for the lifetime of the tab).</summary>
    Guid SessionId { get; }

    /// <summary>Display name shown in the tab header.</summary>
    string DisplayName { get; }

    /// <summary>Current lifecycle state of this session.</summary>
    SessionState State { get; }

    /// <summary>Protocol handled by this session.</summary>
    ProtocolType Protocol { get; }

    /// <summary>Whether a reconnect is possible after disconnect.</summary>
    bool CanReconnect { get; }

    /// <summary>Fires whenever <see cref="State"/> changes.</summary>
    event EventHandler<SessionStateChangedEventArgs> StateChanged;

    /// <summary>Fires when the session wants to surface a notification (e.g., host-key warning).</summary>
    event EventHandler<SessionNotificationEventArgs> NotificationRaised;

    /// <summary>
    /// Establish the connection asynchronously.
    /// Throws <see cref="InvalidOperationException"/> if already connected.
    /// </summary>
    Task ConnectAsync(ConnectionInfo connectionInfo, CancellationToken ct = default);

    /// <summary>Gracefully tear down the connection.</summary>
    Task DisconnectAsync();

    /// <summary>
    /// Returns the WPF <c>FrameworkElement</c> that renders this session's content.
    /// For SSH: a <c>TerminalControl</c>. For RDP: a <c>WindowsFormsHost</c>.
    /// The return type is <c>object</c> to avoid a WPF reference in Core;
    /// callers in the App layer cast to <c>FrameworkElement</c>.
    /// </summary>
    object GetSessionControl();

    /// <summary>
    /// Called when the containing tab is resized.
    /// SSH implementations use this to send SIGWINCH; RDP updates DesktopWidth/Height.
    /// </summary>
    void OnResize(int width, int height);
}

// ─── Event args ───────────────────────────────────────────────────────────────

public sealed class SessionStateChangedEventArgs(SessionState previous, SessionState current) : EventArgs
{
    public SessionState Previous { get; } = previous;
    public SessionState Current { get; } = current;
}

public sealed class SessionNotificationEventArgs(string message, NotificationSeverity severity) : EventArgs
{
    public string Message { get; } = message;
    public NotificationSeverity Severity { get; } = severity;
}

public enum NotificationSeverity { Info, Warning, Error }
