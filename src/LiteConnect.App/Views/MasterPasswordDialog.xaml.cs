using System.Windows;
using LiteConnect.Core.Abstractions;

namespace LiteConnect.App.Views;

/// <summary>
/// Modal dialog prompting for the Master Password to unlock the SQLCipher database
/// on startup or after an idle/system auto-lock (Sprint 4.3 &amp; 4.6).
/// </summary>
public partial class MasterPasswordDialog : Window
{
    private readonly ICredentialStore _credentialStore;
    private bool _syncingPassword;

    public MasterPasswordDialog(ICredentialStore credentialStore, bool isAutoLock = false)
    {
        InitializeComponent();
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));

        if (isAutoLock)
        {
            Title            = "LiteConnect Locked";
            TxtSubtitle.Text = "Vault locked due to inactivity or Windows session lock. Enter your master password to continue.";
        }

        Loaded += (_, _) => PwdMaster.Focus();
    }

    private string CurrentPassword =>
        BtnReveal.IsChecked == true ? TxtMasterRevealed.Text : PwdMaster.Password;

    private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        var password = CurrentPassword;
        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter your master password.");
            return;
        }

        BtnUnlock.IsEnabled = false;
        try
        {
            bool unlocked = await _credentialStore.UnlockWithPasswordAsync(password);
            if (unlocked)
            {
                DialogResult = true;
                return;
            }

            ShowError("Incorrect master password. Please try again.");
            PwdMaster.SelectAll();
            PwdMaster.Focus();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to unlock vault: {ex.Message}");
        }
        finally
        {
            BtnUnlock.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void ShowError(string message)
    {
        TxtError.Text       = message;
        TxtError.Visibility = Visibility.Visible;
    }

    private void PwdMaster_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        _syncingPassword       = true;
        TxtMasterRevealed.Text = PwdMaster.Password;
        TxtError.Visibility    = Visibility.Collapsed;
        _syncingPassword       = false;
    }

    private void TxtMasterRevealed_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncingPassword) return;
        _syncingPassword   = true;
        PwdMaster.Password = TxtMasterRevealed.Text;
        TxtError.Visibility = Visibility.Collapsed;
        _syncingPassword   = false;
    }

    private void BtnReveal_CheckedChanged(object sender, RoutedEventArgs e)
    {
        bool revealed = BtnReveal.IsChecked == true;
        if (revealed)
        {
            TxtMasterRevealed.Text       = PwdMaster.Password;
            TxtMasterRevealed.Visibility = Visibility.Visible;
            PwdMaster.Visibility         = Visibility.Collapsed;
            TxtMasterRevealed.Focus();
            TxtMasterRevealed.CaretIndex = TxtMasterRevealed.Text.Length;
        }
        else
        {
            PwdMaster.Password           = TxtMasterRevealed.Text;
            PwdMaster.Visibility         = Visibility.Visible;
            TxtMasterRevealed.Visibility = Visibility.Collapsed;
            PwdMaster.Focus();
        }
    }
}
