using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TinyRDP;

/// <summary>A local Windows account TinyRDP owns, plus the password just set on it.</summary>
public sealed class TinyAccount
{
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
}

/// <summary>
/// Creates and tears down the throwaway local accounts each RDP session logs in
/// as, and locks RDP to localhost so those accounts can't be reached from the
/// network. Passwords are never written to disk — a fresh one is set each time
/// sessions are prepared; only the account NAMES are tracked (for clean removal).
/// </summary>
public sealed class AccountManager
{
    private const string Prefix = "TinyRDP";
    private const string FirewallRule = "TinyRDP - Block external RDP";
    private const string HideKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TinyRDP");
    private static string ConfigPath => Path.Combine(ConfigDir, "accounts.json");

    private sealed class Config { public List<string> Accounts { get; set; } = new(); }

    /// <summary>Names of accounts TinyRDP has created (survives restarts, for cleanup).</summary>
    public List<string> TrackedAccounts() => LoadConfig().Accounts;

    /// <summary>
    /// Ensures accounts <c>TinyRDP1..N</c> exist, (re)sets a strong password on
    /// each so the returned credentials are valid right now, and returns them.
    /// </summary>
    public List<TinyAccount> EnsureAccounts(int count, IProgress<string> log)
    {
        count = Math.Clamp(count, 1, 20);
        var cfg = LoadConfig();
        var result = new List<TinyAccount>();

        for (int i = 1; i <= count; i++)
        {
            string name = Prefix + i;
            string pwd = GeneratePassword();

            if (UserExists(name))
            {
                log.Report($"Updating account {name}…");
                Run("net.exe", $"user {name} {pwd}");
            }
            else
            {
                log.Report($"Creating account {name}…");
                Run("net.exe", $"user {name} {pwd} /add /y /expires:never");
                Run("net.exe", $"localgroup \"Remote Desktop Users\" {name} /add", tolerateFailure: true);
                Run("net.exe", $"localgroup Users {name} /add", tolerateFailure: true);
                // Don't nag about password expiry (best-effort; module is present on Win10+).
                Run("powershell.exe",
                    $"-NoProfile -Command \"Set-LocalUser -Name '{name}' -PasswordNeverExpires $true\"",
                    tolerateFailure: true);
                HideFromLogin(name, true);
            }

            if (!cfg.Accounts.Contains(name)) cfg.Accounts.Add(name);
            result.Add(new TinyAccount { Username = name, Password = pwd });
        }

        SaveConfig(cfg);
        return result;
    }

    /// <summary>
    /// Signs off every logged-in TinyRDP session. Closing an RDP window only
    /// *disconnects* the session — it keeps running and holds the profile open,
    /// so these pile up and block cleanup unless we actually log them off.
    /// </summary>
    public void SignOffSessions(IProgress<string> log)
    {
        var (_, output) = RunCapture("qwinsta.exe", "");
        // Rows look like:  "            TinyRDP2       2   Disc ..."  — grab the id
        // (the number right after the TinyRDPn username).
        var ids = new List<string>();
        foreach (Match m in Regex.Matches(output, @"TinyRDP\d+\s+(\d+)\s"))
            ids.Add(m.Groups[1].Value);

        foreach (var id in ids.Distinct())
        {
            log.Report($"Signing off session {id}…");
            Run("logoff.exe", id, tolerateFailure: true);
        }
        if (ids.Count > 0) Thread.Sleep(1500);   // let the profiles unload
    }

    /// <summary>
    /// Deletes every account TinyRDP created — signs off its sessions, removes the
    /// account, wipes its Windows profile folder, and sweeps any leftover
    /// TinyRDP* profile folders + registry so nothing accumulates over time.
    /// </summary>
    public void RemoveAll(IProgress<string> log)
    {
        SignOffSessions(log);   // must be logged off or the profiles won't delete

        var cfg = LoadConfig();
        foreach (var name in cfg.Accounts.ToList())
        {
            log.Report($"Removing account {name}…");
            RemoveUserProfile(name);   // wipe C:\Users\<name> while the SID still resolves
            Run("net.exe", $"user {name} /delete", tolerateFailure: true);
            HideFromLogin(name, false);
        }

        SweepLeftoverProfiles(log);   // orphaned TinyRDP1.WINDOWS etc. from past rounds
        cfg.Accounts.Clear();
        SaveConfig(cfg);
    }

