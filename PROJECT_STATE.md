# Project state — 2026-09-08

## Current phase
Experimental MVP engineering is implemented. Manual authenticated multi-account acceptance failed: sequential A/B restart persistence passed, but simultaneous A/B use showed both profiles on the account signed in later. Preserve both profile data directories and do not claim account isolation. Correct the cause and repeat a clean A/B test before reopening this release gate.

## Completed
- Core UUID profiles, atomic schema-checked configuration, locks, path validation, registration-only removal, workspace metadata.
- Desktop 2.12.2 discovery, independent process home/user-data, ownership reconciliation, duplicate-launch focus, graceful/forced stop and restart.
- One fixed per-user launch gate shared by every Hub data root, with a persistent pre-create barrier that fails closed across delayed discovery, cancellation and runtime-write failure.
- CLI lifecycle, JSON, diagnostics and structured operation logs.
- WPF management UI, executable picker, readable status details, operation locking, isolated --data-root startup.
- CLI and GUI desktop shortcuts: UUID and Hub data-root arguments, no overwrite of existing links, rename stability.
- Portable self-contained win-x64 packaging and reproducible packaged-shortcut smoke script.

## Latest verification
- Release build: zero warnings/errors; 26/26 Core/process/shortcut and launch-barrier checks passed.
- CLI concurrency: 12/12 passed; artifacts/cli-concurrency/b60c3b98f77d457194b08a4b5ec093df.
- Real-process integration: 12/12 passed, including independent CLI races within one data root and across two data roots, concurrent-launch rejection and single-root restart; artifacts/integration/21f030b032c74a7fa092ca3d6ed602fc.
- GUI create, rename, launch and graceful stop exercised with an isolated root; actual running PID and stopped state observed.
- Static installed-app inspection still reports Desktop 2.12.2; no credentials inspected.
- Packaging uses scripts/Publish.ps1; scripts/Test-Package.ps1 verifies actual .lnk launch after rename and duplicate-launch behavior. See FINAL_REPORT.md for the release record.
- Latest verified package: the newest timestamped `artifacts/package/*/AGProfileHub-win-x64.zip` built from 0.1.2; packaged shortcut lifecycle and the launch-all gate pass 6/6. Earlier packages predate the persistent cross-data-root launch barrier and are historical. The current Doctor reports Google account isolation as incompatible on Desktop 2.12.2.
- User-reported manual check: A and B each retained a signed-in session through sequential Hub stop/relaunch, but when running concurrently both showed the account signed in later. Authenticated account isolation failed; no credentials, identity details, or authentication data were inspected or recorded. Both profile data directories are preserved.

## Release gate
Authenticated multi-account acceptance failed. Keep the product experimental and Google-account-incompatible for the current Desktop version, preserve both profile data directories, and do not claim separate-account support. The product now prevents a second root; reopen the gate only with an official profile-scoped authentication contract or a separate Windows identity boundary, followed by a clean A/B test. Settings/project behavior, Hub recovery, desktop shortcuts, and synthetic-home behavior remain unverified.

## Deferred scope
Vendor workspace forwarding is unsupported by the verified launch contract; stored workspace metadata is implemented. Tray, standalone CLI/headless identity and Agent Safe Git worktrees remain optional future scope. Destructive profile-data purge is not provided.

## Working tree note
scripts/Inspect-InstalledApp.py already had uncommitted additions for loading-overlay/retry inspection before this continuation. Those changes were preserved and run successfully; do not silently include them in unrelated commits.
