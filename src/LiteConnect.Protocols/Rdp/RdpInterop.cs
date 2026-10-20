using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LiteConnect.Protocols.Rdp;

/// <summary>
/// COM interface <c>IMsTscNonScriptable</c> from <c>mstscax.dll</c>, used to set
/// <see cref="ClearTextPassword"/> on the RDP ActiveX control.
/// </summary>
[ComImport]
[Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsTscNonScriptable
{
    string ClearTextPassword { [param: MarshalAs(UnmanagedType.BStr)] set; }
    string PortablePassword { [param: MarshalAs(UnmanagedType.BStr)] set; [return: MarshalAs(UnmanagedType.BStr)] get; }
    string PortableSalt { [param: MarshalAs(UnmanagedType.BStr)] set; [return: MarshalAs(UnmanagedType.BStr)] get; }
    string BinaryPassword { [param: MarshalAs(UnmanagedType.BStr)] set; [return: MarshalAs(UnmanagedType.BStr)] get; }
    string BinarySalt { [param: MarshalAs(UnmanagedType.BStr)] set; [return: MarshalAs(UnmanagedType.BStr)] get; }
    void ResetPassword();
}

/// <summary>
/// COM event dispinterface <c>IMsTscAxEvents</c> from <c>mstscax.dll</c>.
/// </summary>
[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(7)] void OnChannelReceivedData([MarshalAs(UnmanagedType.BStr)] string chanName, [MarshalAs(UnmanagedType.BStr)] string data);
    [DispId(8)] void OnRequestGoFullScreen();
    [DispId(9)] void OnRequestLeaveFullScreen();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
}

/// <summary>
/// RDP Gateway and performance constants (<c>TSC_PROXY_*</c>, <c>TS_PERF_*</c>).
/// </summary>
public static class RdpGatewayConstants
{
    // Proxy usage modes
    public const uint ProxyModeNoneDirect   = 0; // Do not use RD Gateway
    public const uint ProxyModeDirect       = 1; // Always use RD Gateway (no bypass)
    public const uint ProxyModeDetect       = 2; // Use RD Gateway, bypass local addresses
    public const uint ProxyModeDefault      = 3;

    public const uint ProfileModeDefault    = 0;
    public const uint ProfileModeExplicit   = 1;

    public const uint CredsSourceUserPass   = 0;
    public const uint CredsSourceSmartCard  = 1;
    public const uint CredsSourceAny        = 4;

    // Performance flags (TS_PERF_*)
    public const int PerfDisableWallpaper          = 0x00000001;
    public const int PerfDisableFullWindowDrag     = 0x00000002;
    public const int PerfDisableMenuAnimations     = 0x00000004;
    public const int PerfDisableTheming            = 0x00000008;
    public const int PerfDisableCursorShadow       = 0x00000020;
    public const int PerfDisableCursorSettings     = 0x00000040;
    public const int PerfEnableFontSmoothing       = 0x00000080;
    public const int PerfEnableDesktopComposition  = 0x00000100;
}

/// <summary>
/// Hosts the Windows built-in RDP ActiveX control via <see cref="AxHost"/> without
/// requiring design-time <c>&lt;COMReference&gt;</c> generation, enabling clean CLI
/// builds on both x64 and ARM64 Windows.
/// </summary>
/// <remarks>
/// Uses <c>MsTscAx.MsTscAx.*</c> (SafeForScripting) CLSIDs which are the controls
/// that <c>mstsc.exe</c> uses and that work reliably when hosted in WPF/WinForms
/// applications. The legacy <c>MsRDP.MsRDP.*</c> (NotSafeForScripting) CLSIDs
/// immediately fire <c>OnDisconnected(1)</c> on modern Windows 10/11 when hosted
/// outside Internet Explorer.
/// </remarks>
public sealed class AxMsRdpClient9NotSafeForScripting : AxHost
{
    // -- MsTscAx (SafeForScripting) CLSIDs -- preferred for custom hosting --------
    /// <summary>CLSID for <c>MsTscAx.MsTscAx.13</c> (Windows 11 24H2+).</summary>
    public const string ClsidMsTscAx13 = "3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A";

