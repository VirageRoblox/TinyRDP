using Microsoft.Win32;

namespace TinyRDP;

/// <summary>
/// The per-session tweaks that make RDP usable for background macroing: keep the
/// session rendering when its window is minimized, and pin a fixed resolution so
/// pixel-coordinate macros stay accurate across every session.
/// </summary>
public sealed class SessionSettings
{
    private const string TscKey = @"Software\Microsoft\Terminal Server Client";
    private const string MinimizeValue = "RemoteDesktop_SuppressWhenMinimized";

    /// <summary>Desktop width for each session (ignored when <see cref="FullScreen"/>).</summary>
    public int Width { get; set; } = 1920;

    /// <summary>Desktop height for each session (ignored when <see cref="FullScreen"/>).</summary>
    public int Height { get; set; } = 1080;

    public bool FullScreen { get; set; }

    public string Describe() => FullScreen ? "full screen" : $"{Width}×{Height}";

    /// <summary>
    /// Stops Windows from freezing an RDP session's graphics while its window is
    /// minimized — without this, a screen-reading macro pauses the moment you
    /// minimize the session. Value 2 = keep rendering. Per-user (HKCU) setting for
    /// whoever launches the sessions, which is the same account TinyRDP runs as.
    /// </summary>
    public void ApplyMinimizeRenderFix(IProgress<string> log)
    {
        log.Report("Keeping sessions rendering when minimized…");
        using var k = Registry.CurrentUser.CreateSubKey(TscKey);
        k.SetValue(MinimizeValue, 2, RegistryValueKind.DWord);
    }

    public bool IsMinimizeRenderFixApplied()
    {
        using var k = Registry.CurrentUser.OpenSubKey(TscKey);
        return k?.GetValue(MinimizeValue) is int v && v == 2;
    }
}
