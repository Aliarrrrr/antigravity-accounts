# Project state — 2026-09-12

## Current phase

The Sandboxie dependency is no longer required. A process-local backend ships as the default for new Profiles: each Profile points the instance's home at its managed data root and asks the language server to keep its OAuth token in a file there, so instances stop sharing the Windows user's `gemini:antigravity` credential entry. A launch fails closed unless the language server proves in its own log that it switched to the file store, and the vendor's `antigravity://` callback is routed to the Profile that is waiting for it, so concurrent sign-ins land in the right window.

The D2 gate passed against the real desktop (two Profiles, two Google accounts, one file token per Profile, the host credential entry untouched, restart persistence). The evidence tools live in `scripts/` (`Probe-KeyringBypass.py`, `Probe-DesktopEnvPropagation.py`, `Probe-DualAccountLogin.py`, `Probe-ProcessEnvironment.py`, `Agph-D2-ProfileControl.ps1`) and are part of the regression set.

Correction (0.4.1 audit): the earlier claim of a passed "deep-link handoff probe" was wrong — only the registration write was probed, and the handoff launched the Hub CLI instead of the vendor desktop, dropping every callback. Fixed; concurrent sign-in still needs one real end-to-end acceptance run.

The Sandboxie code path has been removed in a separately revertible commit. Profiles recorded as the legacy backend resolve to the same process-local scope, whose launch is verified either way.

## Earlier state — 2026-09-09 (historical, Sandboxie era)

## Current phase

Authenticated multi-account isolation is proven on Antigravity Desktop 2.12.2, with no external sandbox product. Core/CLI/GUI/shortcut lifecycle operations route through a prepared per-profile scope. The legacy direct route remains available for unprepared profiles and remains serial-only.

The root cause is the language server's fixed Windows Credential Manager target `gemini:antigravity`. Directory-only instances all read the same host entry. The shipped mechanism points each instance's home at its own Profile directory and asks the language server for its file token store; the launch verifies the store-selection log line before the instance is trusted. Distinct A/B accounts, concurrent use, restart persistence, and non-interference were manually accepted. See `KEYRING_ISOLATION_REPORT.md` and the probe scripts under `scripts/`.

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
4. Re-run authenticated acceptance after Antigravity changes version.

## Deferred scope

Vendor workspace forwarding remains unsupported; association and direct-write validation are implemented. Tray, standalone CLI/headless identity, Agent Safe Git worktrees, quota management, destructive Profile purge, and sandbox cleanup are deferred.

## Working tree note

The takeover builds on Work Buddy's completed changes. Machine-specific raw forensic helpers are excluded from the product repository; production source, tests, release scripts, installer definition and CI workflows are sealed in the 0.2.3 release commit.
