using System.Windows;
using LiteConnect.Core.Models;

namespace LiteConnect.App.Views;

/// <summary>
/// Code-behind for the Connection Properties dialog.
/// Populates controls from an existing <see cref="ConnectionInfo"/> (edit mode)
/// or creates a new one (new connection mode).
/// </summary>
public partial class ConnectionPropertiesDialog : Window
{
    private readonly ConnectionInfo _model;
    private readonly bool           _isNew;

    public ConnectionInfo? Result { get; private set; }

    // ── Constructor ─────────────────────────────────────────────────────────────

    public ConnectionPropertiesDialog(ConnectionInfo? existing = null, Guid? defaultGroupId = null)
    {
        InitializeComponent();

        _isNew = existing is null;
        _model = existing ?? new ConnectionInfo
        {
            GroupId  = defaultGroupId ?? Guid.Empty,
            Protocol = ProtocolType.SSH,
            Port     = 22
        };

        PopulateStaticItems();
        LoadModel();
        UpdateProtocolVisibility(_model.Protocol);
    }

    // ── Populate combo items ────────────────────────────────────────────────────

    private void PopulateStaticItems()
    {
        CboProtocol.ItemsSource     = Enum.GetValues<ProtocolType>();
        CboTerminalType.ItemsSource = new[] { "xterm-256color", "xterm", "vt100", "vt220" };
        CboEncoding.ItemsSource     = new[] { "UTF-8", "ISO-8859-1", "Windows-1252" };

        // RDP Display
        CboResolutionMode.ItemsSource = new[] { "Auto", "MatchMonitor", "Custom", "FullScreen" };
        CboColorDepth.ItemsSource     = new[] { 15, 16, 24, 32 };
        CboDpiScaling.ItemsSource     = new[] { "Match local", "100%", "125%", "150%", "200%" };

        // RDP Gateway
        CboGatewayAuth.ItemsSource = new[] { "Password", "SmartCard" };

        // RDP Resources
        CboAudioPlayback.ItemsSource  = new[] { "Local (This computer)", "Remote computer", "Do not play" };
        CboAudioRecording.ItemsSource = new[] { "Do not record", "Record from this computer" };
        CboKeyboardHook.ItemsSource   = new[] { "On this computer", "On remote computer", "In full screen only" };

        // RDP Experience
        CboConnectionSpeed.ItemsSource = new[] { "Auto", "Modem", "BroadbandLow", "BroadbandHigh", "LAN" };

        // RDP Security / Advanced
        CboAuthLevel.ItemsSource = new[]
        {
            "0 — No authentication",
            "1 — Require server authentication",
            "2 — Warn if authentication fails (NLA default)"
        };

        // Proxy & Recording
        CboProxyType.ItemsSource    = Enum.GetValues<ProxyType>();
        CboRecordFormat.ItemsSource = Enum.GetValues<RecordingFormat>();
    }

    // ── Load model → controls ───────────────────────────────────────────────────

