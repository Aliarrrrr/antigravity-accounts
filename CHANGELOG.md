# Changelog

## 0.2.3 experimental — 2026-09-09

- Retain the atomic no-overwrite shortcut move while retrying only transient Windows I/O failures where the temporary link still exists and the destination is still absent.
- Include the underlying Windows error in shortcut failures to make environment-specific diagnostics actionable.

## 0.2.2 experimental — 2026-09-09

- Release the Windows Shell COM shortcut object before moving the temporary `.lnk`, reducing its file-handle lifetime; hosted runners still exposed transient locks addressed in 0.2.3.
- Ignore local Playwright CLI session artifacts so browser checks cannot dirty the source tree.

## 0.2.1 experimental — 2026-09-09

- Replaced the machine-specific Work Buddy write-through helper with one packaged, parameterized PowerShell implementation; it no longer depends on Python, hard-coded user paths, repository paths, or fixed Profile/box IDs.
- Writes the bare repeated `OpenFilePath` form required by the tested Sandboxie driver, removes stale indexed and bare rules only from the selected box, preserves file encoding/newlines, makes a backup, verifies the saved set, and rolls back if the service reload fails.
- Requires explicit confirmation that every Sandboxie box is stopped before restarting the shared Sandboxie service.
- Reconciled the current machine status: both accepted boxes now report backend, box, keyring and configured write-through readiness.

## 0.2.0 experimental — 2026-09-09

- Identified the authentication root cause: Desktop 2.12.2's language server uses the fixed Windows credential target `gemini:antigravity`.
- Added a per-Profile Sandboxie backend that claims a box-local same-name entry before official login; distinct A/B accounts, restart persistence and cross-Profile non-interference passed manual acceptance on Sandboxie Plus 1.18.2 / core 5.73.2.
- Routed normal Core, CLI, WPF and shortcut lifecycle commands through prepared boxes while retaining the serial direct fallback for unprepared Profiles.
- Made `launch-all` fail closed before starting anything unless every Profile has a ready box; added duplicate-launch, restart, stop and status routing coverage.
- Added atomic/locked isolation state, unique box ownership, bounded box-name validation, missing-box refusal, strict shim exit-code handling and explicit `--fresh` acknowledgement for existing boxes.
- Blocked the credential shim outside a confirmed Sandboxie process and verified the host credential metadata remained unchanged.
- Added actual `OpenFilePath` readiness checks, GUI/Doctor warnings and a validated, verified, rollback-capable elevated write-through script.
- Packaged the required box/write-through scripts and updated capability documentation. Release suite is 34/34 with zero warnings/errors.

## 0.1.2 experimental — 2026-09-08
- Moved launch coordination to a fixed per-Windows-user state root shared by every `--data-root`.
- Added an atomic pre-create launch barrier that remains on delayed discovery, post-create cancellation, indeterminate process identity and runtime-state write failure; invalid or future barrier data fails closed.
- Added direct process-liveness reconciliation and a distinct confirmed-pre-create failure path, so only a process proven not to have started can clear its reservation immediately.
- Expanded the Core suite to 26/26 and real-process integration to 12/12, including simultaneous independent CLI launches within one data root and across two data roots; each race created exactly one root.
- GUI refreshes state after failed operations and disables `Launch all` when multiple Profiles are registered. Doctor reports retained or invalid global launch barriers.
- Kept Google account isolation `Incompatible` for Desktop 2.12.2; the single-root gate is a safety mitigation, not evidence of multi-account support.

## 0.1.1 experimental — 2026-09-08
- Added CLI/GUI desktop shortcuts that bind immutable UUIDs and the current data root; existing links are not overwritten.
- Added GUI executable settings, isolated `--data-root` startup, operation locking, readable status details, and explicit incomplete-stop errors.
- Added regression checks for unverified versions, changed executable attestations, reused PIDs, Windows shortcut contents, and the single-root launch gate; 19/19 checks pass.
- Re-ran CLI concurrency (12/12) and real-process integration (6/6); exercised GUI create, rename, launch and graceful stop against an isolated test root.
- Added reproducible packaged-shortcut lifecycle smoke script. Manual Google A/B acceptance found that concurrent profiles resolve to the later-signed-in account, so this version blocks a second Antigravity root and does not claim separate-account support.
- Packaged .lnk launch/rename/duplicate/stop smoke plus the launch-all gate passed 6/6; verified self-contained GUI startup and executable picker.

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
