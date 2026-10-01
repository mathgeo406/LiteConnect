using System.Windows;
using LiteConnect.Core.Services;

namespace LiteConnect.App.Views;

/// <summary>
/// Interaction logic for <see cref="DpapiPortabilityDialog"/> (Sprint 4.2).
/// </summary>
public partial class DpapiPortabilityDialog : Window
{
    private readonly SqlCipherCredentialStore _store;

    public DpapiPortabilityDialog(SqlCipherCredentialStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    private void Option_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (PanelNewMaster is null) return;
        PanelNewMaster.Visibility = RbMasterPassword.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void BtnContinue_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Visibility = Visibility.Collapsed;
        BtnContinue.IsEnabled = false;

        try
        {
            if (RbMasterPassword.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(PwdNew.Password))
                {
                    ShowError("Please enter a new master password.");
                    return;
                }

                if (PwdNew.Password != PwdConfirm.Password)
                {
                    ShowError("Passwords do not match.");
                    return;
                }

                await _store.RecoverDpapiToMasterPasswordAsync(PwdNew.Password);
            }
            else
            {
                await _store.ResetDpapiForCurrentUserAsync();
            }

            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError($"Recovery failed: {ex.Message}");
        }
        finally
        {
            BtnContinue.IsEnabled = true;
        }
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void ShowError(string message)
    {
        TxtError.Text       = message;
        TxtError.Visibility = Visibility.Visible;
    }
}