    private void LoadModel()
    {
        Title = _isNew ? "New Connection" : $"Properties — {_model.Name}";

        TxtName.Text              = _model.Name;
        CboProtocol.SelectedItem  = _model.Protocol;
        TxtHostname.Text          = _model.Hostname;
        TxtPort.Text              = _model.Port?.ToString() ?? string.Empty;
        TxtUsername.Text          = _model.Username ?? string.Empty;
        PwdPassword.Password      = _model.Password ?? string.Empty;
        TxtPasswordRevealed.Text  = _model.Password ?? string.Empty;
        TxtDomain.Text            = _model.Domain ?? string.Empty;
        TxtKeyPath.Text           = _model.PrivateKeyPath ?? string.Empty;
        PwdKeyPassphrase.Password = _model.KeyPassphrase ?? string.Empty;
        TxtNotes.Text             = _model.Notes ?? string.Empty;

        // SSH settings
        var ssh = _model.SshSettings ?? new SshConnectionSettings();
        CboTerminalType.SelectedItem = ssh.TerminalType ?? "xterm-256color";
        CboEncoding.SelectedItem     = ssh.Encoding ?? "UTF-8";
        TxtScrollback.Text           = ssh.ScrollbackLines?.ToString() ?? "10000";
        TxtKeepalive.Text            = ssh.KeepaliveIntervalSeconds?.ToString() ?? "60";
        ChkCompression.IsChecked     = ssh.Compression ?? false;

        // RDP settings
        var rdp = _model.RdpSettings ?? new RdpConnectionSettings();

        // 1. Display
        string resMode = rdp.ResolutionMode ?? (rdp.DesktopWidth is > 0 ? "Custom" : "Auto");
        CboResolutionMode.SelectedItem = resMode;
        TxtRdpWidth.Text               = rdp.DesktopWidth?.ToString() ?? string.Empty;
        TxtRdpHeight.Text              = rdp.DesktopHeight?.ToString() ?? string.Empty;
        CboColorDepth.SelectedItem     = rdp.ColorDepth ?? 32;
        ChkSmartSizing.IsChecked       = rdp.SmartSizing ?? true;
        CboDpiScaling.SelectedItem     = rdp.DpiScaling switch
        {
            100 => "100%",
            125 => "125%",
            150 => "150%",
            200 => "200%",
            _   => "Match local"
        };
        UpdateResolutionInputs(resMode);

        // 2. Gateway
        ChkUseGateway.IsChecked         = rdp.UseGateway ?? !string.IsNullOrWhiteSpace(rdp.GatewayHostname);
        TxtGatewayHost.Text             = rdp.GatewayHostname ?? string.Empty;
        TxtGatewayPort.Text             = (rdp.GatewayPort ?? 443).ToString();
        CboGatewayAuth.SelectedItem     = rdp.GatewayAuthMethod ?? "Password";
        ChkGatewayBypassLocal.IsChecked = rdp.GatewayBypassLocal ?? true;
        TxtGatewayUsername.Text         = rdp.GatewayUsername ?? string.Empty;
        PwdGatewayPassword.Password     = rdp.GatewayPassword ?? string.Empty;
        TxtGatewayDomain.Text           = rdp.GatewayDomain ?? string.Empty;

        // 3. Resources
        CboAudioPlayback.SelectedIndex  = Math.Clamp(rdp.AudioRedirectionMode ?? 0, 0, 2);
        CboAudioRecording.SelectedIndex = Math.Clamp(rdp.AudioCaptureRedirectionMode ?? 0, 0, 1);
        CboKeyboardHook.SelectedIndex   = Math.Clamp(rdp.KeyboardHookMode ?? 2, 0, 2);
        ChkClipboard.IsChecked          = rdp.RedirectClipboard ?? true;
        ChkPrinters.IsChecked           = rdp.RedirectPrinters ?? false;
        ChkSmartCards.IsChecked         = rdp.RedirectSmartCards ?? false;
        ChkRedirectUsb.IsChecked        = rdp.RedirectUsb ?? false;
        ChkDrives.IsChecked             = rdp.RedirectDrives ?? false;
        TxtDriveLetters.Text            = rdp.RedirectedDriveLetters ?? "ALL";

        // 4. Experience
        CboConnectionSpeed.SelectedItem  = rdp.ConnectionSpeed ?? "Auto";
        ChkDesktopBackground.IsChecked   = rdp.DesktopBackground ?? true;
        ChkFontSmoothing.IsChecked       = rdp.FontSmoothing ?? true;
        ChkDesktopComposition.IsChecked  = rdp.DesktopComposition ?? true;
        ChkVisualStyles.IsChecked        = rdp.VisualStyles ?? true;
        ChkBitmapCaching.IsChecked       = rdp.PersistentBitmapCaching ?? true;

        // 5. Security & Advanced
        ChkNla.IsChecked                 = rdp.UseNla ?? true;
        ChkEnableCredSsp.IsChecked       = rdp.EnableCredSsp ?? (rdp.UseNla ?? true);
        CboAuthLevel.SelectedIndex       = Math.Clamp(rdp.AuthenticationLevel ?? 2, 0, 2);
        ChkConsoleSession.IsChecked      = rdp.ConsoleSession ?? false;
        TxtConnectTimeout.Text           = (rdp.ConnectTimeoutSeconds ?? 30).ToString();
        ChkAutoReconnect.IsChecked       = rdp.AutoReconnect ?? true;
        TxtMaxReconnectAttempts.Text     = (rdp.MaxReconnectAttempts ?? 20).ToString();
        TxtReconnectInterval.Text        = (rdp.ReconnectIntervalSeconds ?? 3).ToString();
        TxtStartProgram.Text             = rdp.StartProgram ?? string.Empty;
        TxtWorkDir.Text                  = rdp.WorkDir ?? string.Empty;
        TxtLoadBalancingInfo.Text        = rdp.LoadBalancingInfo ?? string.Empty;

        // Proxy settings
        var proxy = _model.ProxySettings ?? new ProxySettings();
        CboProxyType.SelectedItem  = proxy.Type;
        TxtProxyHost.Text          = proxy.Host ?? string.Empty;
        TxtProxyPort.Text          = proxy.Port?.ToString() ?? string.Empty;
        TxtProxyUsername.Text      = proxy.Username ?? string.Empty;
        PwdProxyPassword.Password  = proxy.Password ?? string.Empty;

        // Recording
        ChkAutoRecord.IsChecked      = _model.AutoRecord;
        CboRecordFormat.SelectedItem = _model.RecordingFormat;
        TxtRecordDir.Text            = _model.RecordingDirectory ?? string.Empty;
    }

