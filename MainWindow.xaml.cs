using System.IO;
using System.Text.Json;
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
        Loaded += (_, _) => { LoadUiState(); RefreshStatus(); };
        Closing += (_, _) => SaveUiState();
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>Refreshes the status line, the descriptive hint, and the Remove link.</summary>
    private void RefreshStatus()
    {
        var state = UpdateStatusLine();
        string ver = _rdp.TermSrvVersion;
        int accts = _accounts.TrackedAccounts().Count;
        string acctNote = accts > 0 ? $" {accts} TinyRDP account(s) set up." : "";

        HintText.Text = state switch
        {
            RdpWrapState.Ready => $"Ready on Windows {ver}.{acctNote} Set how many instances and click Launch.",
            RdpWrapState.NeedsUpdate when _rdp.IniSupportsCurrentVersion()
                => "Installed, but the wrapper isn't running yet. Click Repair setup (a reboot may be needed).",
            RdpWrapState.NeedsUpdate
                => $"No offsets for your Windows build ({ver}) yet. Click Repair setup to update them.",
            _ => "RDPWrap isn't installed. Click Repair setup to download and install it."
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

    // ── Input ─────────────────────────────────────────────────────────────────

    private void DigitsOnly(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

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

    // ── Actions ───────────────────────────────────────────────────────────────

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

    // Full flow: prepare accounts + firewall + session tweaks, then open one RDP
    // session per account.
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        int count = InstanceCount();
        ReadResolution();
        SaveUiState();

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

    private void SetBusy(bool busy)
    {
        RepairBtn.IsEnabled = !busy;
        LaunchBtn.IsEnabled = false;   // re-enabled by UpdateStatusLine when Ready
        InstanceCountBox.IsEnabled = !busy;
        ResolutionBox.IsEnabled = !busy;
        RemoveLink.IsEnabled = !busy && _accounts.TrackedAccounts().Count > 0;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    // ── Remembered UI state ─────────────────────────────────────────────────────

    private sealed class UiState
    {
        public int Instances { get; set; } = 2;
        public int ResolutionIndex { get; set; }
    }

    private static string UiStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyRDP", "ui.json");

    private void LoadUiState()
    {
        try
        {
            if (!File.Exists(UiStatePath)) return;
            var s = JsonSerializer.Deserialize<UiState>(File.ReadAllText(UiStatePath));
            if (s is null) return;
            InstanceCountBox.Text = Math.Clamp(s.Instances, 1, 20).ToString();
            if (s.ResolutionIndex >= 0 && s.ResolutionIndex < ResolutionBox.Items.Count)
                ResolutionBox.SelectedIndex = s.ResolutionIndex;
        }
        catch { /* first run / malformed — keep defaults */ }
    }

    private void SaveUiState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiStatePath)!);
            var s = new UiState { Instances = InstanceCount(), ResolutionIndex = ResolutionBox.SelectedIndex };
            File.WriteAllText(UiStatePath, JsonSerializer.Serialize(s));
        }
        catch { /* non-fatal */ }
    }
}
