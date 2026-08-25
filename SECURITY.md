# Security & trust

This document explains how installs and updates work, so you can decide whether to use the pre-built releases or build from source.

## Distribution model

| Channel | What you get |
|---------|----------------|
| [GitHub `main`](https://github.com/WAY29/Gamehelper/tree/main) | Full source (clone or download ZIP) |
| [GitHub Releases](https://github.com/WAY29/Gamehelper/releases) | Pre-built binaries (`GameHelperDownloader.exe`, full ZIP) |
| In-app auto-update | Same signed release ZIP as on GitHub Releases |

You do **not** have to use auto-update. Manual install from the full ZIP or a self-built `publish\` folder works the same.

## Auto-update (what it does)

1. The launcher downloads **`manifest.json`** and **`manifest.sig`** from GitHub Releases.
2. The manifest signature is checked with an **RSA public key embedded in the launcher** (`UpdateManifestVerifier`).
3. Each file in the manifest has a **SHA256** hash; mismatches abort the update.
4. The update is delivered as **one ZIP** (not arbitrary DLL URLs).
5. User data is **not** shipped in the update package:
   - `configs/` (core settings, `plugins.json`)
   - `Plugins/*/config/` (per-plugin settings)

You still trust the **maintainer** who signs releases — the same trust model as any overlay or game tool that is not independently audited.

## Build from source (maximum control)

```powershell
git clone https://github.com/WAY29/Gamehelper.git
cd Gamehelper
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

Run `publish\GameHelper.exe`. No downloader or auto-update required.

## Windows Defender and antivirus (false positives)

Unsigned game overlays that **download**, **extract**, and **load DLLs** are commonly flagged by Windows Defender and other AV. That is **expected** for tools like GameHelper — it does not necessarily mean malware.

### Common detection names

| Name | Typical trigger |
|------|-----------------|
| `Trojan:Win32/Wacatac.C!ml` | `GameHelper.App.dll` — overlay DLL load + memory reads |
| `Trojan:Win32/PowhidSubExec.B` | Auto-update installer — background script copies from `%TEMP%\GameHelperUpdate\` into your install folder |

Other vendors may use different names for the same behavior.

### Why GameHelper may not start

1. Defender **blocked** `GameHelper.exe`, `GameHelper.App.dll`, `cmd.exe`, or a `GameHelperUpdate` entry.
2. Open **Windows Security → Protection history**, find the block, then **Allow** / **Restore**, or add a **folder exclusion** for your GameHelper install path.
3. Do **not** mix files from different versions (e.g. old `GameHelper.App.dll` with a new launcher).
4. **Clean install:** download the [full ZIP](https://github.com/WAY29/Gamehelper/releases/latest), extract to a **new empty folder**, run `GameHelper.exe`.

You do **not** need to read source code to fix a block — use Protection history or a manual ZIP install.

### What auto-update does (why AV complains)

When you approve an update, the launcher:

1. Downloads a signed ZIP to `%TEMP%\GameHelperUpdate\<version>\`
2. Starts a small **background installer** (`cmd` + batch + `robocopy`) that waits for the launcher to exit, copies files into your install folder, and restarts GameHelper

That matches generic “dropper” heuristics. The same files are on GitHub Releases with SHA256 checks in the signed `manifest.json`.

### Release integrity

- `manifest.json` is **signed**; the launcher rejects tampered manifests.
- Each file must match the **SHA256** hash in the manifest.

You still trust whoever holds the signing key — the same model as any pre-built overlay.

## Reporting issues

Security or trust concerns: [GitHub Issues](https://github.com/WAY29/Gamehelper/issues) (no DMs required).

Please include version (`VERSION.txt`), install method (downloader / ZIP / self-built), and whether auto-update was used.
