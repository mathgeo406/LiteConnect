using System.Windows;
using LiteConnect.Core.Models;

namespace LiteConnect.App.Views;

/// <summary>
/// Modal dialog prompting for Username, Password, and optional Domain when initiating
/// a Quick Connect session or connecting to a host without saved credentials.
/// </summary>
public partial class CredentialPromptDialog : Window
{
    private readonly bool _requireUsername;
    private bool _syncingPassword;

    public string Username => TxtUsername.Text.Trim();

    public string Password =>
        BtnReveal.IsChecked == true ? TxtPasswordRevealed.Text : PwdBox.Password;

    public string Domain => TxtDomain.Text.Trim();

    public CredentialPromptDialog(
        string hostname,
        ProtocolType protocol,
        string? initialUsername = null,
        string? initialDomain = null,
        string? promptReason = null)
    {
        InitializeComponent();

        _requireUsername = protocol is ProtocolType.SSH or ProtocolType.RDP;

        Title = $"{protocol} Credentials — {hostname}";
        TxtSubtitle.Text = !string.IsNullOrWhiteSpace(promptReason)
            ? promptReason
            : $"Enter credentials for {protocol} connection to {hostname}:";

        if (!string.IsNullOrWhiteSpace(initialUsername))
            TxtUsername.Text = initialUsername;

        if (!string.IsNullOrWhiteSpace(initialDomain))
            TxtDomain.Text = initialDomain;

        if (protocol == ProtocolType.RDP)
            PnlDomain.Visibility = Visibility.Visible;

        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text))
                TxtUsername.Focus();
            else
                PwdBox.Focus();
        };
    }

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_requireUsername && string.IsNullOrWhiteSpace(Username))
        {
            TxtError.Text = "Please enter a username.";
            TxtError.Visibility = Visibility.Visible;
            TxtUsername.Focus();
            return;
        }

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void PwdBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        _syncingPassword = true;
        TxtPasswordRevealed.Text = PwdBox.Password;
        TxtError.Visibility = Visibility.Collapsed;
        _syncingPassword = false;
    }

    private void TxtPasswordRevealed_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        _syncingPassword = true;
        PwdBox.Password = TxtPasswordRevealed.Text;
        TxtError.Visibility = Visibility.Collapsed;
        _syncingPassword = false;
    }

    private void BtnReveal_CheckedChanged(object sender, RoutedEventArgs e)
    {
        bool revealed = BtnReveal.IsChecked == true;
        if (revealed)
        {
            TxtPasswordRevealed.Text = PwdBox.Password;
            TxtPasswordRevealed.Visibility = Visibility.Visible;
            PwdBox.Visibility = Visibility.Collapsed;
            TxtPasswordRevealed.Focus();
            TxtPasswordRevealed.CaretIndex = TxtPasswordRevealed.Text.Length;
        }
        else
        {
            PwdBox.Password = TxtPasswordRevealed.Text;
            PwdBox.Visibility = Visibility.Visible;
            TxtPasswordRevealed.Visibility = Visibility.Collapsed;
            PwdBox.Focus();
        }
    }
}
