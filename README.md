# TinyRDP — Multi-Session

Run multiple isolated Windows desktops on one PC — one per account — so you can
run the same setup across several accounts at once.

## Download

[**TinyRDP.exe**](https://github.com/VirageRoblox/TinyRDP/releases/latest/download/TinyRDP.exe)
 — one file, no install. Right-click → **Run as administrator**.

## What it does

TinyRDP is a convenience wrapper around **RDPWrap** (the community
multi-session enabler). It does the fiddly parts for you:

- Installs / repairs RDPWrap and keeps its offsets matched to your Windows build
  (downloaded from the official source — TinyRDP itself embeds none of it).
- Creates isolated local accounts with strong passwords, locked to localhost.
- Fixes the settings that trip people up: keeps sessions rendering when
  minimized, pins a fixed resolution so pixel macros stay accurate, and strips
  desktop eye-candy so several sessions stay light on the host.
- Opens all your sessions with one click.

## Use

1. Run TinyRDP as administrator.
2. Click **Repair setup** if it isn't already **READY** (installs/updates RDPWrap).
3. Pick how many instances + a resolution, then click **Launch**.
4. Log your game + macro into each session, then minimize the windows.

Clean up any time with **Remove TinyRDP accounts** (deletes the accounts, their
profiles, and the firewall rule).

## Honest notes

- Multi-session is a Server/RDS-licensed Windows feature — using it on
  consumer Windows is against the Windows EULA. Use at your own risk.
- Windows Defender flags **RDPWrap** itself (not TinyRDP); allow it if prompted.
- A Windows Update can break RDPWrap until the community offsets catch up — then
  **Repair setup** fixes it in a click. Right after a brand-new Windows build,
  there can be a short wait before working offsets exist.
- Each session runs a full desktop plus your app — realistically 2–5 on a
  normal PC, not dozens.
- Windows may say *"unknown publisher"* — click **More info → Run anyway**. The
  full source is in this repo.
