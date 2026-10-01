using System.Windows;
using System.Windows.Media;
using LiteConnect.Core.Models;
using LiteConnect.Core.Services;

namespace LiteConnect.App.Views;

/// <summary>
/// Interaction logic for <see cref="SecuritySettingsDialog"/> (Sprint 4.3, 4.4, 4.6).
/// </summary>
public partial class SecuritySettingsDialog : Window
{
    private readonly SqlCipherCredentialStore _store;
    private bool _initializing;

    public bool VaultWasLocked { get; private set; }

    public SecuritySettingsDialog(SqlCipherCredentialStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));

        LoadCurrentState();
    }

    private void LoadCurrentState()
    {
        _initializing = true;

        bool isMaster = _store.Mode == EncryptionMode.MasterPassword;
        ChkEnableMasterPassword.IsChecked = isMaster;
        TxtAutoLockMinutes.Text           = _store.AutoLockMinutes.ToString();

        UpdateUiPanels();
        _initializing = false;
    }

    private void UpdateUiPanels()
    {
        bool wasMaster = _store.Mode == EncryptionMode.MasterPassword;
        bool wantMaster = ChkEnableMasterPassword.IsChecked == true;

        TxtModeSummary.Text = wasMaster
            ? "Current Mode: Master Password (portable SQLCipher encryption; requires password on launch)."
            : "Current Mode: Windows DPAPI (zero-prompt encryption tied to your current Windows account).";

        if (!wasMaster && wantMaster)
        {
            // Switching DPAPI -> Master Password
            PanelCurrentPassword.Visibility = Visibility.Collapsed;
            PanelNewPassword.Visibility     = Visibility.Visible;
            LblNewPasswordHeader.Text       = "SET NEW MASTER PASSWORD";
            PanelAutoLock.Visibility        = Visibility.Visible;
        }
        else if (wasMaster && !wantMaster)
        {
            // Switching Master Password -> DPAPI (require current password verification)
            PanelCurrentPassword.Visibility = Visibility.Visible;
            PanelNewPassword.Visibility     = Visibility.Collapsed;
            PanelAutoLock.Visibility        = Visibility.Collapsed;
        }
        else if (wasMaster && wantMaster)
        {
            // Already in Master Password mode (can change password or auto-lock minutes)
            PanelCurrentPassword.Visibility = Visibility.Visible;
            PanelNewPassword.Visibility     = Visibility.Visible;
            LblNewPasswordHeader.Text       = "CHANGE MASTER PASSWORD (LEAVE BLANK TO KEEP CURRENT)";
            PanelAutoLock.Visibility        = Visibility.Visible;
        }
        else
        {
            // Remaining in DPAPI mode
            PanelCurrentPassword.Visibility = Visibility.Collapsed;
            PanelNewPassword.Visibility     = Visibility.Collapsed;
            PanelAutoLock.Visibility        = Visibility.Collapsed;
        }
    }

    private void ChkEnableMasterPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        TxtStatus.Visibility = Visibility.Collapsed;
        UpdateUiPanels();
    }

    private void PwdNew_PasswordChanged(object sender, RoutedEventArgs e)
    {
        string pwd = PwdNew.Password;
        int score = CalculatePasswordScore(pwd);

        BarStrength.Value = score;
        (string label, System.Windows.Media.Color color) = score switch
        {
            0          => ("Strength: —",      System.Windows.Media.Color.FromRgb(0x85, 0x85, 0x85)),
            < 40       => ("Weak",             System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55)),
            < 70       => ("Fair",             System.Windows.Media.Color.FromRgb(0xFF, 0xB8, 0x4D)),
            < 90       => ("Good",             System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0)),
            _          => ("Strong",           System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50))
        };

        TxtStrength.Text       = label;
        TxtStrength.Foreground = new SolidColorBrush(color);
        BarStrength.Foreground = new SolidColorBrush(color);
    }

    private static int CalculatePasswordScore(string pwd)
    {
        if (string.IsNullOrEmpty(pwd)) return 0;

        int score = Math.Min(40, pwd.Length * 4);
        if (pwd.Any(char.IsUpper)) score += 15;
        if (pwd.Any(char.IsLower)) score += 15;
        if (pwd.Any(char.IsDigit)) score += 15;
        if (pwd.Any(c => !char.IsLetterOrDigit(c))) score += 15;
        return Math.Clamp(score, 0, 100);
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        TxtStatus.Visibility = Visibility.Collapsed;
        BtnSave.IsEnabled    = false;

        try
        {
            bool wasMaster  = _store.Mode == EncryptionMode.MasterPassword;
            bool wantMaster = ChkEnableMasterPassword.IsChecked == true;

            if (!wasMaster && wantMaster)
            {
                // DPAPI -> Master Password
                if (string.IsNullOrWhiteSpace(PwdNew.Password))
                {
                    ShowError("Please enter a new master password.");
                    return;
                }
                if (PwdNew.Password != PwdConfirm.Password)
                {
                    ShowError("New password and confirmation do not match.");
                    return;
                }

                await _store.SwitchModeAsync(EncryptionMode.MasterPassword, PwdNew.Password);
                await SaveAutoLockMinutesAsync();
                DialogResult = true;
            }
            else if (wasMaster && !wantMaster)
            {
                // Master Password -> DPAPI (verify current password first)
                if (string.IsNullOrEmpty(PwdCurrent.Password))
                {
                    ShowError("Please enter your current master password to switch back to DPAPI.");
                    return;
                }

                bool valid = await _store.UnlockWithPasswordAsync(PwdCurrent.Password);
                if (!valid)
                {
                    ShowError("Current master password is incorrect.");
                    return;
                }

                await _store.SwitchModeAsync(EncryptionMode.Dpapi);
                DialogResult = true;
            }
            else if (wasMaster && wantMaster)
            {
                // In Master Password mode: optionally change password, and save auto-lock minutes
                if (!string.IsNullOrEmpty(PwdNew.Password) || !string.IsNullOrEmpty(PwdConfirm.Password))
                {
                    if (string.IsNullOrEmpty(PwdCurrent.Password))
                    {
                        ShowError("Please enter your current master password to change it.");
                        return;
                    }
                    if (PwdNew.Password != PwdConfirm.Password)
                    {
                        ShowError("New password and confirmation do not match.");
                        return;
                    }

                    await _store.ChangeMasterPasswordAsync(PwdCurrent.Password, PwdNew.Password);
                }

                await SaveAutoLockMinutesAsync();
                DialogResult = true;
            }
            else
            {
                DialogResult = true;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            BtnSave.IsEnabled = true;
        }
    }

    private async Task SaveAutoLockMinutesAsync()
    {
        if (int.TryParse(TxtAutoLockMinutes.Text, out int mins) && mins > 0)
        {
            await _store.SetAutoLockMinutesAsync(mins);
        }
    }

    private void BtnLockNow_Click(object sender, RoutedEventArgs e)
    {
        _store.Lock();
        VaultWasLocked = true;
        DialogResult   = true;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void ShowError(string message)
    {
        TxtStatus.Text       = message;
        TxtStatus.Visibility = Visibility.Visible;
    }
}
