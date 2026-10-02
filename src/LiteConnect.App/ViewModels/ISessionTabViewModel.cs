using CommunityToolkit.Mvvm.Input;
using LiteConnect.Core.Models;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// Contract for any session that can live in a tab.
/// ViewModels for SSH, RDP, and VNC implement this.
/// </summary>
public interface ISessionTabViewModel : IDisposable
{
    /// <summary>Text shown in the tab header.</summary>
    string DisplayName { get; set; }

    /// <summary>Status icon shown in the tab header (🟢 connected / 🔴 disconnected / 🟡 connecting).</summary>
    string StatusIcon { get; }

    /// <summary>Current lifecycle state of the underlying protocol session.</summary>
    SessionState State { get; }

    /// <summary>True when this tab is the selected one.</summary>
    bool IsActive { get; set; }

    /// <summary>True when session recording is currently active on this tab.</summary>
    bool IsRecording => false;

    /// <summary>Formatted recording status indicator text for the main status bar.</summary>
    string RecordingStatusText => string.Empty;

    /// <summary>Command that closes (removes) this tab.</summary>
    IRelayCommand CloseCommand { get; }

    /// <summary>Command to reconnect the session.</summary>
    IRelayCommand? ReconnectCommand { get; }

    /// <summary>Command to disconnect the session.</summary>
    IRelayCommand? DisconnectCommand { get; }

    /// <summary>Command to enter full-screen mode.</summary>
    IRelayCommand? FullScreenCommand { get; }

    /// <summary>Command to open the connection properties dialog.</summary>
    IRelayCommand? PropertiesCommand { get; }

    /// <summary>Command to start or stop session recording (SSH tabs).</summary>
    IRelayCommand? ToggleRecordingCommand => null;
}
