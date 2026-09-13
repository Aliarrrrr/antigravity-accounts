# Changelog

## [0.4.3] - 2026-09-13

Security and truthfulness remediation from the 0.4.2 full audit (`docs/compose/spec/audit-042-report.md`). **59 automated checks pass.** Manual acceptance on Desktop 2.12.2 completed 2026-09-13 (see `MANUAL_TEST_PLAN.md`).

### Fixed

- **Launch and deep-link handoff refused an unpinned executable** (`executable_not_pinned`). Doctor still inspects an unpinned path so the user can run `configure-executable` once.
- **Warm scopes skipped token-store proof**: a live scope without a Hub-written `scope-attestation.json` is no longer reported as a healthy isolated session (`isolation_scope_unverified`). Attestation is written after a verified cold start; it is cleared when the Hub observes the scope idle, when stop fails or the box is missing, and on a successful terminate. A foreign process started outside the Hub still has to be stopped and reopened through the Hub before the scope is trusted again.
- **Language-server log length read failure no longer lowers the verification floor** (fail closed as `isolation_scope_unverified`).
- **`EnsureRegistered` no longer replays a free-form `RouterCommand` from disk** into `HKCU`; the command is rebuilt from a CLI-shaped executable plus this data root, and the ownership record is rewritten to match so restore/`IsRegisteredFor` stay consistent.
- **Callback handoff resolves `--user-data-dir` from ProfileId** (managed path), not the free-form path in the pending file.
- **Doctor no longer marks concurrent Google account isolation / visible windows as fully Verified** when scopes are merely prepared; `concurrentSignInRouting` is Verified only after this release's recorded E2E.

### Deferred (documented, not fixed)

- Pending deep-link grants remain a single last-writer slot: two Profiles signing in at the same instant can steal each other's callback. Finish one sign-in before starting the next; multi-slot routing is deferred until a stronger concurrent-OAuth requirement appears.

### Changed

- `MANUAL_TEST_PLAN.md` rewritten for process-local (no Sandboxie verbs) and carries the 2026-09-13 acceptance record (A/B/C passed).
- `ARCHITECTURE.md` shortcut implementation is COM `IShellLinkW` (not WScript.Shell).
- SECURITY.md documents warm attestation, router-command rebuild, authoritative handoff path, and the single-slot pending limitation.
- Settings comment no longer claims missing `defaultBackend` loads as Sandboxie.
- Version is 0.4.3.

## [0.4.2] - 2026-09-13

Audit-remediation release: every finding from the full 0.4.1 audit was fixed in its own revertible commit. 54 automated checks pass.

### Fixed

- **A WMI enumeration failure read as "no processes"**: stop reported a scope as already stopped while the instance was alive, status showed Stopped, and the already-running launch gate disappeared. Enumeration failures now throw `process_enumeration_unavailable`: status reports liveness as unknown, stop refuses to claim success, and a launch that cannot see the scope starts nothing. The launch polling loop tolerates transient failures because the token-store gate is log-based.
- **Scope attribution used substring matching**: `--user-data-dir` is now parsed with real argv rules and compared as a full path, so a look-alike path embedding the expected one is no longer attributed to the scope; scope children join only when created no earlier than their in-scope parent, closing the reused parent-PID hole in the closure walk.
- **Pending callbacks never expired and survived failed launches**: a webpage-triggerable `antigravity://` callback could be handed to a profile that never came up. Grants now expire after 30 minutes, are cleared by every failure after the pending write, are cleared only by their own profile's stop, and a corrupt or overlong callback URI is rejected (`deeplink_callback_invalid`).
- **A corrupt deep-link registration record aborted the launch after the instance had already started**, skipping the verification/termination gate. The record is validated before anything starts; a failed post-start re-assertion terminates the scope; a failed registration write deletes its ownership record instead of leaving one that could be self-healed later.
- **`configure-executable` performed no verification of the binary**: the configured executable is now pinned by SHA-256 at configure time and verified before every discovery and deep-link handoff (`executable_attestation_changed`). Legacy unpinned configurations keep working; doctor warns until they reconfigure.
- The installer dependency check only recognised the default LocalAppData Antigravity path and warned "not detected" for Program Files or custom installs; it now mirrors the driver's discovery paths. The installer no longer advertises architectures (ARM64) that were never built or tested.
- CLI: `doctor`/`setup` exit 1 when the executable check fails so scripts can gate on them; `close` honours `graceful_close_pending` exactly like `stop`; `create` is an alias of `new` instead of a path that skipped isolation; the removed `sandboxie` backend name is rejected instead of silently mapping to process-local; unexpected errors carry the exception message and point at `open-logs`; the empty list suggests `new`; `version_unverified` names the verified version; help documents the exit codes.