    /// <summary>
    /// Removes any stray TinyRDP* profiles left on disk (e.g. TinyRDP1.WINDOWS
    /// created when an old folder blocked a fresh login) plus their ProfileList
    /// registry entries — the bits RemoveUserProfile can't match because no
    /// account points at them anymore.
    /// </summary>
    private static void SweepLeftoverProfiles(IProgress<string> log)
    {
        // Drop ProfileList registry entries whose path points at a TinyRDP profile.
        string ps =
            "Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList' | " +
            "ForEach-Object { $p = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ProfileImagePath; " +
            "if ($p -match '\\\\Users\\\\TinyRDP') { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue } }";
        Run("powershell.exe", "-NoProfile -Command \"" + ps + "\"", tolerateFailure: true);

        // Delete leftover folders on disk.
        try
        {
            string usersDir = Directory.GetParent(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!.FullName;
            foreach (var dir in Directory.GetDirectories(usersDir, "TinyRDP*"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    log.Report($"Deleted leftover {Path.GetFileName(dir)}…");
                }
                catch { /* still locked — a session may not have fully unloaded */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Deletes the account's Windows profile (the C:\Users\&lt;name&gt; folder and
    /// its ProfileList registry entry) via Win32_UserProfile, matched by SID so we
    /// can never delete the wrong folder. Runs before the account is deleted, while
    /// the SID still resolves. A profile that's currently in use won't delete —
    /// close that session first.
    /// </summary>
    private static void RemoveUserProfile(string name)
    {
        string ps =
            "$u = Get-LocalUser -Name '" + name + "' -ErrorAction SilentlyContinue; " +
            "if ($u) { Get-CimInstance Win32_UserProfile | " +
            "Where-Object { $_.SID -eq $u.SID.Value } | " +
            "Remove-CimInstance -ErrorAction SilentlyContinue }";
        Run("powershell.exe", "-NoProfile -Command \"" + ps + "\"", tolerateFailure: true);
    }

    // ── Firewall ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Blocks inbound RDP (TCP/UDP 3389) from the network. Loopback traffic is
    /// exempt from Windows Firewall, so localhost sessions still work — this only
    /// stops the freshly-created accounts from being reachable from outside.
    /// </summary>
    public void ApplyFirewallLockdown(IProgress<string> log)
    {
        if (FirewallRuleExists()) return;
        log.Report("Locking RDP to this PC only…");
        Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=block " +
            "protocol=TCP localport=3389", tolerateFailure: true);
        Run("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=block " +
            "protocol=UDP localport=3389", tolerateFailure: true);
    }

    public void RemoveFirewallLockdown(IProgress<string> log)
    {
        log.Report("Removing RDP firewall lock…");
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"",
            tolerateFailure: true);
    }

    public bool FirewallRuleExists() =>
        Run("netsh.exe", $"advfirewall firewall show rule name=\"{FirewallRule}\"",
            tolerateFailure: true) == 0;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool UserExists(string name) =>
        Run("net.exe", $"user {name}", tolerateFailure: true) == 0;

    private static void HideFromLogin(string name, bool hide)
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(HideKey);
            if (hide) k.SetValue(name, 0, RegistryValueKind.DWord);
            else k.DeleteValue(name, throwOnMissingValue: false);
        }
        catch { /* cosmetic — a visible account still works */ }
    }

    /// <summary>16-char password with upper/lower/digit/symbol so it meets policy.</summary>
    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digit = "23456789";
        const string sym   = "!@#$%^&*-_=+";
        string all = upper + lower + digit + sym;

        var chars = new List<char>
        {
            Pick(upper), Pick(lower), Pick(digit), Pick(sym)
        };
        while (chars.Count < 16) chars.Add(Pick(all));

        // Fisher–Yates shuffle so the guaranteed-class chars aren't always first.
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());

        static char Pick(string s) => s[RandomNumberGenerator.GetInt32(s.Length)];
    }

    private static Config LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new Config();
        }
        catch { /* fall through to empty */ }
        return new Config();
    }

    private static void SaveConfig(Config cfg)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath,
            JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int Run(string exe, string args, bool tolerateFailure = false)
        => RunCapture(exe, args, tolerateFailure).code;

    private static (int code, string output) RunCapture(string exe, string args, bool tolerateFailure = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            if (tolerateFailure) return (-1, "");
            throw new InvalidOperationException($"Failed to start {exe}");
        }
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }
}
