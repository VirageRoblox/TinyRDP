using System.Windows;

namespace TinyRDP;

public partial class App : Application
{
    // Phase 1 — scaffold only. The RDPWrap / account / launch layers land in
    // later phases (see the spec). This exe embeds none of RDPWrap; it will
    // download the official fork at runtime so the binary stays AV-clean.
}