### Added

- GUI: window title carries the product version; busy operations give visible feedback instead of silently dropping clicks; error dialogs keep the code and message; the InfoBar stays dismissed while the situation it described is unchanged and reappears when it changes again; a one-time hint offers callback registration once two or more profiles exist; `⋯` menu has an accessibility name; wrapped detail rows no longer clip at the fixed row height; hard-coded off-token colors removed; the app and windows now use a generated application icon.
- MIT `LICENSE`, `dependabot.yml` (NuGet + GitHub Actions), and a `scripts/legacy/` quarantine with warnings for the one-off scripts that mutate real data or reference dead paths.

### Changed

- CI builds with `-LockedRestore`, and the tag workflow publishes with `-Release`, enforcing locked restore, a strict clean tree, the exact `v<version>` tag and a matching CHANGELOG entry; the workflow refuses a manifest marked non-release or dirty. The manifest now records the real process-local file-token-store isolation and config schema 1 (it claimed the removed Sandboxie mechanism and schema 2), and `Test-PackageStructure.ps1` asserts those fields.
- Stale-era documents moved to `docs/history/`; VERSIONING.md, SECURITY.md, README and PROJECT_STATE.md now describe the current pipeline, capability status (concurrent sign-in acceptance after the 0.4.1 routing repair is explicitly pending) and safety boundaries.

## [0.4.1] - 2026-09-12

> 更正（0.4.2 审计时发现）：本条目"Changed"中声称清理了 Publish.ps1 的 Sandboxie 表述，实际未完成（manifest 仍写入沙盒机制与 schema 2），已在 0.4.2 修正并加入发布结构断言。

### Fixed

- **Deep-link routing dropped every OAuth callback**: `Route` relaunched the Hub CLI instead of the vendor desktop, so while the router owned `antigravity://` no sign-in callback could arrive. The handoff now relaunches `Antigravity.exe` with the waiting Profile's user-data directory, and executable discovery is shared with the driver. Note: concurrent sign-in still requires one real end-to-end acceptance run before it should be relied on.
- The launch wrapper (`launch.cmd`) was written with ASCII encoding, silently corrupting non-ASCII paths (for example a Chinese Windows user name) into `?`; it is now UTF-8 without BOM with an early `chcp 65001`, and `%` is escaped as `%%` so cmd cannot expand it. Verified against a real `cmd.exe` run.
- A second `open`/`launch-all` during a cold first launch failed with a misleading `lock_timeout` after ten seconds; launch/stop/restart now wait up to four minutes, past the worst-case verification window.
- Stopping an isolated Profile killed PIDs from a process snapshot without revalidation; a reused PID would have terminated an unrelated process tree. Termination now kills only processes whose live start time still matches the snapshot.
- Uninstalling no longer leaves `antigravity://` pointing at the removed CLI; the installer restores the previous handler (only when this data root owns the registration).

### Added

- GUI `Callbacks` toolbar button to register or restore deep-link routing without the CLI; creating a profile no longer hides an isolation-preparation failure.

### Changed

- `BuildLaunchPlan`/`CreateAsync` now require an explicit isolation backend instead of defaulting to the legacy value.
- Purged Sandboxie-era wording left in SECURITY.md, ARCHITECTURE.md, PROJECT_STATE.md, CAPABILITY_REPORT.md, KEYRING_ISOLATION_REPORT.md, GUI/CLI copy and Publish.ps1; corrected the false deep-link probe claim and the 0.4.0 test count (43/43, not 51/51).

## [0.4.0] - 2026-09-12

### Removed

