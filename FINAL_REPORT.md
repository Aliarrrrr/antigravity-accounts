# Delivery report — 2026-09-08

## Status
Antigravity Profile Hub 0.1.1 is an experimental Windows profile manager. Core, CLI, WPF GUI, desktop shortcuts and packaging are implemented. Full authenticated multi-account acceptance is not complete: the user will perform the Google A/B checklist later.

## Delivered behavior
N immutable UUID profiles; rename without moving data; workspace metadata; launch, stop, restart, status and batch operations; live process reconciliation; duplicate-launch handling; safe unregistering with data preserved; doctor and structured logs.

The GUI adds an executable picker, operation locking, status detail wrapping, explicit incomplete-stop messages and an isolated --data-root option. Desktop shortcuts call the CLI with the UUID and original data root. They still work after a profile rename; existing links are not overwritten. Moving the package requires recreating shortcuts.

## Verification
September 8: Release build has zero warnings/errors; Core/process/shortcut assertions pass 16/16; independent CLI concurrency checks pass 12/12; real-process integration passes 6/6. GUI create, rename, launch and graceful stop were exercised against an isolated test root, including visible running and stopped status. Windows shortcut tests verify target/arguments, Unicode names, rename stability and preservation of existing links.

scripts/Publish.ps1 produces the self-contained win-x64 package under artifacts/package/<timestamp>/AGProfileHub with a ZIP and SHA-256 sidecar. scripts/Test-Package.ps1 performs actual Windows .lnk launch, duplicate launch and stop checks against disposable profiles. The September 7 package remains a historical build; use the newest verified package for this continuation.

Verified current package: artifacts/package/20260908-115335/AGProfileHub-win-x64.zip. Packaged shortcut lifecycle passed 5/5 (artifacts/package-smoke/70ebc158a2bd434a89d10c0e121071bf). The self-contained GUI started with the isolated test profile, displayed wrapped details, and opened the executable picker. The GUI doctor displayed Desktop 2.12.2 and manual-verification-required. Six integration log events were checked for allowed fields and fixed sanitized command templates; no unexpected fields/templates were found.

## Manual acceptance
MANUAL_TEST_PLAN.md remains authoritative. Verify two real Google identities, restart persistence, concurrent operation, settings behavior and OAuth callback routing. The user confirmed these checks have not yet been done. No credential inspection or automation of authentication was performed.

## Limits
Compatibility is pinned to locally observed Desktop 2.12.2. Shared OS OAuth registration and credential facilities are not proved isolated. Synthetic home changes tool configuration lookup. Workspace forwarding is unsupported; workspace association stores metadata only. Tray, standalone CLI/headless identity and Git worktree orchestration remain deferred. See SECURITY.md for path-isolation boundaries.
