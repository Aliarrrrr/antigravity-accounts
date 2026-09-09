# Delivery report — 2026-09-09

## Status

Antigravity Profile Hub 0.2.3 is an experimental Windows multi-Profile manager for the exact tested combination of Antigravity Desktop 2.12.2 and Sandboxie Plus 1.18.2 / core 5.73.2. The core requirement—two official Desktop windows under one Windows user, concurrently retaining different Google accounts—passed manual acceptance after the fixed `gemini:antigravity` credential collision was isolated per box.

Normal Core, CLI, WPF and desktop-shortcut lifecycle entry points now use the prepared box automatically. Unprepared Profiles retain the safe serial fallback.

## Delivered behavior

- UUID-backed create/list/rename/remove-with-data-preserved and workspace metadata.
- Version-pinned launch plans with dedicated home/appdata/temp/user-data.
- Per-Profile Sandboxie preparation, strict fresh-box acknowledgement, box/keyring/write-through status, launch, duplicate launch, stop and restart.
- All-or-nothing `launch-all` preflight; mixed prepared/unprepared sets start nothing.
- Atomic, locked, schema-checked Profile/settings/runtime/isolation state and unique box ownership.
- Live PID/start-time/executable/user-data reconciliation for vendor roots.
- Host-scope keyring-shim refusal; no password/token/cookie export or authentication automation.
- WPF isolation status/actions, Doctor, CLI JSON, structured Hub logs and UUID-bound desktop shortcuts.
- Validated elevated setup scripts with write-through verification/rollback.
- Self-contained Windows x64 packaging with external-dependency manifest and required scripts.

## Verification

- Release build: 0 warnings, 0 errors.
- Deterministic suite: 34/34 passed.
- Host shim safety: direct call returned `shim_outside_isolation`; shared entry metadata stayed present at 503 bytes before/after.
- Live reconciliation during takeover: both accepted boxes retained 11 processes and their original root PIDs.
- Source and packaged WPF visual smoke passed, including both Profile rows, isolation status/actions, `Launch all`, and Doctor capability output.
- Packaged CLI help, 0.2.3 manifest, required scripts and SHA-256 sidecar were verified.

Artifact: the newest `artifacts/package/<timestamp>/AGProfileHub-win-x64.zip` produced from this report. The repository `PROJECT_STATE.md` records the exact verified path and SHA-256.

The real-process packaged shortcut smoke was not run because the accepted A/B Antigravity instances remain active; its disposable direct launch is intentionally blocked in that condition. Earlier 0.1.2 direct-path shortcut/integration evidence remains valid for that fallback.

## Manual acceptance and open gates

Passed: distinct A/B accounts concurrently, A restart persistence, B remaining uninterrupted while A restarted, and unchanged host authentication entry.

The current default Profiles now report the correct configured `OpenFilePath` set. Still required: verify a boxed workspace edit appears on the host and survives restart, and verify logout/re-authentication in one Profile does not affect the other. These steps require user-visible application/authentication actions and were not automated.

## Limits

The box is an application-state isolation mechanism, not a security boundary between Windows users. Compatibility must be reopened after either tested dependency changes. Vendor workspace forwarding, standalone CLI/headless identity, Tray, Agent Safe Git worktrees, quota management, sandbox cleanup and destructive Profile purge are deferred.
