# Changelog

## 0.1.1 experimental — 2026-09-08
- Added CLI/GUI desktop shortcuts that bind immutable UUIDs and the current data root; existing links are not overwritten.
- Added GUI executable settings, isolated `--data-root` startup, operation locking, readable status details, and explicit incomplete-stop errors.
- Added regression checks for unverified versions, changed executable attestations, reused PIDs, and Windows shortcut contents; 16/16 checks pass.
- Re-ran CLI concurrency (12/12) and real-process integration (6/6); exercised GUI create, rename, launch and graceful stop against an isolated test root.
- Added reproducible packaged-shortcut lifecycle smoke script. Google A/B acceptance remains pending user verification.
- Packaged .lnk launch/rename/duplicate/stop smoke passed 5/5; verified self-contained GUI startup and executable picker.

## 0.1.0 development — 2026-09-07
- Established native Windows project plan and security boundaries.
- Identified Desktop 2.12.2 home-based .gemini storage; rejected user-data-only assumption.
- Observed two disposable process trees with separate state paths and independent stop.
- Added reproducible metadata-only probe and manual authentication test plan.
- Added UUID-backed profile storage with atomic JSON writes, schema checks, path safety, profile locks, and structured Hub logs.
- Added Antigravity Desktop driver and process supervisor with verified PID/start-time/executable/user-data ownership, duplicate-launch handling, graceful/forced stop, and restart isolation.
- Added CLI lifecycle commands and WPF profile hub UI without credential inputs.
- Added 12 Core/process assertions, CLI concurrency checks, and real-process integration checks.
- Published and smoke-tested the self-contained win-x64 package under `artifacts/package/20260907-213404/`.
- Kept Google account persistence and OAuth callback routing explicitly `manual-verification-required`.
