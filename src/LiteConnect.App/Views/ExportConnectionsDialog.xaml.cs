using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiteConnect.Core.Abstractions;

namespace LiteConnect.App.Views;

public partial class ExportConnectionsDialog : Window
{
    private readonly IExportService _exportService;
    private readonly IReadOnlyList<Guid>? _selectedConnectionIds;

    public ExportConnectionsDialog(
        IExportService exportService,
        IReadOnlyList<Guid>? selectedConnectionIds = null,
        string? selectedScopeLabel = null)
    {
        InitializeComponent();
        _exportService         = exportService ?? throw new ArgumentNullException(nameof(exportService));
        _selectedConnectionIds = selectedConnectionIds;

        if (selectedConnectionIds is { Count: > 0 })
        {
            RbScopeSelected.IsEnabled = true;
            RbScopeSelected.Content   = string.IsNullOrWhiteSpace(selectedScopeLabel)
                ? $"Selected connections ({selectedConnectionIds.Count} connection(s))"
                : $"Selected: \"{selectedScopeLabel}\" ({selectedConnectionIds.Count} connection(s))";
            RbScopeSelected.IsChecked = true;
        }
        else
        {
            RbScopeSelected.IsEnabled = false;
            RbScopeAll.IsChecked      = true;
        }

        UpdateControlsForFormat();
    }

    private ExportFormat GetSelectedFormat()
    {
        if (CboExportFormat?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            Enum.TryParse<ExportFormat>(tag, out var fmt))
        {
            return fmt;
        }
        return ExportFormat.EncryptedLcx;
    }

    private void CboExportFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateControlsForFormat();
    }

    private void ChkIncludeCredentials_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkIncludeSshKeys is not null)
        {
            ChkIncludeSshKeys.IsEnabled = ChkIncludeCredentials.IsChecked == true &&
                                          GetSelectedFormat() == ExportFormat.EncryptedLcx;
        }
    }

    private void UpdateControlsForFormat()
    {
        if (PanelExportPassword is null)
            return;

        var format = GetSelectedFormat();
        bool isEncryptedLcx = format == ExportFormat.EncryptedLcx;

        PanelExportPassword.Visibility  = isEncryptedLcx ? Visibility.Visible : Visibility.Collapsed;
        ChkIncludeCredentials.IsEnabled = isEncryptedLcx;
        ChkIncludeSshKeys.IsEnabled     = isEncryptedLcx && ChkIncludeCredentials.IsChecked == true;
        ChkIncludeRecording.IsEnabled   = format is ExportFormat.EncryptedLcx or ExportFormat.SessionsOnlyLcx;

        if (!isEncryptedLcx)
        {
            ChkIncludeCredentials.IsChecked = false;
            ChkIncludeSshKeys.IsChecked     = false;
        }
        else
        {
            ChkIncludeCredentials.IsChecked = true;
            ChkIncludeSshKeys.IsChecked     = true;
        }
    }

    private void PwdExport_PasswordChanged(object sender, RoutedEventArgs e)
    {
        string pwd = PwdExport.Password;
        int score = 0;
        if (pwd.Length >= 8)  score += 30;
        if (pwd.Length >= 12) score += 20;
        if (pwd.Any(char.IsUpper) && pwd.Any(char.IsLower)) score += 20;
        if (pwd.Any(char.IsDigit)) score += 15;
        if (pwd.Any(c => !char.IsLetterOrDigit(c))) score += 15;

        BarPasswordStrength.Value = score;
        TxtStrengthLabel.Text = score switch
        {
            0          => "Enter password",
            < 45       => "Weak",
            < 75       => "Medium",
            _          => "Strong"
        };
    }

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var format = GetSelectedFormat();
        string? password = null;

        if (format == ExportFormat.EncryptedLcx)
        {
            password = PwdExport.Password;
            if (string.IsNullOrEmpty(password) || password.Length < 4)
            {
                System.Windows.MessageBox.Show(
                    "Please enter an export password (at least 4 characters).",
                    "Export Connections",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                PwdExport.Focus();
                return;
            }

            if (password != PwdConfirm.Password)
            {
                System.Windows.MessageBox.Show(
                    "Export password and confirmation do not match.",
                    "Export Connections",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                PwdConfirm.Focus();
                return;
            }
        }

        var saveDlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Connections",
            FileName   = format switch
            {
                ExportFormat.Csv          => $"LiteConnect_Export_{DateTime.Now:yyyy-MM-dd}.csv",
                ExportFormat.MRemoteNGXml => $"confCons_{DateTime.Now:yyyy-MM-dd}.xml",
                _                         => $"LiteConnect_Backup_{DateTime.Now:yyyy-MM-dd}.lcx"
            },
            Filter = format switch
            {
                ExportFormat.Csv          => "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                ExportFormat.MRemoteNGXml => "mRemoteNG XML (*.xml)|*.xml|All Files (*.*)|*.*",
                _                         => "LiteConnect Bundle (*.lcx)|*.lcx|All Files (*.*)|*.*"
            }
        };

        if (saveDlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(saveDlg.FileName))
            return;

        var connectionIds = RbScopeSelected.IsChecked == true ? _selectedConnectionIds : null;
        var options = new ExportOptions(
            ConnectionIds:            connectionIds,
            Format:                   format,
            IncludeCredentials:       ChkIncludeCredentials.IsChecked == true,
            IncludeSshKeys:           ChkIncludeSshKeys.IsChecked == true,
            IncludeRecordingSettings: ChkIncludeRecording.IsChecked == true,
            PreserveFolderStructure:  ChkPreserveFolders.IsChecked == true,
            Password:                 password);

        try
        {
            BtnExport.IsEnabled = false;
            await using var fs = new FileStream(saveDlg.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            await _exportService.ExportAsync(options, fs);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            BtnExport.IsEnabled = true;
            System.Windows.MessageBox.Show(
                $"Export failed:\n\n{ex.Message}",
                "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
