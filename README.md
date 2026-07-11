# TinyRDP — Multi-Session

Run multiple isolated Windows desktops on one PC — one per account — so you can
run the same setup across several accounts at once.

> **Status: early scaffold (v0.1.0).** Not usable yet. See the build phases below.

## How it will work

TinyRDP is a convenience wrapper around **RDPWrap** (the community
multi-session enabler). It does the fiddly parts for you:

- Installs / repairs RDPWrap and keeps its offsets matched to your Windows build
  (downloaded from the official source — TinyRDP itself embeds none of it).
- Creates isolated local accounts with strong passwords, locked to localhost.
- Fixes the settings that trip people up (keeps sessions rendering when
  minimized; pins a fixed resolution so pixel macros stay accurate).
- Launches all your sessions with one click.

## Honest notes

- Multi-session is a Server/RDS-licensed Windows feature — using it on
  consumer Windows is against the Windows EULA. Use at your own risk.
- Windows Defender flags RDPWrap itself; TinyRDP will guide you through it.
- A Windows Update can occasionally break RDPWrap until offsets update.
- Each session runs a full desktop plus your app — realistically 2–5 on a
  normal PC, not dozens.

## Build phases

1. **Scaffold** — app shell, admin manifest, branding ✅
2. **RDPWrap detect / install / offset repair** ✅
3. **Account creation + localhost firewall lockdown** ✅
4. **Session fixes (minimize-render key, fixed resolution)** ✅ (current)
5. One-click launch (DPAPI-encrypted `.rdp` + `mstsc`)
6. UI polish
7. Publish + VirusTotal + optional signing
