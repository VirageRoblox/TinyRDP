using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
    private static string InstallerExe => Path.Combine(InstallDir, "RDPWInst.exe");
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
        return IniSupportsCurrentVersion() ? RdpWrapState.Ready : RdpWrapState.NeedsUpdate;
    }

    /// <summary>
    /// Installs RDPWrap if needed and refreshes the offset ini so it covers the
    /// current Windows build, then restarts Terminal Services. Idempotent — safe
    /// to run as a "Repair" button whether or not anything is installed yet.
    /// </summary>
    public async Task InstallOrRepairAsync(IProgress<string> log, CancellationToken ct = default)
    {
        Directory.CreateDirectory(InstallDir);

        if (!File.Exists(InstallerExe) || !File.Exists(WrapDll))
        {
            log.Report("Downloading RDPWrap (official fork)…");
            await DownloadAndStageBinariesAsync(log, ct);
        }

        log.Report("Registering the wrapper with Terminal Services…");
        Run(InstallerExe, "-i -o", InstallDir);   // -i install, -o override existing

        log.Report("Fetching the latest offsets for your Windows build…");
        await UpdateIniAsync(ct);

        log.Report("Restarting Remote Desktop service…");
        RestartTermService(log);

        log.Report(CheckState() == RdpWrapState.Ready
            ? "Done — multi-session is ready."
            : "Installed, but this Windows build isn't in the offset list yet. "
              + "Try Repair again later, or reboot and re-check.");
    }

    /// <summary>Downloads the newest fork release zip and copies the wrapper files into place.</summary>
    private async Task DownloadAndStageBinariesAsync(IProgress<string> log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(ReleaseApi, ct));
        string? zipUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

        if (zipUrl is null)
            throw new InvalidOperationException(
                "Couldn't find a RDPWrap download. Install RDPWrap manually, then use Repair.");

        string tmp = Path.Combine(Path.GetTempPath(), "tinyrdp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string zip = Path.Combine(tmp, "rdpwrap.zip");

        byte[] bytes = await Http.GetByteArrayAsync(zipUrl, ct);
        await File.WriteAllBytesAsync(zip, bytes, ct);

        string extract = Path.Combine(tmp, "x");
        ZipFile.ExtractToDirectory(zip, extract);

        // The zip layout varies between releases, so find each file by name.
        foreach (var wanted in new[] { "RDPWInst.exe", "rdpwrap.dll", "rdpwrap.ini",
                                       "RDPConf.exe", "RDPCheck.exe" })
        {
            var found = Directory.EnumerateFiles(extract, wanted, SearchOption.AllDirectories)
                                 .FirstOrDefault();
            if (found != null)
                File.Copy(found, Path.Combine(InstallDir, wanted), overwrite: true);
        }

        try { Directory.Delete(tmp, true); } catch { /* temp cleanup is best-effort */ }
    }

    /// <summary>Overwrites the installed ini with the continuously-updated community offsets.</summary>
    private async Task UpdateIniAsync(CancellationToken ct)
    {
        string ini = await Http.GetStringAsync(IniRawUrl, ct);
        await File.WriteAllTextAsync(IniPath, ini, ct);
    }

    private static void RestartTermService(IProgress<string> log)
    {
        // TermService has dependents and active sessions, so a live restart can
        // fail; the wrapper still takes effect on next boot, so we tolerate it.
        Run("net.exe", "stop termservice /y", null, tolerateFailure: true);
        var r = Run("net.exe", "start termservice", null, tolerateFailure: true);
        if (r != 0)
            log.Report("Note: couldn't restart the service live — a reboot will finish it.");
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
