using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TinyRDP;

public partial class MainWindow : Window
{
    private readonly RdpWrapManager _rdp = new();
    private readonly AccountManager _accounts = new();
    private readonly SessionSettings _session = new();
    private readonly RdpLauncher _launcher = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshStatus();
    }

    /// <summary>Refreshes both the status line and the descriptive hint (used on load).</summary>
    private void RefreshStatus()
    {
        var state = UpdateStatusLine();
        string ver = _rdp.TermSrvVersion;
        HintText.Text = state switch
        {
            RdpWrapState.Ready       => $"RDPWrap is installed and covers your Windows build ({ver}).",
            RdpWrapState.NeedsUpdate => $"RDPWrap is installed but has no offsets for your build ({ver}). "
                                        + "Click Repair setup to update it.",
            _                        => "RDPWrap isn't installed. Click Repair setup to download and install it."
        };
    }

    /// <summary>
    /// Updates only the coloured status line + Launch enablement. Kept separate
    /// from the hint so a Repair result/error message isn't clobbered when we
    /// re-check state afterwards.
    /// </summary>
    private RdpWrapState UpdateStatusLine()
    {
        var state = _rdp.CheckState();
        (string label, Color color, bool canLaunch) = state switch
        {
            RdpWrapState.Ready       => ("Multi-session: READY",        Color.FromRgb(0x1b, 0x7f, 0x2b), true),
            RdpWrapState.NeedsUpdate => ("Multi-session: NEEDS REPAIR", Color.FromRgb(0xb8, 0x86, 0x00), false),
            _                        => ("Multi-session: NOT SET UP",   Color.FromRgb(0xc6, 0x28, 0x28), false)
        };
        StatusText.Text = label;
        StatusText.Foreground = new SolidColorBrush(color);
        LaunchBtn.IsEnabled = canLaunch;
        return state;
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
            // Refresh only the status line — keep the repair's final message/error.
            UpdateStatusLine();
        }
    }

    private void SetBusy(bool busy)
    {
        RepairBtn.IsEnabled = !busy;
        LaunchBtn.IsEnabled = false;
        InstanceCountBox.IsEnabled = !busy;
        ResolutionBox.IsEnabled = !busy;
        RemoveLink.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    private int InstanceCount()
        => int.TryParse(InstanceCountBox.Text, out int n) ? Math.Clamp(n, 1, 20) : 1;

    /// <summary>Reads the resolution dropdown into the session settings.</summary>
    private void ReadResolution()
    {
        string sel = (ResolutionBox.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        if (sel.StartsWith("Full", StringComparison.OrdinalIgnoreCase))
        {
            _session.FullScreen = true;
        }
        else
        {
            var m = Regex.Match(sel, @"(\d+)\D+(\d+)");
            if (m.Success)
            {
                _session.FullScreen = false;
                _session.Width = int.Parse(m.Groups[1].Value);
                _session.Height = int.Parse(m.Groups[2].Value);
            }
        }
    }

    // Full flow: prepare accounts + firewall + session tweaks, then open one RDP
    // session per account.
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        int count = InstanceCount();
        ReadResolution();

        var confirm = MessageBox.Show(this,
            $"TinyRDP will create {count} local account(s) (TinyRDP1…{count}), block external " +
            $"RDP, then open {count} Remote Desktop session(s) on this PC at {_session.Describe()}." +
            "\n\nContinue?",
            "TinyRDP", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        SetBusy(true);
        var progress = new Progress<string>(m => HintText.Text = m);
        try
        {
            int launched = await Task.Run(() =>
            {
                _session.ApplyMinimizeRenderFix(progress);
                _accounts.ApplyFirewallLockdown(progress);
                var accts = _accounts.EnsureAccounts(count, progress);
                return _launcher.LaunchAll(accts, _session, progress);
            });
            HintText.Text = $"Opened {launched} session(s) at {_session.Describe()}. " +
                            "Launch your game + macro inside each, then minimize the windows.";
        }
        catch (Exception ex) { HintText.Text = "Launch failed: " + ex.Message; }
        finally { SetBusy(false); UpdateStatusLine(); }
    }

    private async void RemoveAccounts_Click(object sender, RoutedEventArgs e)
    {
        var tracked = _accounts.TrackedAccounts();
        if (tracked.Count == 0)
        {
            MessageBox.Show(this, "No TinyRDP accounts to remove.", "TinyRDP");
            return;
        }
        var confirm = MessageBox.Show(this,
            $"Delete {tracked.Count} TinyRDP account(s) and remove the RDP firewall lock?",
            "TinyRDP", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        SetBusy(true);
        var progress = new Progress<string>(m => HintText.Text = m);
        try
        {
            await Task.Run(() =>
            {
                _accounts.RemoveAll(progress);
                _accounts.RemoveFirewallLockdown(progress);
                ((IProgress<string>)progress).Report("Removed TinyRDP accounts and firewall lock.");
            });
        }
        catch (Exception ex) { HintText.Text = "Remove failed: " + ex.Message; }
        finally { SetBusy(false); UpdateStatusLine(); }
    }
}
