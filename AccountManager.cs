using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
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

    /// <summary>Deletes every account TinyRDP created and un-hides them from login.</summary>
    public void RemoveAll(IProgress<string> log)
    {
        var cfg = LoadConfig();
        foreach (var name in cfg.Accounts.ToList())
        {
            log.Report($"Removing account {name}…");
            Run("net.exe", $"user {name} /delete", tolerateFailure: true);
            HideFromLogin(name, false);
        }
        cfg.Accounts.Clear();
        SaveConfig(cfg);
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
            if (tolerateFailure) return -1;
            throw new InvalidOperationException($"Failed to start {exe}");
        }
        p.WaitForExit();
        return p.ExitCode;
    }
}
