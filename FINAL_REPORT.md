# Final report

## Delivery status

Antigravity Profile Hub is an experimental Windows desktop manager for multiple local Antigravity Desktop profiles. The repository includes a native Core library, CLI, WPF GUI, capability probe, safety documentation, and a portable packaging script. The final release status below is updated only after build and runtime checks.

## What was built

The repository now contains the Core profile/storage/process service, CLI commands, WPF GUI project, capability probes, safety documentation, automated Core/process checks, and the portable packaging script. The experimental package is buildable and runnable; authenticated account separation remains a manual acceptance item.

The CLI manages UUID-backed profiles, metadata-only workspaces, launch/stop/restart/status, duplicate-launch detection, batch operations, diagnostics, structured logs, and safe unregistering that preserves profile data. The GUI exposes the same core lifecycle actions through a compact WPF window and keeps the authentication warning visible.

## Capability evidence

Antigravity Desktop 2.12.2 was found at the current user's LocalAppData installation path. A disposable probe observed two independent process homes, Electron user-data directories, backend process trees, and two visible first-run windows. Stopping one probe instance left the other alive. No Google credentials were read or copied. Authenticated A/B persistence and OAuth callback routing remain manual verification requirements.

## Installation and use

`scripts/Publish.ps1` is the package entry point. The verified self-contained package is at `artifacts/package/20260907-213404/AGProfileHub/`; the archive is `artifacts/package/20260907-213404/AGProfileHub-win-x64.zip` and its SHA-256 sidecar is next to the archive. Read the sidecar to verify the archive after copying it.

Package smoke checks passed: `cli/AGProfileHub.Cli.exe help` returned the command list, and `hub/AGProfileHub.Gui.exe` opened a window and exited cleanly after the smoke run. Use `README.md` and `MANUAL_TEST_PLAN.md` for profile creation and authenticated verification.

## Security model

Hub manages local paths and processes. It does not store, inspect, export, copy, or proxy Google passwords, tokens, cookies, browser sessions, or authorization headers. Profile isolation is a same-Windows-user path/process arrangement and is not a security boundary. See `SECURITY.md`.

## Testing

On September 7, 2026, `scripts/Build.ps1 -Test` built the four-project solution with zero warnings/errors and passed 12/12 automated Core/process checks. `scripts/Test-CliConcurrency.py` passed 12 checks covering concurrent profile creation, camelCase JSON, workspace metadata, config rejection, UUID stability, and data preservation. `scripts/Test-Integration.ps1` passed 6 real-process checks covering CLI survival after exit, duplicate launch, two independent roots, restart/stop isolation, and data preservation. Packaged CLI help and GUI startup smoke also passed for the final package listed above. Automated tests do not perform Google login; `MANUAL_TEST_PLAN.md` is the authority for authenticated identity separation.

## Known limitations and roadmap

OAuth callback routing, Credential Manager/DPAPI behavior, settings persistence after real login, and Desktop workspace forwarding require manual or future capability work. The current workspace metadata command intentionally does not claim to forward a workspace into the Antigravity Desktop window. Standalone CLI/headless identity, shortcuts, tray integration, and Agent Safe Git worktrees remain later phases.
