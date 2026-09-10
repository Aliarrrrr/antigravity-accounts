# Project state — 2026-09-09

## Current phase

Authenticated multi-account isolation is proven on the local compatibility pair: Antigravity Desktop 2.12.2 and Sandboxie Plus 1.18.2 / core 5.73.2. The productization pass now routes normal Core/CLI/GUI/shortcut lifecycle operations through a prepared per-profile box. The legacy direct route remains available for unprepared profiles and remains serial-only.

The root cause is the language server's fixed Windows Credential Manager target `gemini:antigravity`. Directory-only instances all read the same host entry. A fresh Sandboxie box also falls through to that host entry until it owns a same-named entry. The implemented shim writes an invalid placeholder only inside a confirmed Sandboxie process, causing the vendor's own fallback/storage behavior to remain profile-scoped. Distinct A/B accounts, concurrent use, restart persistence, and non-interference when A restarts were manually accepted. See `KEYRING_ISOLATION_REPORT.md`.

## Completed in the takeover pass

- Moved isolation routing into `HubService`; normal `launch`, `launch-all`, `stop`, `stop-all`, `restart`, desktop shortcuts, and GUI actions now share one decision point.
- Fixed mixed `launch-all`: it now preflights every Profile and starts none unless all Profiles have a ready box.
- Added isolation-aware status/Doctor output and GUI columns/actions for box and direct-write readiness.
- Added duplicate-launch behavior for boxes, atomic/locked isolation state, schema/box-name validation, one-box-per-Profile uniqueness, missing-box refusal, and strict shim exit-code handling.
- Added a host-scope guard: the destructive `credential-shim --force --fresh` path refuses to run unless `SbieDll.dll` is injected into the current process. A before/after metadata check kept the host entry present at 503 bytes.
- Refused silent attachment to an existing unregistered box. `iso-prepare --fresh` is now an explicit acknowledgement that the box has never been used to sign in.
- Added direct-write inspection against actual numbered Sandboxie `OpenFilePath` settings. Doctor warns when expected Profile/workspace paths are absent.
- Hardened the elevated write-through script: validate all inputs before clearing rules, verify writes, and restore prior rules on failure.
- Included both Sandboxie setup scripts and the keyring report in packaging; package manifest is now 0.2.3 and records the tested external dependency.
- Replaced the final machine-specific Python write-through workaround with a single parameterized packaged PowerShell script. It removes stale indexed/bare rules for only the selected box, preserves configuration encoding, verifies the exact set, restarts the shared service only after explicit all-boxes-stopped confirmation, and rolls back on failure.

## Verification

- Release build: 0 warnings, 0 errors.
- Deterministic Core/process/security suite: 34/34 passed. New tests cover shim failure, existing-box acknowledgement, duplicate box ownership, corrupt isolation state, Core lifecycle routing, and mixed launch-all no-partial-start behavior.
- PowerShell parser checks passed for both elevated setup scripts and the publisher.
- Host shim safety check passed: direct invocation returned `shim_outside_isolation`; host entry metadata was unchanged before/after.
- Live read-only reconciliation found both accepted A/B roots still running under their two recorded boxes with 11 processes each and unchanged root PIDs during the takeover work.
- WPF visual smoke passed from source and from the self-contained package: both rows, compact isolation status, all new actions, enabled isolated `Launch all`, and the updated Doctor dialog rendered correctly.
- The previous 0.2.0 through 0.2.2 packages are historical. A fresh self-contained 0.2.3 package must be used for daily operation.

## Current live readiness

Both accepted A/B boxes currently report `backendAvailable=true`, `boxExists=true`, `keyringClaimed=true`, and `writeThroughReady=true`, with no missing paths. The remaining write-through gate is behavioral: create a harmless file from a boxed workspace, confirm it appears immediately on the host, and confirm it survives restart.

## Remaining release gates

1. Manually verify an actual Profile/workspace write reaches the host and survives restart.
2. Manually verify logout in A does not log out B, and vice versa. Authentication actions remain user-only.
3. Run the real-process package shortcut smoke after the accepted A/B instances are intentionally stopped; its disposable direct launch is currently (correctly) blocked by the live-root guard.
4. Re-run authenticated acceptance after either Antigravity or Sandboxie changes version.

## Deferred scope

Vendor workspace forwarding remains unsupported; association and direct-write validation are implemented. Tray, standalone CLI/headless identity, Agent Safe Git worktrees, quota management, destructive Profile purge, and sandbox cleanup are deferred.

## Working tree note

The takeover builds on Work Buddy's completed changes. Machine-specific raw forensic helpers are excluded from the product repository; production source, tests, release scripts, installer definition and CI workflows are sealed in the 0.2.3 release commit.