    /// <summary>CLSID for <c>MsTscAx.MsTscAx.12</c> (Windows 11 22H2+).</summary>
    public const string ClsidMsTscAx12 = "1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8";

    /// <summary>CLSID for <c>MsTscAx.MsTscAx.11</c> (Windows 10 1903+).</summary>
    public const string ClsidMsTscAx11 = "A0C63C30-F08D-4AB4-907C-34905D770C7D";

    /// <summary>CLSID for <c>MsTscAx.MsTscAx.10</c> (Windows 10 1709+).</summary>
    public const string ClsidMsTscAx10 = "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";

    /// <summary>CLSID for <c>MsTscAx.MsTscAx.9</c> (Windows 8.1+).</summary>
    public const string ClsidMsTscAx9 = "A3BC03A0-041D-42E3-AD22-882B7865C9C5";

    // -- MsRDP (NotSafeForScripting) CLSIDs -- kept as fallback constants ----------
    /// <summary>CLSID for <c>MsRDP.MsRDP.9</c> (legacy NotSafeForScripting).</summary>
    public const string ClsidNotSafeForScripting9 = "301B94BA-5D25-4A12-BFFE-3B6E7A616585";

    private ConnectionPointCookie? _cookie;
    private RdpAxEventSink? _eventSink;

    public AxMsRdpClient9NotSafeForScripting()
        : base(ResolveInstalledClsid())
    {
    }

