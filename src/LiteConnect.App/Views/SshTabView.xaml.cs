using System.Windows;
using System.Windows.Media;
using LiteConnect.App.ViewModels;

// Avoid ambiguity between System.Windows.Controls.UserControl and System.Windows.Forms.UserControl
using WpfUserControl = System.Windows.Controls.UserControl;

namespace LiteConnect.App.Views;

/// <summary>
/// Hosts the <c>TerminalControl</c> for an active <see cref="SshSessionViewModel"/> tab.
/// </summary>
public partial class SshTabView : WpfUserControl
{
    public SshTabView()
    {
        InitializeComponent();
    }

    private void SshTabView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        TerminalHostContainer.Child = null;

        if (e.NewValue is SshSessionViewModel vm && vm.SessionControl is { } control)
        {
            if (VisualTreeHelper.GetParent(control) is System.Windows.Controls.Decorator parentDecorator)
            {
                parentDecorator.Child = null;
            }

            TerminalHostContainer.Child = control;
            control.Focus();
        }
    }

    private void SshTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        TerminalHostContainer.Child = null;
    }
}
