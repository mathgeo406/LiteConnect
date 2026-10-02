using System.Windows;
using System.Windows.Media;
using LiteConnect.Protocols.Ssh;

namespace LiteConnect.App.Views;

/// <summary>
/// Modal dialog prompting the user to acceptonce, accept &amp; remember, or reject
/// an unknown or changed SSH server host key (Sprint 5.1).
/// </summary>
public partial class HostKeyVerificationDialog : Window
{
    public HostKeyVerificationDecision Decision { get; private set; } = HostKeyVerificationDecision.Reject;

    public HostKeyVerificationDialog(HostKeyVerificationPrompt prompt)
    {
        InitializeComponent();

        TxtHost.Text        = $"{prompt.Hostname}:{prompt.Port}";
        TxtAlgorithm.Text   = $"{prompt.KeyAlgorithm} ({prompt.KeyBitLength} bits)";
        TxtFingerprint.Text = prompt.Sha256Fingerprint;

        if (prompt.IsKeyMismatch)
        {
            Title = "WARNING: SSH Host Key Changed!";
            TxtHeader.Text = "⚠️ WARNING: Remote Host Identification Has Changed!";
            TxtHeader.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0x4C, 0x4C));
            TxtDescription.Text =
                "The host key presented by this server does NOT match the fingerprint previously trusted in KnownHosts. " +
                "This could indicate a man-in-the-middle attack or that the server's SSH host key was regenerated.";

            LblPrevFingerprint.Visibility = Visibility.Visible;
            TxtPrevFingerprint.Visibility = Visibility.Visible;
            TxtPrevFingerprint.Text       = prompt.PreviousFingerprint ?? "(unknown)";
        }
        else
        {
            TxtDescription.Text =
                "The authenticity of this host cannot be established automatically because it is not yet in your KnownHosts list. " +
                "Do you want to trust this host key and continue connecting?";
        }
    }

    private void BtnReject_Click(object sender, RoutedEventArgs e)
    {
        Decision = HostKeyVerificationDecision.Reject;
        DialogResult = false;
    }

    private void BtnAcceptOnce_Click(object sender, RoutedEventArgs e)
    {
        Decision = HostKeyVerificationDecision.AcceptOnce;
        DialogResult = true;
    }

    private void BtnAcceptRemember_Click(object sender, RoutedEventArgs e)
    {
        Decision = HostKeyVerificationDecision.AcceptAndRemember;
        DialogResult = true;
    }
}
