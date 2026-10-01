using LiteConnect.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiteConnect.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// The ViewModel is injected via DI and set as DataContext.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    // ── Menu events ─────────────────────────────────────────────────────────────

    private void MenuItem_Exit_Click(object sender, System.Windows.RoutedEventArgs e)
        => System.Windows.Application.Current.Shutdown();

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
            e.Handled = true; // prevent the event from bubbling and expanding/collapsing the parent
        }
    }

    // ── Window loaded ───────────────────────────────────────────────────────────

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await ViewModel.TreeViewModel.LoadTreeAsync();
    }
}