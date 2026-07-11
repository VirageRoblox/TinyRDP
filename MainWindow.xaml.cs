using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TinyRDP;

public partial class MainWindow : Window
{
    private readonly RdpWrapManager _rdp = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshStatus();
    }

    private void RefreshStatus()
    {
        var state = _rdp.CheckState();
        string ver = _rdp.TermSrvVersion;

        switch (state)
        {
            case RdpWrapState.Ready:
                StatusText.Text = "Multi-session: READY";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x1b, 0x7f, 0x2b));
                HintText.Text = $"RDPWrap is installed and covers your Windows build ({ver}).";
                LaunchBtn.IsEnabled = true;
                break;

            case RdpWrapState.NeedsUpdate:
                StatusText.Text = "Multi-session: NEEDS REPAIR";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xb8, 0x86, 0x00));
                HintText.Text = $"RDPWrap is installed but has no offsets for your build ({ver}). " +
                                "Click Repair setup to update it.";
                LaunchBtn.IsEnabled = false;
                break;

            default: // NotInstalled
                StatusText.Text = "Multi-session: NOT SET UP";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xc6, 0x28, 0x28));
                HintText.Text = "RDPWrap isn't installed. Click Repair setup to download and install it.";
                LaunchBtn.IsEnabled = false;
                break;
        }
    }

    private void DigitsOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        var progress = new Progress<string>(msg => HintText.Text = msg);
        try
        {
            await _rdp.InstallOrRepairAsync(progress);
        }
        catch (Exception ex)
        {
            HintText.Text = "Repair failed: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void SetBusy(bool busy)
    {
        RepairBtn.IsEnabled = !busy;
        LaunchBtn.IsEnabled = false;
        InstanceCountBox.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, "Launch arrives in a later phase.", "TinyRDP");
}
