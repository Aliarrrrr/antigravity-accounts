# Project state — 2026-09-08

## Current phase
Experimental MVP engineering is implemented. Full product acceptance is blocked on the user's two-account Google login verification. The user confirmed September 8 that this has not been performed and will be done later using the checklist.

## Completed
- Core UUID profiles, atomic schema-checked configuration, locks, path validation, registration-only removal, workspace metadata.
- Desktop 2.12.2 discovery, independent process home/user-data, ownership reconciliation, duplicate-launch focus, graceful/forced stop and restart.
- CLI lifecycle, JSON, diagnostics and structured operation logs.
- WPF management UI, executable picker, readable status details, operation locking, isolated --data-root startup.
- CLI and GUI desktop shortcuts: UUID and Hub data-root arguments, no overwrite of existing links, rename stability.
- Portable self-contained win-x64 packaging and reproducible packaged-shortcut smoke script.

## Latest verification
- Release build: zero warnings/errors; 16/16 Core/process/shortcut checks passed.
- CLI concurrency: 12/12 passed; artifacts/cli-concurrency/65adbc30ceb149afbabc3d388b71ea20.
- Real-process integration: 6/6 passed; artifacts/integration/f420d2a9975b46e1acba79ee457fc880.
- GUI create, rename, launch and graceful stop exercised with an isolated root; actual running PID and stopped state observed.
- Static installed-app inspection still reports Desktop 2.12.2; no credentials inspected.
- Packaging uses scripts/Publish.ps1; scripts/Test-Package.ps1 verifies actual .lnk launch after rename and duplicate-launch behavior. See FINAL_REPORT.md for the release record.
- Final package: artifacts/package/20260908-115335/AGProfileHub-win-x64.zip; packaged shortcut lifecycle 5/5 passed, self-contained GUI startup and executable-picker opening observed. GUI doctor returned current version and manual-auth requirement.

## Remaining release gate
Run MANUAL_TEST_PLAN.md with two Google accounts: separate login, persistence across restarts, simultaneous distinct identities, independent settings, callback routing. Keep experimental/manual-verification-required until direct user observations are recorded. Directory/process evidence does not establish account isolation.

## Deferred scope
Vendor workspace forwarding is unsupported by the verified launch contract; stored workspace metadata is implemented. Tray, standalone CLI/headless identity and Agent Safe Git worktrees remain optional future scope. Destructive profile-data purge is not provided.

## Working tree note
scripts/Inspect-InstalledApp.py already had uncommitted additions for loading-overlay/retry inspection before this continuation. Those changes were preserved and run successfully; do not silently include them in unrelated commits.