    /// <summary>
    /// Resolves the best available RDP ActiveX CLSID registered in <c>HKEY_CLASSES_ROOT\CLSID</c>.
    /// Prefers <c>MsTscAx</c> (SafeForScripting) CLSIDs, newest version first.
    /// </summary>
    public static string ResolveInstalledClsid()
    {
        // Prefer MsTscAx (SafeForScripting) CLSIDs -- these work reliably in custom hosts.
        // The MsRDP.MsRDP.* (NotSafeForScripting) CLSIDs fire OnDisconnected(1) immediately
        // on modern Windows 10/11 when hosted outside Internet Explorer.
        string[] candidates =
        [
            ClsidMsTscAx12,
            ClsidMsTscAx11,
            ClsidMsTscAx10,
            ClsidMsTscAx9,
            ClsidNotSafeForScripting9  // last-resort fallback
        ];

        foreach (var clsid in candidates)
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{clsid}}}");
            if (key is not null)
                return clsid;
        }

        return ClsidMsTscAx9;
    }

    // ── ActiveX Events (3.4) ────────────────────────────────────────────────────

    public event EventHandler? Connecting;
    public event EventHandler? ConnectedEvent;
    public event EventHandler? LoginComplete;
    public event EventHandler<int>? Disconnected;
    public event EventHandler<int>? FatalError;
    public event EventHandler<int>? Warning;
    public event EventHandler? RequestGoFullScreen;
    public event EventHandler? RequestLeaveFullScreen;

    protected override void CreateSink()
    {
        base.CreateSink();
        try
        {
            var ocx = GetOcx();
            if (ocx is not null)
            {
                _eventSink = new RdpAxEventSink(this);
                _cookie = new ConnectionPointCookie(ocx, _eventSink, typeof(IMsTscAxEvents));
            }
        }
        catch
        {
            // Ignore sink attachment errors if running in headless test environments
        }
    }

    protected override void DetachSink()
    {
        try
        {
            _cookie?.Disconnect();
            _cookie = null;
            _eventSink = null;
        }
        catch
        {
            // Ignore detach errors
        }
        base.DetachSink();
    }

    internal void RaiseConnecting() => Connecting?.Invoke(this, EventArgs.Empty);
    internal void RaiseConnected() => ConnectedEvent?.Invoke(this, EventArgs.Empty);
    internal void RaiseLoginComplete() => LoginComplete?.Invoke(this, EventArgs.Empty);
    internal void RaiseDisconnected(int reason) => Disconnected?.Invoke(this, reason);
    internal void RaiseFatalError(int errorCode) => FatalError?.Invoke(this, errorCode);
    internal void RaiseWarning(int warningCode) => Warning?.Invoke(this, warningCode);
    internal void RaiseRequestGoFullScreen() => RequestGoFullScreen?.Invoke(this, EventArgs.Empty);
    internal void RaiseRequestLeaveFullScreen() => RequestLeaveFullScreen?.Invoke(this, EventArgs.Empty);

    /// <summary>Gets the underlying COM ActiveX object (<c>IMsRdpClient9</c>).</summary>
    public object Ocx => GetOcx()
        ?? throw new InvalidOperationException("RDP ActiveX control (OCX) is not initialized yet.");

    // ── Core properties on IMsRdpClient ─────────────────────────────────────────

    public string Server
    {
        get => ComDispatch.GetProperty<string>(Ocx, nameof(Server)) ?? string.Empty;
        set => ComDispatch.SetProperty(Ocx, nameof(Server), value);
    }

    public string UserName
    {
        get => ComDispatch.GetProperty<string>(Ocx, nameof(UserName)) ?? string.Empty;
        set => ComDispatch.SetProperty(Ocx, nameof(UserName), value);
    }

    public string Domain
    {
        get => ComDispatch.GetProperty<string>(Ocx, nameof(Domain)) ?? string.Empty;
        set => ComDispatch.SetProperty(Ocx, nameof(Domain), value);
    }

    public int ColorDepth
    {
        get => ComDispatch.GetProperty<int>(Ocx, nameof(ColorDepth));
        set => ComDispatch.SetProperty(Ocx, nameof(ColorDepth), value);
    }

    public int DesktopWidth
    {
        get => ComDispatch.GetProperty<int>(Ocx, nameof(DesktopWidth));
        set => ComDispatch.SetProperty(Ocx, nameof(DesktopWidth), value);
    }

    public int DesktopHeight
    {
        get => ComDispatch.GetProperty<int>(Ocx, nameof(DesktopHeight));
        set => ComDispatch.SetProperty(Ocx, nameof(DesktopHeight), value);
    }

    public bool FullScreen
    {
        get => ComDispatch.GetProperty<bool>(Ocx, nameof(FullScreen));
        set => ComDispatch.SetProperty(Ocx, nameof(FullScreen), value);
    }

    public string FullScreenTitle
    {
        set => ComDispatch.TrySetProperty(Ocx, nameof(FullScreenTitle), value);
    }

    /// <summary>
    /// Returns the raw connection state from the OCX: 0 = Disconnected, 1 = Connected, 2 = Connecting.
    /// </summary>
    public short Connected => ComDispatch.GetProperty<short>(Ocx, nameof(Connected));

    public uint ExtendedDisconnectReason
    {
        get
        {
            try { return ComDispatch.GetProperty<uint>(Ocx, nameof(ExtendedDisconnectReason)); }
            catch { return 0u; }
        }
    }

    public string GetErrorDescription(uint discReason, uint extendedDisconnectReason)
    {
        try
        {
            return ComDispatch.InvokeMethod(
                Ocx,
                nameof(GetErrorDescription),
                discReason,
                extendedDisconnectReason) as string ?? $"Disconnect code {discReason} (ext {extendedDisconnectReason})";
        }
        catch
        {
            return $"Disconnect code {discReason} (ext {extendedDisconnectReason})";
        }
    }

    // ── Sub-objects ─────────────────────────────────────────────────────────────

    public RdpAdvancedSettingsWrapper AdvancedSettings =>
        new(ComDispatch.TryGetProperty<object>(Ocx, "AdvancedSettings9")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "AdvancedSettings8")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "AdvancedSettings7")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "AdvancedSettings2")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "AdvancedSettings")
            ?? throw new InvalidOperationException("Could not retrieve AdvancedSettings from RDP ActiveX control."));

    public RdpTransportSettingsWrapper TransportSettings =>
        new(ComDispatch.TryGetProperty<object>(Ocx, "TransportSettings4")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "TransportSettings3")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "TransportSettings2")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "TransportSettings")
            ?? throw new InvalidOperationException("Could not retrieve TransportSettings from RDP ActiveX control."));

    public RdpSecuredSettingsWrapper SecuredSettings =>
        new(ComDispatch.TryGetProperty<object>(Ocx, "SecuredSettings3")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "SecuredSettings2")
            ?? ComDispatch.TryGetProperty<object>(Ocx, "SecuredSettings")
            ?? throw new InvalidOperationException("Could not retrieve SecuredSettings from RDP ActiveX control."));

    // ── Password & methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets the clear-text password via <c>AdvancedSettings.ClearTextPassword</c> (falling back to
    /// <see cref="IMsTscNonScriptable"/> if needed).
    /// </summary>
    public void SetClearTextPassword(string password)
    {
        if (ComDispatch.TrySetProperty(AdvancedSettings.ComObject, "ClearTextPassword", password))
            return;

        try
        {
            if (Ocx is IMsTscNonScriptable nonScriptable)
            {
                nonScriptable.ClearTextPassword = password;
            }
        }
        catch
        {
            // Ignore if non-scriptable interface is unavailable
        }
    }

    /// <summary>
    /// Configures specific drive redirection via <c>ClientNonScriptable3.DriveCollection</c> if available.
    /// </summary>
    public void ConfigureDriveCollection(bool redirectDrives, string? driveLetters)
    {
        var adv = AdvancedSettings;
        if (!redirectDrives)
        {
            adv.RedirectDrives = false;
            return;
        }

        // If "ALL" or unspecified, RedirectDrives = true redirects all fixed drives
        if (string.IsNullOrWhiteSpace(driveLetters) ||
            driveLetters.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            adv.RedirectDrives = true;
            return;
        }

        // Parse specific drive letters (e.g. "C,D")
        var selectedLetters = driveLetters
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => char.ToUpperInvariant(s[0]))
            .ToHashSet();

        adv.RedirectDrives = false;

        try
        {
            var driveCollection = ComDispatch.TryGetProperty<object>(Ocx, "DriveCollection");
            if (driveCollection is null)
            {
                adv.RedirectDrives = true;
                return;
            }

            uint count = ComDispatch.GetProperty<uint>(driveCollection, "DriveCount");
            for (uint i = 0; i < count; i++)
            {
                var drive = ComDispatch.GetIndexedProperty(driveCollection, "DriveByIndex", i);
                if (drive is null) continue;

                string name = ComDispatch.GetProperty<string>(drive, "Name") ?? string.Empty;
                if (name.Length > 0)
                {
                    char letter = char.ToUpperInvariant(name[0]);
                    ComDispatch.TrySetProperty(drive, "RedirectionState", selectedLetters.Contains(letter));
                }
            }
        }
        catch
        {
            // Fallback to all drives if DriveCollection is not scriptable via IDispatch
            adv.RedirectDrives = true;
        }
    }

    public void Connect() => ComDispatch.InvokeMethod(Ocx, nameof(Connect));

    public void Disconnect() => ComDispatch.InvokeMethod(Ocx, nameof(Disconnect));

    public void UpdateSessionDisplaySettings(uint width, uint height, uint scaleFactor = 100u)
    {
        uint scale = scaleFactor is >= 100u and <= 500u ? scaleFactor : 100u;
        ComDispatch.InvokeMethod(
            Ocx,
            "UpdateSessionDisplaySettings",
            width,
            height,
            width,
            height,
            0u,    // ulOrientation: landscape
            scale, // ulDesktopScaleFactor
            100u   // ulDeviceScaleFactor
        );
    }
}

