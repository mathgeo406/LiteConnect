using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiteConnect.App.ViewModels;

using WpfPoint = System.Windows.Point;

namespace LiteConnect.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// Handles tree double-click, Quick Connect shortcuts, and tab drag-and-drop / middle-click / rename.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private WpfPoint _tabDragStartPoint;
    private ISessionTabViewModel? _draggedTab;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // ── Global keyboard shortcuts for focus (Ctrl+T, Ctrl+F) ────────────────────

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Alt)) == 0)
        {
            if (e.Key == Key.T)
            {
                FocusQuickConnect();
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                FocusTreeSearch();
                e.Handled = true;
            }
        }
    }

    private void FocusQuickConnect()
    {
        CboQuickConnect.Focus();
        if (CboQuickConnect.Template.FindName("PART_EditableTextBox", CboQuickConnect) is System.Windows.Controls.TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void FocusTreeSearch()
    {
        ViewModel.IsTreePanelVisible = true;
        TxtTreeSearch.Focus();
        TxtTreeSearch.SelectAll();
    }

    // ── Menu events ─────────────────────────────────────────────────────────────

    private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
        => System.Windows.Application.Current.Shutdown();

    private void MenuItem_Find_Click(object sender, RoutedEventArgs e)
        => FocusTreeSearch();

    private void MenuItem_QuickConnect_Click(object sender, RoutedEventArgs e)
        => FocusQuickConnect();

    private void MenuItem_About_Click(object sender, RoutedEventArgs e)
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        var arch = RuntimeInformation.ProcessArchitecture;
        System.Windows.MessageBox.Show(
            this,
            $"LiteConnect v{ver} ({arch})\n\n" +
            "Lightweight, Portable Remote Connection Manager (SSH / RDP / VNC)\n" +
            "• Custom VT100/xterm Terminal Emulator & Session Recorder\n" +
            "• In-Process RDP ActiveX & RD Gateway Integration\n" +
            "• AES-256 SQLCipher Credential Vault (DPAPI & Master Password)\n" +
            "• Encrypted .lcx, mRemoteNG XML, and CSV Import/Export",
            "About LiteConnect",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void CboQuickConnect_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Return || e.Key == Key.Enter)
        {
            if (ViewModel.QuickConnectCommand.CanExecute(null))
            {
                ViewModel.QuickConnectCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    // ── Tab Header: Drag-and-Drop Reorder, Middle-Click Close, Double-Click Rename ─

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInsideButton(dep))
            return;

        if (sender is TabItem { DataContext: ISessionTabViewModel tab })
        {
            _tabDragStartPoint = e.GetPosition(this);
            _draggedTab        = tab;
        }
    }

    private void TabItem_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedTab is null)
            return;

        var currentPos = e.GetPosition(this);
        if (Math.Abs(currentPos.X - _tabDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(currentPos.Y - _tabDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is TabItem tabItem)
            {
                var tabToDrag = _draggedTab;
                _draggedTab = null;
                DragDrop.DoDragDrop(tabItem, tabToDrag, System.Windows.DragDropEffects.Move);
            }
        }
    }

    private void TabItem_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is TabItem { DataContext: ISessionTabViewModel targetTab })
        {
            if (e.Data.GetData(typeof(RdpSessionViewModel)) is ISessionTabViewModel rdpSource)
                ViewModel.MoveTab(rdpSource, targetTab);
            else if (e.Data.GetData(typeof(SshSessionViewModel)) is ISessionTabViewModel sshSource)
                ViewModel.MoveTab(sshSource, targetTab);
            else if (e.Data.GetData(typeof(PlaceholderTabViewModel)) is ISessionTabViewModel phSource)
                ViewModel.MoveTab(phSource, targetTab);
        }
    }

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Middle-click closes the tab (Sprint 8.2)
        if (e.ChangedButton == MouseButton.Middle &&
            sender is TabItem { DataContext: ISessionTabViewModel tab })
        {
            ViewModel.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    private void TabItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (sender is TabItem { DataContext: ISessionTabViewModel tab })
        {
            string? newName = PromptRenameTab(tab.DisplayName);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                tab.DisplayName = newName.Trim();
            }
            e.Handled = true;
        }
    }

    private string? PromptRenameTab(string currentName)
    {
        var win = new Window
        {
            Title                 = "Rename Tab",
            Width                 = 340,
            Height                = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode            = ResizeMode.NoResize,
            Owner                 = this,
            Background            = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x1E, 0x1E))
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        var txt = new System.Windows.Controls.TextBox
        {
            Text       = currentName,
            Padding    = new Thickness(6, 4, 6, 4),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = System.Windows.Media.Brushes.White
        };
        txt.SelectAll();

        var btnRow = new StackPanel
        {
            Orientation         = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin              = new Thickness(0, 14, 0, 0)
        };

        var btnOk = new System.Windows.Controls.Button
        {
            Content   = "OK",
            Width     = 75,
            Padding   = new Thickness(4),
            Margin    = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        btnOk.Click += (_, _) => win.DialogResult = true;

        var btnCancel = new System.Windows.Controls.Button
        {
            Content  = "Cancel",
            Width    = 75,
            Padding  = new Thickness(4),
            IsCancel = true
        };

        btnRow.Children.Add(btnOk);
        btnRow.Children.Add(btnCancel);
        panel.Children.Add(txt);
        panel.Children.Add(btnRow);
        win.Content = panel;
        win.Loaded += (_, _) => txt.Focus();

        return win.ShowDialog() == true ? txt.Text : null;
    }

    private static bool IsInsideButton(DependencyObject? dep)
    {
        while (dep is not null)
        {
            if (dep is System.Windows.Controls.Primitives.ButtonBase)
                return true;
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        }
        return false;
    }

    // ── Tree events ─────────────────────────────────────────────────────────────

    /// <summary>Keep SelectedNode on the TreeViewModel in sync with the TreeView selection.</summary>
    private void ConnectionTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeNodeViewModel node)
            ViewModel.TreeViewModel.SelectedNode = node;
    }

    /// <summary>Double-click on a connection node → open the session tab.</summary>
    private void TreeItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: TreeNodeViewModel node } && node.IsConnection)
        {
            ViewModel.TreeViewModel.OpenConnectionCommand.Execute(node);
            e.Handled = true;
        }
    }

    // ── Window loaded ───────────────────────────────────────────────────────────

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await ViewModel.TreeViewModel.LoadTreeAsync();
    }
}