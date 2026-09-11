# Delivery report — 2026-09-08

## Status
Antigravity Profile Hub 0.1.2 is an experimental Windows serial profile manager. Core, CLI, WPF GUI, desktop shortcuts and packaging are implemented. Sequential A/B restart-persistence checks passed, but authenticated multi-account isolation failed: with both profiles open, both showed the account signed in later. This release must not be described as supporting separate Google accounts; both profile data directories are preserved for diagnosis.

## Delivered behavior
N immutable UUID profiles; rename without moving data; workspace metadata; launch, stop, restart, status and batch operations; live process reconciliation; duplicate-launch handling; safe unregistering with data preserved; doctor and structured logs.

The GUI adds an executable picker, operation locking, status detail wrapping, explicit incomplete-stop messages and an isolated --data-root option. It refreshes status after failed mutations and disables `Launch all` when multiple Profiles make that action unsafe. Desktop shortcuts call the CLI with the UUID and original data root. They still work after a profile rename; existing links are not overwritten. Moving the package requires recreating shortcuts.

## Verification
September 8: Release build has zero warnings/errors; Core/process/shortcut and launch-barrier assertions pass 26/26; independent CLI metadata concurrency checks pass 12/12; real-process integration passes 12/12. The integration now races independent CLI processes both within one data root and across two data roots, and proves exactly one root starts. It also covers duplicate launch, sequential handoff, single-root restart and data-preserving removal. GUI create, rename, launch and graceful stop were exercised against an isolated test root, including visible running and stopped status. Windows shortcut tests verify target/arguments, Unicode names, rename stability and preservation of existing links.

scripts/Publish.ps1 produces the self-contained win-x64 package under artifacts/package/<timestamp>/AGProfileHub with a ZIP and SHA-256 sidecar. scripts/Test-Package.ps1 performs actual Windows .lnk launch, duplicate launch and stop checks against disposable profiles. The September 7 package remains a historical build; use the newest verified package for this continuation.

The newest timestamped `artifacts/package/*/AGProfileHub-win-x64.zip` built from this report is the verified 0.1.2 package; earlier packages predate the persistent cross-data-root launch barrier. Packaged shortcut lifecycle plus the launch-all gate pass 6/6. The current Doctor reports Desktop 2.12.2 and Google account isolation as incompatible. Ten integration log events were checked against the allowlist and one fixed sanitized command template; no unexpected fields or templates were found.

## Manual acceptance
MANUAL_TEST_PLAN.md remains authoritative. Sequential restart persistence passed for A and B, including B after its first launch displayed A's session. In the concurrent A/B test, both profiles showed the account signed in later, so authenticated account isolation failed. Preserve the profile data directories; do not claim authenticated multi-account support. Cross-profile restart/stop, settings/project behavior, Hub recovery, shortcut identity, synthetic-home behavior, and OAuth callback routing remain unverified. No credential inspection or automation of authentication was performed.

## Limits
Compatibility is pinned to locally observed Desktop 2.12.2. Direct manual observation showed authenticated account isolation failed for this build: concurrently launched profiles both showed the account signed in later. Static evidence strongly implicates a Windows-user-scoped credential facility, but the exact runtime storage contract was not dynamically traced and no credential contents were read. Synthetic home changes tool configuration lookup. Workspace forwarding is unsupported; workspace association stores metadata only. Tray, standalone CLI/headless identity and Git worktree orchestration remain deferred. See SECURITY.md for path-isolation boundaries.
