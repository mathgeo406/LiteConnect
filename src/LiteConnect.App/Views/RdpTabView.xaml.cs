using System.Windows;
using System.Windows.Controls;
using LiteConnect.App.ViewModels;

namespace LiteConnect.App.Views;

/// <summary>
/// Interaction logic for <see cref="RdpTabView"/>.
/// Manages attaching/detaching the <c>WindowsFormsHost</c> across tab switches
/// so multiple RDP tabs can be open simultaneously without breaking rendering.
/// </summary>
public partial class RdpTabView : System.Windows.Controls.UserControl
{
    public RdpTabView()
    {
        InitializeComponent();
    }

    private void RdpTabView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HostContainer.Child = null;

        if (e.NewValue is RdpSessionViewModel vm)
        {
            AttachSessionControl(vm.SessionControl);
        }
    }

    private void AttachSessionControl(FrameworkElement control)
    {
        // Detach from any previous parent Border/ContentControl before attaching
        if (control.Parent is Decorator oldDecorator && !ReferenceEquals(oldDecorator, HostContainer))
        {
            oldDecorator.Child = null;
        }
        else if (control.Parent is ContentControl oldContent)
        {
            oldContent.Content = null;
        }
        else if (control.Parent is System.Windows.Controls.Panel oldPanel)
        {
            oldPanel.Children.Remove(control);
        }

        HostContainer.Child = control;
    }

    private void RdpTabView_Unloaded(object sender, RoutedEventArgs e)
    {
        // Release visual parent reference when tab is switched away so a new RdpTabView can attach it
        HostContainer.Child = null;
    }

    private void RdpTabView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is RdpSessionViewModel vm && e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            vm.OnResize((int)e.NewSize.Width, (int)e.NewSize.Height);
        }
    }
}
