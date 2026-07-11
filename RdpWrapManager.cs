using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Win32;

namespace TinyRDP;

/// <summary>How healthy the RDPWrap multi-session setup is right now.</summary>
public enum RdpWrapState
{
    /// <summary>No wrapper installed — TermService still uses the stock termsrv.dll.</summary>
    NotInstalled,

    /// <summary>Installed, but rdpwrap.ini has no offsets for the running termsrv.dll
    /// version (the classic "broke after a Windows Update" state). Needs a repair.</summary>
    NeedsUpdate,

    /// <summary>Installed and the ini covers the current build — multi-session should work.</summary>
    Ready
}

/// <summary>
/// Detects, installs, and repairs RDPWrap. This app embeds none of RDPWrap: the
/// wrapper binaries and the continuously-updated offset ini are pulled from the
/// official community sources at runtime, so TinyRDP.exe itself stays AV-clean.
/// </summary>
public sealed class RdpWrapManager
{
    // Actively-maintained fork + its separate, continuously-updated offset ini.
    private const string ReleaseApi =
        "https://api.github.com/repos/sebaxakerhtc/rdpwrap/releases/latest";
    private const string IniRawUrl =
        "https://raw.githubusercontent.com/sebaxakerhtc/rdpwrap.ini/master/rdpwrap.ini";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RDP Wrapper");