/// <summary>
/// Event sink implementation receiving COM callbacks from <see cref="IMsTscAxEvents"/>.
/// </summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed class RdpAxEventSink(AxMsRdpClient9NotSafeForScripting owner) : IMsTscAxEvents
{
    public void OnConnecting() => owner.RaiseConnecting();
    public void OnConnected() => owner.RaiseConnected();
    public void OnLoginComplete() => owner.RaiseLoginComplete();
    public void OnDisconnected(int discReason) => owner.RaiseDisconnected(discReason);
    public void OnEnterFullScreenMode() { }
    public void OnLeaveFullScreenMode() { }
    public void OnChannelReceivedData(string chanName, string data) { }
    public void OnRequestGoFullScreen() => owner.RaiseRequestGoFullScreen();
    public void OnRequestLeaveFullScreen() => owner.RaiseRequestLeaveFullScreen();
    public void OnFatalError(int errorCode) => owner.RaiseFatalError(errorCode);
    public void OnWarning(int warningCode) => owner.RaiseWarning(warningCode);
    public void OnRemoteDesktopSizeChange(int width, int height) { }
}

/// <summary>
/// Strongly-typed late-bound wrapper over <c>IMsRdpClientAdvancedSettings8</c>.
/// </summary>
public sealed class RdpAdvancedSettingsWrapper(object comObject)
{
    internal object ComObject => comObject;

