using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace TinyRDP;

/// <summary>
/// Writes an .rdp file per account and opens each in mstsc, producing one live
/// local desktop per account. All sessions target loopback (127.0.0.x), so they
/// never leave the machine and the firewall lock in AccountManager applies.
/// </summary>
public sealed class RdpLauncher
{
    private static string SessionDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TinyRDP", "sessions");

    public int LaunchAll(IReadOnlyList<TinyAccount> accounts, SessionSettings s, IProgress<string> log)
    {
        Directory.CreateDirectory(SessionDir);
        int launched = 0;

        for (int i = 0; i < accounts.Count; i++)
        {
            var acct = accounts[i];
            // Distinct loopback address per session so mstsc treats each as its
            // own connection (the whole 127.0.0.0/8 range is local).
            string address = $"127.0.0.{i + 2}";
            string rdpPath = Path.Combine(SessionDir, acct.Username + ".rdp");

            File.WriteAllText(rdpPath, BuildRdp(address, acct, s));

            log.Report($"Opening session {i + 1} of {accounts.Count} ({acct.Username})…");
            Process.Start(new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = $"\"{rdpPath}\"",
                UseShellExecute = true
            });
            launched++;

            // Stagger so the logins don't stampede the session manager.
            Thread.Sleep(700);
        }

        return launched;
    }

    private static string BuildRdp(string address, TinyAccount acct, SessionSettings s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{address}");
        sb.AppendLine($"username:s:{acct.Username}");
        sb.AppendLine($"password 51:b:{Dpapi.ProtectRdpPassword(acct.Password)}");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("authentication level:i:0");   // connect without cert warnings (localhost)
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("administrative session:i:0");
        sb.AppendLine("use multimon:i:0");
        sb.AppendLine("redirectclipboard:i:1");
        sb.AppendLine("audiomode:i:2");              // don't play the session's audio locally

        // Performance: strip desktop eye-candy so the host isn't rendering
        // wallpaper / animations / transparency across every session at once.
        sb.AppendLine("disable wallpaper:i:1");
        sb.AppendLine("disable full window drag:i:1");
        sb.AppendLine("disable menu anims:i:1");
        sb.AppendLine("disable themes:i:1");
        sb.AppendLine("disable cursor setting:i:1");
        sb.AppendLine("bitmapcachepersistenable:i:1");
        sb.AppendLine("connection type:i:6");        // treat as fast LAN (it's loopback)
        sb.AppendLine("allow font smoothing:i:1");   // keep text crisp so OCR stays accurate

        if (s.FullScreen)
        {
            sb.AppendLine("screen mode id:i:2");
        }
        else
        {
            sb.AppendLine("screen mode id:i:1");
            sb.AppendLine($"desktopwidth:i:{s.Width}");
            sb.AppendLine($"desktopheight:i:{s.Height}");
            sb.AppendLine("smart sizing:i:0");       // keep 1:1 pixels so macros stay accurate
        }

        return sb.ToString();
    }
}
