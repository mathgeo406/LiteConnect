using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteConnect.Core.Models;

namespace LiteConnect.App.ViewModels;

/// <summary>
/// Placeholder session tab used until real SSH/VNC session ViewModels are implemented (Sprint 5/8).
/// </summary>
public sealed partial class PlaceholderTabViewModel : ObservableObject, ISessionTabViewModel
{
    [ObservableProperty]
    private string _displayName;

    public string StatusIcon => "🟡";

    public SessionState State => SessionState.Connecting;

    [ObservableProperty]
    private bool _isActive;

    public IRelayCommand CloseCommand { get; }
    public IRelayCommand? ReconnectCommand => null;
    public IRelayCommand? DisconnectCommand => null;
    public IRelayCommand? FullScreenCommand => null;
    public IRelayCommand? PropertiesCommand => null;
    public IRelayCommand? ToggleRecordingCommand => null;

    public PlaceholderTabViewModel(ConnectionInfo connection, IRelayCommand closeTabCommand)
    {
        _displayName = $"{connection.Protocol} — {(string.IsNullOrWhiteSpace(connection.Name) ? connection.Hostname : connection.Name)}";
        CloseCommand = new RelayCommand(() => closeTabCommand.Execute(this));
    }

    public void Dispose() { }
}