    public int RDPPort
    {
        get => ComDispatch.GetProperty<int>(comObject, nameof(RDPPort));
        set => ComDispatch.TrySetProperty(comObject, nameof(RDPPort), value);
    }

    public string ClearTextPassword
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(ClearTextPassword), value);
    }

    public bool SmartSizing
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(SmartSizing));
        set => ComDispatch.TrySetProperty(comObject, nameof(SmartSizing), value);
    }

    public uint AuthenticationLevel
    {
        get => ComDispatch.GetProperty<uint>(comObject, nameof(AuthenticationLevel));
        set => ComDispatch.TrySetProperty(comObject, nameof(AuthenticationLevel), value);
    }

    public bool EnableCredSspSupport
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(EnableCredSspSupport));
        set => ComDispatch.TrySetProperty(comObject, nameof(EnableCredSspSupport), value);
    }

    public bool ConnectToAdministerServer
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(ConnectToAdministerServer));
        set => ComDispatch.TrySetProperty(comObject, nameof(ConnectToAdministerServer), value);
    }

    public bool RedirectClipboard
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(RedirectClipboard));
        set => ComDispatch.TrySetProperty(comObject, nameof(RedirectClipboard), value);
    }

    public bool RedirectDrives
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(RedirectDrives));
        set => ComDispatch.TrySetProperty(comObject, nameof(RedirectDrives), value);
    }

    public bool RedirectPrinters
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(RedirectPrinters));
        set => ComDispatch.TrySetProperty(comObject, nameof(RedirectPrinters), value);
    }

    public bool RedirectSmartCards
    {
        get => ComDispatch.GetProperty<bool>(comObject, nameof(RedirectSmartCards));
        set => ComDispatch.TrySetProperty(comObject, nameof(RedirectSmartCards), value);
    }

    public bool RedirectDirectX
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(RedirectDirectX), value);
    }

    public uint AudioRedirectionMode
    {
        get => ComDispatch.GetProperty<uint>(comObject, nameof(AudioRedirectionMode));
        set => ComDispatch.TrySetProperty(comObject, nameof(AudioRedirectionMode), value);
    }

    public bool AudioCaptureRedirectionMode
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(AudioCaptureRedirectionMode), value);
    }

    public int PerformanceFlags
    {
        get => ComDispatch.GetProperty<int>(comObject, nameof(PerformanceFlags));
        set => ComDispatch.TrySetProperty(comObject, nameof(PerformanceFlags), value);
    }

    public uint NetworkConnectionType
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(NetworkConnectionType), value);
    }

    public int BitmapPersistence
    {
        set
        {
            // MSTSCLib has a historic typo "BitmapPeristence" alongside "BitmapPersistence"
            if (!ComDispatch.TrySetProperty(comObject, "BitmapPeristence", value))
                ComDispatch.TrySetProperty(comObject, "BitmapPersistence", value);
        }
    }

    public int SingleConnectionTimeout
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(SingleConnectionTimeout), value);
    }

    public bool EnableAutoReconnect
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(EnableAutoReconnect), value);
    }

    public int MaxReconnectAttempts
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(MaxReconnectAttempts), value);
    }

    public string LoadBalanceInfo
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(LoadBalanceInfo), value);
    }
}

/// <summary>
/// Strongly-typed late-bound wrapper over <c>IMsRdpClientSecuredSettings2</c>.
/// </summary>
public sealed class RdpSecuredSettingsWrapper(object comObject)
{
    public int AudioRedirectionMode
    {
        get => ComDispatch.GetProperty<int>(comObject, nameof(AudioRedirectionMode));
        set => ComDispatch.TrySetProperty(comObject, nameof(AudioRedirectionMode), value);
    }