    // ── Save controls → model ───────────────────────────────────────────────────

    private bool TrySaveModel()
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text))
        {
            System.Windows.MessageBox.Show("Display Name is required.", "Validation", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            TxtName.Focus();
            return false;
        }

        if (string.IsNullOrWhiteSpace(TxtHostname.Text))
        {
            System.Windows.MessageBox.Show("Hostname is required.", "Validation", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            TxtHostname.Focus();
            return false;
        }

        string rawPassword = BtnRevealPassword.IsChecked == true
            ? TxtPasswordRevealed.Text
            : PwdPassword.Password;

        _model.Name           = TxtName.Text.Trim();
        _model.Protocol       = (ProtocolType)CboProtocol.SelectedItem!;
        _model.Hostname       = TxtHostname.Text.Trim();
        _model.Port           = int.TryParse(TxtPort.Text, out var p) ? p : null;
        _model.Username       = NullIfEmpty(TxtUsername.Text);
        _model.Password       = NullIfEmpty(rawPassword);
        _model.Domain         = NullIfEmpty(TxtDomain.Text);
        _model.PrivateKeyPath = NullIfEmpty(TxtKeyPath.Text);
        _model.KeyPassphrase  = NullIfEmpty(PwdKeyPassphrase.Password);
        _model.Notes          = NullIfEmpty(TxtNotes.Text);
        _model.UpdatedAt      = DateTimeOffset.UtcNow;

        // SSH
        _model.SshSettings = _model.Protocol == ProtocolType.SSH ? new SshConnectionSettings
        {
            TerminalType             = CboTerminalType.SelectedItem as string,
            Encoding                 = CboEncoding.SelectedItem as string,
            ScrollbackLines          = int.TryParse(TxtScrollback.Text, out var sb) ? sb : null,
            KeepaliveIntervalSeconds = int.TryParse(TxtKeepalive.Text, out var ka) ? ka : null,
            Compression              = ChkCompression.IsChecked
        } : null;

        // RDP (all §2.3 fields)
        int? dpiScaling = (CboDpiScaling.SelectedItem as string) switch
        {
            "100%" => 100,
            "125%" => 125,
            "150%" => 150,
            "200%" => 200,
            _      => null
        };

        _model.RdpSettings = _model.Protocol == ProtocolType.RDP ? new RdpConnectionSettings
        {
            // Display
            ResolutionMode = CboResolutionMode.SelectedItem as string ?? "Auto",
            DesktopWidth   = int.TryParse(TxtRdpWidth.Text, out var dw) ? dw : null,
            DesktopHeight  = int.TryParse(TxtRdpHeight.Text, out var dh) ? dh : null,
            ColorDepth     = CboColorDepth.SelectedItem as int? ?? 32,
            SmartSizing    = ChkSmartSizing.IsChecked,
            DpiScaling     = dpiScaling,

            // Gateway
            UseGateway         = ChkUseGateway.IsChecked,
            GatewayHostname    = NullIfEmpty(TxtGatewayHost.Text),
            GatewayPort        = int.TryParse(TxtGatewayPort.Text, out var gp) ? gp : 443,
            GatewayAuthMethod  = CboGatewayAuth.SelectedItem as string ?? "Password",
            GatewayBypassLocal = ChkGatewayBypassLocal.IsChecked,
            GatewayUsername    = NullIfEmpty(TxtGatewayUsername.Text),
            GatewayPassword    = NullIfEmpty(PwdGatewayPassword.Password),
            GatewayDomain      = NullIfEmpty(TxtGatewayDomain.Text),

            // Resources
            AudioRedirectionMode        = Math.Max(0, CboAudioPlayback.SelectedIndex),
            RedirectAudio               = CboAudioPlayback.SelectedIndex != 2,
            AudioCaptureRedirectionMode = Math.Max(0, CboAudioRecording.SelectedIndex),
            KeyboardHookMode            = Math.Max(0, CboKeyboardHook.SelectedIndex),
            RedirectClipboard           = ChkClipboard.IsChecked,
            RedirectPrinters            = ChkPrinters.IsChecked,
            RedirectSmartCards          = ChkSmartCards.IsChecked,
            RedirectUsb                 = ChkRedirectUsb.IsChecked,
            RedirectDrives              = ChkDrives.IsChecked,
            RedirectedDriveLetters      = NullIfEmpty(TxtDriveLetters.Text),

            // Experience
            ConnectionSpeed         = CboConnectionSpeed.SelectedItem as string ?? "Auto",
            DesktopBackground       = ChkDesktopBackground.IsChecked,
            FontSmoothing           = ChkFontSmoothing.IsChecked,
            DesktopComposition      = ChkDesktopComposition.IsChecked,
            VisualStyles            = ChkVisualStyles.IsChecked,
            PersistentBitmapCaching = ChkBitmapCaching.IsChecked,

            // Security & Advanced
            UseNla                   = ChkNla.IsChecked,
            EnableCredSsp            = ChkEnableCredSsp.IsChecked,
            AuthenticationLevel      = Math.Max(0, CboAuthLevel.SelectedIndex),
            ConsoleSession           = ChkConsoleSession.IsChecked,
            ConnectTimeoutSeconds    = int.TryParse(TxtConnectTimeout.Text, out var ct) ? ct : 30,
            AutoReconnect            = ChkAutoReconnect.IsChecked,
            MaxReconnectAttempts     = int.TryParse(TxtMaxReconnectAttempts.Text, out var mra) ? mra : 20,
            ReconnectIntervalSeconds = int.TryParse(TxtReconnectInterval.Text, out var ri) ? ri : 3,
            StartProgram             = NullIfEmpty(TxtStartProgram.Text),
            WorkDir                  = NullIfEmpty(TxtWorkDir.Text),
            LoadBalancingInfo        = NullIfEmpty(TxtLoadBalancingInfo.Text)
        } : null;

        // Proxy
        var proxyType = (ProxyType)CboProxyType.SelectedItem!;
        _model.ProxySettings = proxyType == ProxyType.None ? null : new ProxySettings
        {
            Type     = proxyType,
            Host     = NullIfEmpty(TxtProxyHost.Text),
            Port     = int.TryParse(TxtProxyPort.Text, out var pp) ? pp : null,
            Username = NullIfEmpty(TxtProxyUsername.Text),
            Password = NullIfEmpty(PwdProxyPassword.Password)
        };

        // Recording
        _model.AutoRecord         = ChkAutoRecord.IsChecked == true;
        _model.RecordingFormat    = (RecordingFormat)CboRecordFormat.SelectedItem!;
        _model.RecordingDirectory = NullIfEmpty(TxtRecordDir.Text);

        return true;
    }

    // ── Event handlers ──────────────────────────────────────────────────────────

    private void CboProtocol_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboProtocol.SelectedItem is ProtocolType p)
        {
            UpdateProtocolVisibility(p);

            // Auto-fill default port when the user hasn't overridden it
            if (string.IsNullOrWhiteSpace(TxtPort.Text) ||
                TxtPort.Text is "22" or "3389" or "5900")
            {
                TxtPort.Text = p switch
                {
                    ProtocolType.SSH => "22",
                    ProtocolType.RDP => "3389",
                    ProtocolType.VNC => "5900",
                    _                => ""
                };
            }
        }
    }

    private void CboResolutionMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboResolutionMode.SelectedItem is string mode)
            UpdateResolutionInputs(mode);
    }

    private void UpdateResolutionInputs(string mode)
    {
        bool isCustom = string.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase);
        TxtRdpWidth.IsEnabled  = isCustom;
        TxtRdpHeight.IsEnabled = isCustom;
    }

    private void UpdateProtocolVisibility(ProtocolType protocol)
    {
        var isSsh = protocol == ProtocolType.SSH;
        var isRdp = protocol == ProtocolType.RDP;

        PanelSshKey.Visibility     = isSsh ? Visibility.Visible : Visibility.Collapsed;
        PanelDomain.Visibility     = isRdp ? Visibility.Visible : Visibility.Collapsed;
        PanelRdpAdvanced.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;

        TabSsh.Visibility           = isSsh ? Visibility.Visible : Visibility.Collapsed;
        TabProxy.Visibility         = isRdp ? Visibility.Collapsed : Visibility.Visible;

        TabRdpDisplay.Visibility    = isRdp ? Visibility.Visible : Visibility.Collapsed;
        TabRdpGateway.Visibility    = isRdp ? Visibility.Visible : Visibility.Collapsed;
        TabRdpResources.Visibility  = isRdp ? Visibility.Visible : Visibility.Collapsed;
        TabRdpExperience.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySaveModel()) return;
        Result = _model;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void BtnBrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select Private Key File",
            Filter = "Key files (*.pem;*.ppk;*.key)|*.pem;*.ppk;*.key|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            TxtKeyPath.Text = dlg.FileName;
    }

    private bool _syncingPassword;

    private void PwdPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        _syncingPassword         = true;
        TxtPasswordRevealed.Text = PwdPassword.Password;
        _syncingPassword         = false;
    }

    private void TxtPasswordRevealed_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        _syncingPassword   = true;
        PwdPassword.Password = TxtPasswordRevealed.Text;
        _syncingPassword   = false;
    }

    private void BtnRevealPassword_CheckedChanged(object sender, RoutedEventArgs e)
    {
        bool revealed = BtnRevealPassword.IsChecked == true;
        if (revealed)
        {
            TxtPasswordRevealed.Text       = PwdPassword.Password;
            TxtPasswordRevealed.Visibility = Visibility.Visible;
            PwdPassword.Visibility         = Visibility.Collapsed;
        }
        else
        {
            PwdPassword.Password           = TxtPasswordRevealed.Text;
            PwdPassword.Visibility         = Visibility.Visible;
            TxtPasswordRevealed.Visibility = Visibility.Collapsed;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