- The Sandboxie dependency, end to end: the sandbox backend, the elevated box/`OpenFilePath` administration, the keyring shim and its CLI verbs, and the write-through gates. No administrator rights are needed at any point.

### Changed

- Profiles can now run without Sandboxie. A process-local backend gives each Profile its own identity by pointing the instance's home at its managed data root and asking the language server to keep its OAuth token in a file there, instead of the one shared Windows credential entry. Preparing a scope needs no elevation, no credential shim and no direct-write rules, and a launch is not trusted until the language server proves in its own log that it switched to the file store.
- Profiles recorded as the legacy sandbox backend resolve to the same process-local scope; the launch-time store-selection verification keeps them off the shared Windows credential entry either way. Old configuration documents load unchanged.
- The backend choice is an additive, optional field rather than a schema bump: documents written before it existed load as Sandboxie, and unknown future schemas are still rejected (ADR-014).
- Tests: 43/43.

### Added

- `configure-deeplink register|restore` routes the vendor `antigravity://` callback to the Profile that is waiting for it, so concurrent sign-ins reach the right window; registering keeps the previous handler and can always be restored.
- A launcher re-assertion puts the callback registration back after every desktop start, because the vendor desktop rewrites the scheme to itself while starting up.
- New CLI: `configure-isolation <sandboxie|process-local>` (backend for newly created profiles; existing profiles keep theirs), `new --backend <...>` (override for one profile), `configure-deeplink <register|restore>` and `callback <uri>`.
- Unified development entry point `hub.ps1` (PowerShell 5.1/7) with `build`, `test`, `run`, `cli`, `publish`, `version` and `check` verbs; restore/build/test run through the shared `scripts/Common.ps1` environment.
- Locked restore and a Release publishing gate: `packages.lock.json` is generated for all four projects and committed; `Build.ps1 -LockedRestore -Test` enforces it, and `publish -Release` requires a clean tree, an exact `v<version>` tag and a matching CHANGELOG entry.
- Added the `version` CLI verb (`--version`/`-v`), printing the product version and exiting before any Hub bootstrap.
- Added `NuGet.Config` (nuget.org only) and `.editorconfig`.
- `global.json` now pins `allowPrerelease: false`.

### Fixed

- Creating or renaming a Profile no longer accepts a name that another Profile already uses; duplicate names made every name-based lookup ambiguous.

## 0.3.0 experimental — 2026-09-10

- Sandboxie.ini parsing consolidated into `Core/Isolation/SandboxieIniFile`; `SandboxieBackend` delegates to it so the read and write paths can no longer disagree.
- Elevation is now in-house: `Core/Isolation/ElevationBridge` + `AdminOperations` and an internal `admin-helper` CLI verb. Box creation and direct-write rules no longer require hand-copied elevated scripts.
- Added `Core/Isolation/IsolationSetup`: an idempotent state machine covering box, registration, keyring claim and direct-write rules; a second run is a provable no-op.
- Direct-write rules converged from five managed sub-directories to one profile root (the legacy five-rule form is still honoured as complete, so existing profiles are not forced to reapply).
- New everyday CLI verbs: `setup`, `new`, `ls`, `open`, `close`, `fix`. Every command now accepts a name, unique prefix or UUID (`ProfileManager.ResolveAsync`); UUID remains valid but is no longer required.
- GUI rebuilt on `Themes/Theme.xaml`: design tokens, styled DataGrid with status badges, dynamic InfoBar, converged toolbar, per-row Open + ⋯ menu, empty state, themed `InputDialog`, parallel refresh.
- Hardened `SandboxieBackend` `Start.exe` calls with `/silent` and a 30 s timeout (previously a single read could block forever).
- Tests: 38/38. Build: 0 warnings, 0 errors.

## 0.2.3 experimental — 2026-09-09

- Replace the locale-sensitive `WScript.Shell` shortcut writer with the native Unicode `IShellLinkW`/`IPersistFile` interfaces, fixing non-ASCII Profile and destination paths on English Windows runners.
- Retain the atomic no-overwrite move and include the underlying Windows error in shortcut failures.
- Move GitHub Actions to their Node.js 24 generations to remove hosted-runner deprecation warnings.

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