    public int KeyboardHookMode
    {
        get => ComDispatch.GetProperty<int>(comObject, nameof(KeyboardHookMode));
        set => ComDispatch.TrySetProperty(comObject, nameof(KeyboardHookMode), value);
    }

    public string StartProgram
    {
        get => ComDispatch.GetProperty<string>(comObject, nameof(StartProgram)) ?? string.Empty;
        set => ComDispatch.TrySetProperty(comObject, nameof(StartProgram), value);
    }

    public string WorkDir
    {
        get => ComDispatch.GetProperty<string>(comObject, nameof(WorkDir)) ?? string.Empty;
        set => ComDispatch.TrySetProperty(comObject, nameof(WorkDir), value);
    }
}

/// <summary>
/// Strongly-typed late-bound wrapper over <c>IMsRdpClientTransportSettings2</c> for RD Gateway configuration.
/// </summary>
public sealed class RdpTransportSettingsWrapper(object comObject)
{
    public string GatewayHostname
    {
        get => ComDispatch.GetProperty<string>(comObject, nameof(GatewayHostname)) ?? string.Empty;
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayHostname), value);
    }

    public uint GatewayUsageMethod
    {
        get => ComDispatch.GetProperty<uint>(comObject, nameof(GatewayUsageMethod));
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayUsageMethod), value);
    }

    public uint GatewayProfileUsageMethod
    {
        get => ComDispatch.GetProperty<uint>(comObject, nameof(GatewayProfileUsageMethod));
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayProfileUsageMethod), value);
    }

    public uint GatewayCredsSource
    {
        get => ComDispatch.GetProperty<uint>(comObject, nameof(GatewayCredsSource));
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayCredsSource), value);
    }

    public uint GatewayCredSharing
    {
        get => ComDispatch.GetProperty<uint>(comObject, nameof(GatewayCredSharing));
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayCredSharing), value);
    }

    public string GatewayUsername
    {
        get => ComDispatch.GetProperty<string>(comObject, nameof(GatewayUsername)) ?? string.Empty;
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayUsername), value);
    }

    public string GatewayDomain
    {
        get => ComDispatch.GetProperty<string>(comObject, nameof(GatewayDomain)) ?? string.Empty;
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayDomain), value);
    }

    public string GatewayPassword
    {
        set => ComDispatch.TrySetProperty(comObject, nameof(GatewayPassword), value);
    }
}

/// <summary>
/// Helper for invoking late-bound COM <c>IDispatch</c> properties and methods.
/// </summary>
internal static class ComDispatch
{
    public static T? GetProperty<T>(object comObject, string propertyName)
    {
        var result = comObject.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
            binder: null,
            target: comObject,
            args: null);

        if (result is null)
            return default;

        if (result is T typed)
            return typed;

        return (T)Convert.ChangeType(result, typeof(T));
    }

    public static T? TryGetProperty<T>(object comObject, string propertyName)
    {
        try
        {
            return GetProperty<T>(comObject, propertyName);
        }
        catch
        {
            return default;
        }
    }

    public static object? GetIndexedProperty(object comObject, string propertyName, params object?[] args)
    {
        return comObject.GetType().InvokeMember(
            propertyName,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
            binder: null,
            target: comObject,
            args: args);
    }

    public static void SetProperty(object comObject, string propertyName, object? value)
    {
        try
        {
            comObject.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                binder: null,
                target: comObject,
                args: [value]);
        }
        catch (TargetInvocationException tie) when (value is uint u)
        {
            // Retry with VT_I4 (int) if the COM dispinterface rejects VT_UI4 (uint)
            try
            {
                comObject.GetType().InvokeMember(
                    propertyName,
                    BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                    binder: null,
                    target: comObject,
                    args: [(int)u]);
            }
            catch
            {
                throw tie.InnerException ?? tie;
            }
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    public static bool TrySetProperty(object comObject, string propertyName, object? value)
    {
        try
        {
            SetProperty(comObject, propertyName, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static object? InvokeMethod(object comObject, string methodName, params object?[] args)
    {
        try
        {
            return comObject.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase,
                binder: null,
                target: comObject,
                args: args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }
}
