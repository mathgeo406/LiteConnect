using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.ImportExport;

namespace LiteConnect.App.Views;

public partial class ImportConnectionsDialog : Window
{
    public sealed class PreviewRowViewModel : INotifyPropertyChanged
    {
        private readonly ImportPreviewItem _item;

        public PreviewRowViewModel(ImportPreviewItem item)
        {
            _item = item;
        }

        public ImportPreviewItem Item => _item;

        public bool Selected
        {
            get => _item.Selected;
            set
            {
                if (_item.Selected != value)
                {
                    _item.Selected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Name => _item.Connection.Name;
        public string Protocol => _item.Connection.Protocol.ToString();
        public string HostAndPort => $"{_item.Connection.Hostname}:{_item.Connection.EffectivePort}";
        public string FolderPath => string.IsNullOrEmpty(_item.FolderPath) ? "(Root)" : _item.FolderPath;
        public bool HasConflict => _item.HasConflict;
        public string ConflictStatus => _item.HasConflict ? "⚠️ Conflict" : "✓ Ready";

        public ConflictResolution Resolution
        {
            get => _item.Resolution;
            set
            {
                if (_item.Resolution != value)
                {
                    _item.Resolution = value;
                    OnPropertyChanged();
                }
            }
        }

        public IReadOnlyList<ConflictResolution> ResolutionOptions { get; } =
            Enum.GetValues<ConflictResolution>();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    private readonly IImportService _importService;
    private readonly ObservableCollection<PreviewRowViewModel> _rows = [];
    private ImportPreview? _currentPreview;
    private ImportFileInfo? _currentFileInfo;
    private string? _selectedFilePath;

    public ImportResult? Result { get; private set; }

    public ImportConnectionsDialog(IImportService importService)
    {
        InitializeComponent();
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        LstPreview.ItemsSource = _rows;
    }

    private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var openDlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select Connections File to Import",
            Filter = "All Supported Files (*.lcx;*.xml;*.csv)|*.lcx;*.xml;*.csv|LiteConnect Bundle (*.lcx)|*.lcx|mRemoteNG XML (*.xml)|*.xml|CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"
        };

        if (openDlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(openDlg.FileName))
            return;

        _selectedFilePath = openDlg.FileName;
        TxtFilePath.Text  = _selectedFilePath;

        await InspectAndLoadFileAsync(_selectedFilePath);
    }

    private async Task InspectAndLoadFileAsync(string filePath)
    {
        _rows.Clear();
        _currentPreview     = null;
        BtnImport.IsEnabled = false;

        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _currentFileInfo = await _importService.InspectFileAsync(fs, filePath);

            PanelPassword.Visibility   = _currentFileInfo.IsEncrypted ? Visibility.Visible : Visibility.Collapsed;
            PanelCsvMapping.Visibility = _currentFileInfo.Format == ImportFormat.Csv ? Visibility.Visible : Visibility.Collapsed;

            if (_currentFileInfo.Format == ImportFormat.Csv && _currentFileInfo.CsvHeaders is { Count: > 0 } headers)
            {
                PopulateCsvComboBoxes(headers);
            }

            if (_currentFileInfo.IsEncrypted)
            {
                TxtSummary.Text = "Encrypted .lcx bundle detected — enter password and click Decrypt & Preview.";
                PwdImport.Focus();
                return;
            }

            await LoadPreviewAsync(filePath, password: null, csvMapping: null);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not inspect file:\n\n{ex.Message}",
                "Import Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void PopulateCsvComboBoxes(IReadOnlyList<string> headers)
    {
        var options = new List<string> { "(None)" };
        for (int i = 0; i < headers.Count; i++)
            options.Add($"{i}: {headers[i]}");

        var detected = CsvImportExportCodec.AutoDetectMapping(headers);

        SetupCombo(CboCsvName, options, detected.NameColumn);
        SetupCombo(CboCsvProtocol, options, detected.ProtocolColumn);
        SetupCombo(CboCsvHostname, options, detected.HostnameColumn);
        SetupCombo(CboCsvPort, options, detected.PortColumn);
        SetupCombo(CboCsvUsername, options, detected.UsernameColumn);
        SetupCombo(CboCsvFolder, options, detected.FolderPathColumn);
    }

    private static void SetupCombo(System.Windows.Controls.ComboBox combo, List<string> options, int colIndex)
    {
        combo.ItemsSource = options;
        combo.SelectedIndex = (colIndex >= 0 && colIndex + 1 < options.Count) ? colIndex + 1 : 0;
    }

    private async void BtnDecryptPreview_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedFilePath))
            return;

        await LoadPreviewAsync(_selectedFilePath, PwdImport.Password, csvMapping: null);
    }

    private async void BtnApplyCsvMapping_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedFilePath))
            return;

        var mapping = new CsvColumnMapping
        {
            NameColumn       = CboCsvName.SelectedIndex - 1,
            ProtocolColumn   = CboCsvProtocol.SelectedIndex - 1,
            HostnameColumn   = CboCsvHostname.SelectedIndex - 1,
            PortColumn       = CboCsvPort.SelectedIndex - 1,
            UsernameColumn   = CboCsvUsername.SelectedIndex - 1,
            DomainColumn     = -1,
            FolderPathColumn = CboCsvFolder.SelectedIndex - 1,
            NotesColumn      = -1,
            HasHeaderRow     = ChkCsvHasHeader.IsChecked == true
        };

        await LoadPreviewAsync(_selectedFilePath, password: null, csvMapping: mapping);
    }

    private async Task LoadPreviewAsync(string filePath, string? password, CsvColumnMapping? csvMapping)
    {
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _currentPreview = await _importService.PreviewAsync(fs, password, csvMapping, filePath);

            _rows.Clear();
            foreach (var item in _currentPreview.Items)
            {
                _rows.Add(new PreviewRowViewModel(item));
            }

            int conflicts = _currentPreview.Items.Count(i => i.HasConflict);
            TxtSummary.Text = conflicts > 0
                ? $"{_rows.Count} connection(s) found ({conflicts} conflict(s) detected)"
                : $"{_rows.Count} connection(s) ready to import";

            BtnImport.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to load preview:\n\n{ex.Message}",
                "Import Preview",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = true;
    }

    private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = false;
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPreview is null)
            return;

        try
        {
            BtnImport.IsEnabled = false;
            Result = await _importService.ImportAsync(_currentPreview, new ImportOptions());
            DialogResult = true;
        }
        catch (Exception ex)
        {
            BtnImport.IsEnabled = true;
            System.Windows.MessageBox.Show(
                $"Import failed:\n\n{ex.Message}",
                "Import Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