    private static string WrapDll   => Path.Combine(InstallDir, "rdpwrap.dll");
    private static string IniPath    => Path.Combine(InstallDir, "rdpwrap.ini");
    private static string TermSrvDll => Path.Combine(Environment.SystemDirectory, "termsrv.dll");

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient();
        // GitHub's API rejects requests without a User-Agent.
        h.DefaultRequestHeaders.UserAgent.ParseAdd("TinyRDP");
        h.Timeout = TimeSpan.FromMinutes(2);
        return h;
    }

    /// <summary>
    /// The running Terminal Services DLL version as "10.0.26100.8115". Built from
    /// the numeric parts, NOT FileVersion — that string carries a "(WinBuild…)"
    /// suffix which would never match the ini's "[10.0.26100.8115]" section header.
    /// </summary>
    public string TermSrvVersion
    {
        get
        {
            if (!File.Exists(TermSrvDll)) return "";
            var fv = FileVersionInfo.GetVersionInfo(TermSrvDll);
            return $"{fv.FileMajorPart}.{fv.FileMinorPart}.{fv.FileBuildPart}.{fv.FilePrivatePart}";
        }
    }

    /// <summary>True when the wrapper is installed AND wired into the TermService.</summary>
    public bool IsInstalled => File.Exists(WrapDll) && ServiceDllPointsToWrapper();

    private static bool ServiceDllPointsToWrapper()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            return k?.GetValue("ServiceDll") is string dll
                && dll.Contains("rdpwrap.dll", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether the installed ini contains an offset section for the running
    /// termsrv.dll version — the practical "is my build supported?" test.
    /// </summary>
    public bool IniSupportsCurrentVersion()
    {
        var v = TermSrvVersion;
        if (string.IsNullOrEmpty(v) || !File.Exists(IniPath)) return false;
        string header = $"[{v}]";
        foreach (var line in File.ReadLines(IniPath))
            if (line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public RdpWrapState CheckState()
    {
        if (!IsInstalled) return RdpWrapState.NotInstalled;
        // Ready means BOTH: the offsets cover this build AND the wrapper is
        // actually loaded. The offsets alone can read "supported" while the
        // service is still running stock termsrv — which looks ready but isn't.
        if (!IniSupportsCurrentVersion()) return RdpWrapState.NeedsUpdate;
        return IsWrapperLoaded() ? RdpWrapState.Ready : RdpWrapState.NeedsUpdate;
    }

    /// <summary>
    /// Installs RDPWrap if needed and refreshes the offset ini so it covers the
    /// current Windows build, then restarts Terminal Services. Idempotent — safe
    /// to run as a "Repair" button whether or not anything is installed yet.
    /// </summary>
    public async Task InstallOrRepairAsync(IProgress<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(InstallDir);

        // Grab the fresh offsets over the network BEFORE we touch the service,
        // so the service is down for the shortest possible time.
        log.Report("Fetching the latest offsets for your Windows build…");
        string iniText = await Http.GetStringAsync(IniRawUrl, ct);

        // Only run the installer for a genuinely fresh setup. When the wrapper is
        // already installed (dll present + wired into TermService), a "repair" is
        // just refreshing the offsets — no download, no installer needed.
        if (!IsInstalled)
        {
            log.Report("Downloading and running the RDPWrap installer…");
            await DownloadAndRunInstallerAsync(log, ct);
        }

        // Bring the service fully DOWN before writing: this both releases the ini
        // lock and — crucially — forces the wrapper (rdpwrap.dll) to actually load
        // on the next start. A plain "net stop" usually fails while the RDP
        // listener is up, so if it's still running we force-kill its svchost.
        log.Report("Stopping Remote Desktop service…");
        Run("net.exe", "stop termservice /y", null, tolerateFailure: true);
        if (IsServiceRunning("TermService"))
        {
            log.Report("Forcing the service to reload…");
            Run("taskkill.exe", "/F /FI \"SERVICES eq TermService\"", null, tolerateFailure: true);
            Thread.Sleep(1500);
        }

        log.Report("Writing offsets…");
        if (!TryWriteIni(iniText))
        {
            Run("net.exe", "start termservice", null, tolerateFailure: true);
            throw new IOException(
                "Couldn't update rdpwrap.ini — it's still locked. Reboot, then run Repair again.");
        }

        log.Report("Starting Remote Desktop service…");
        Run("net.exe", "start termservice", null, tolerateFailure: true);
        Thread.Sleep(2000);

        // Confirm the wrapper actually loaded this time.
        log.Report(IsWrapperLoaded()
            ? "Done — the wrapper is active. Click Launch to open your sessions."
            : "Offsets updated, but the wrapper still isn't active. Reboot once, then click Launch.");
    }

    /// <summary>True if TermService's process currently has rdpwrap.dll loaded.</summary>
    public bool IsWrapperLoaded()
    {
        int pid = TermServicePid();
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            foreach (ProcessModule m in p.Modules)
                if (string.Equals(m.ModuleName, "rdpwrap.dll", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { /* module enumeration can be denied; treat as "can't confirm" */ }
        return false;
    }

    private static int TermServicePid()
    {
        var (_, output) = RunCapture("sc.exe", "queryex TermService");
        var m = System.Text.RegularExpressions.Regex.Match(output, @"PID\s*:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static bool IsServiceRunning(string name)
    {
        var (_, output) = RunCapture("sc.exe", $"query {name}");
        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static (int code, string output) RunCapture(string exe, string args)
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
        if (p is null) return (-1, "");
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    /// <summary>
    /// Writes the ini, retrying briefly: even after "net stop", the service host
    /// can take a moment to release its file handle.
    /// </summary>
    private static bool TryWriteIni(string text)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try { File.WriteAllText(IniPath, text); return true; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
        return false;
    }

    /// <summary>
    /// Downloads the fork's all-in-one installer (RDPW_Installer.exe) and runs it.
    /// It handles copying the wrapper dll, wiring the TermService, and the initial
    /// ini. Only used for a fresh setup; an existing install just gets the offsets
    /// refreshed.
    /// </summary>
    private async Task DownloadAndRunInstallerAsync(IProgress<string> log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(ReleaseApi, ct));
        string? url = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("RDPW_Installer", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

        if (url is null)
            throw new InvalidOperationException(
                "Couldn't find the RDPWrap installer download. Install RDPWrap manually, then use Repair.");

        string tmp = Path.Combine(Path.GetTempPath(), "tinyrdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string exe = Path.Combine(tmp, "RDPW_Installer.exe");
        await File.WriteAllBytesAsync(exe, await Http.GetByteArrayAsync(url, ct), ct);

        Run(exe, "", tmp);   // installer wires up the wrapper + service

        try { Directory.Delete(tmp, true); } catch { /* temp cleanup is best-effort */ }
    }

    private static int Run(string exe, string args, string? workingDir, bool tolerateFailure = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir ?? Environment.SystemDirectory
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
