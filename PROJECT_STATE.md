# Project state — 2026-09-09

## Current phase
Opt-in Sandboxie isolation backend plus in-box credential-shim is implemented and unit-tested. Direct backend remains serial and Google-account-incompatible. Authenticated dual-account acceptance still requires manual official sign-in in two Sandboxie profiles. Preserve all original profile data and do not claim multi-account support until that acceptance passes.

## Completed
- Core UUID profiles, atomic schema-checked configuration, locks, path validation, registration-only removal, workspace metadata.
- Desktop 2.12.2 discovery, independent process home/user-data, ownership reconciliation, duplicate-launch focus, graceful/forced stop and restart.
- One fixed per-user launch gate shared by every Hub data root, with a persistent pre-create barrier that fails closed across delayed discovery, cancellation and runtime-write failure.
- CLI lifecycle, JSON, diagnostics and structured operation logs.
- WPF management UI, executable picker, readable status details, operation locking, isolated --data-root startup.
- CLI and GUI desktop shortcuts: UUID and Hub data-root arguments, no overwrite of existing links, rename stability.
- Portable self-contained win-x64 packaging and reproducible packaged-shortcut smoke script.
- **Root cause:** language server stores the Google OAuth token in a single Windows Credential Manager entry `gemini:antigravity` (KEYRING_ISOLATION_REPORT.md).
- **Isolation primitive:** in-box `credential-shim --force --fresh` claims that target so sandboxed reads no longer fall through to the host.
- **Sandboxie opt-in backend (2026-09-09):** schema v2 (`ProfileBackend`, `BoxId`), `WindowsSandboxieRuntime` (Start.exe/SbieIni), supervisor branch that runs credential-shim once per profile (marker file), discovers the vendor root from live snapshots (never Start.exe PID), and allows concurrent Sandboxie profiles. Direct profiles keep the historical single-root gate.
- CLI: `create <name> --backend direct|sandboxie`.
- Release build after the change: 0 warnings, 0 errors; **36/36** tests pass (schema v1 migration, unique box ids, concurrent Sandboxie launch, launch-all multi-Sandboxie, one-shot keyring shim).

## Latest verification
- Unit tests 36/36 on the isolated worktree branch `feature/sandboxie-keyring-isolation`.
- Real dual-account Google login has **not** been re-attempted yet. Manual acceptance is the remaining gate.
- Historical Sandboxie P2 failure (B showing A's session) is explained by missing in-box credential claim; that path is now closed by design, not by re-running the old boxes.

## Release gate
Direct backend: Google-account-incompatible; keep the single-root gate. Sandboxie backend: experimental until the user completes official sign-in in two separate Sandboxie profiles and verifies concurrent non-interference, restart persistence, and logout isolation. Never read, copy, export, or inject credential contents.

## Deferred scope
Vendor workspace forwarding is unsupported by the verified launch contract; stored workspace metadata is implemented. Tray, standalone CLI/headless identity and Agent Safe Git worktrees remain optional future scope. Destructive profile-data purge is not provided.

## Working tree note
scripts/Inspect-InstalledApp.py already had uncommitted additions for loading-overlay/retry inspection before this continuation. Those changes were preserved and run successfully; do not silently include them in unrelated commits.

## Completed
- Core UUID profiles, atomic schema-checked configuration, locks, path validation, registration-only removal, workspace metadata.
- Desktop 2.12.2 discovery, independent process home/user-data, ownership reconciliation, duplicate-launch focus, graceful/forced stop and restart.
- One fixed per-user launch gate shared by every Hub data root, with a persistent pre-create barrier that fails closed across delayed discovery, cancellation and runtime-write failure.
- CLI lifecycle, JSON, diagnostics and structured operation logs.
- WPF management UI, executable picker, readable status details, operation locking, isolated --data-root startup.
- CLI and GUI desktop shortcuts: UUID and Hub data-root arguments, no overwrite of existing links, rename stability.
- Portable self-contained win-x64 packaging and reproducible packaged-shortcut smoke script.

## Latest verification
- Sandboxie Plus 1.18.2 / core 5.73.2 is installed. `scripts/Test-SandboxCredentialIsolation.ps1` passed the host/A/B synthetic credential matrix with two fresh boxes and removed its random host target and temporary box configurations. This did not inspect any real credential.
- Sandbox P2 state is `C:\Users\ZLF\AppData\Local\AntigravityProfileHub\sandboxie-p2\6a4933eec3f843b99a569e33e977b6da\state.json`. The user reported completing Profile A's official login and confirmed it survived a full box-process restart. With A and B active in separate boxes, process inventory showed correct box ownership, no PID overlap, no unboxed vendor process, and one visible window per box; the user then reported that B displayed A's existing login. A and B each have an independent `SbiePst.dat` path (only path and metadata checked). A five-second read-only B resource trace is recorded at `artifacts/sandboxie-resource-trace/650eedee8550456490d9468204ed8342/report.json` (43,407 session entries; 18,157 B entries; 2,872 unique resources; 337 unmapped discarded; 144 truncated). B showed no direct reads of the host user's existing Google/Antigravity configuration, cookie store, Local State, or credential/vault/DPAPI data; loading Windows System32 `DPAPI.DLL` was observed, and B's own user-data `Cookies-wal` is not host authentication storage. Shared authentication-related objects included `protected_storage`, `SBIE_ProtectedStorage_Section`, and `SBIE_BOXED_ServiceCrypto_Mutex1`. No safe `ClosedFilePath`/`ClosedIpcPath` rules resulted. This failure closes the Sandboxie route.
- A separate `UsePrivacyMode=y` C box launched successfully with only the Antigravity install directory opened, but the installation has no supporter certificate and the approximately five-minute evaluation ended. Privacy Mode is not a shippable dependency or an authenticated-isolation result.
- Release build: zero warnings/errors; 26/26 Core/process/shortcut and launch-barrier checks passed.
- CLI concurrency: 12/12 passed; artifacts/cli-concurrency/b60c3b98f77d457194b08a4b5ec093df.
- Latest integration verification: `artifacts/integration/0c806599b19e49799b7b0591b4c784c8`; earlier failure/interference evidence is `artifacts/integration/d67f62f2f42a4cc89746e4afde2466f0`.
- GUI create, rename, launch and graceful stop exercised with an isolated root; actual running PID and stopped state observed.
- Static installed-app inspection still reports Desktop 2.12.2; no credentials inspected.
- Packaging uses scripts/Publish.ps1; scripts/Test-Package.ps1 verifies actual .lnk launch after rename and duplicate-launch behavior. See FINAL_REPORT.md for the release record.
- Latest verified package: the newest timestamped `artifacts/package/*/AGProfileHub-win-x64.zip` built from 0.1.2; packaged shortcut lifecycle and the launch-all gate pass 6/6. Earlier packages predate the persistent cross-data-root launch barrier and are historical. The current Doctor reports Google account isolation as incompatible on Desktop 2.12.2.
- User-reported manual check: A and B each retained a signed-in session through sequential Hub stop/relaunch, but when running concurrently both showed the account signed in later. Authenticated account isolation failed; no credentials, identity details, or authentication data were inspected or recorded. Both profile data directories are preserved.
- **Root cause confirmed (2026-09-09):** the language server stores the Google OAuth token in a single Windows Credential Manager entry whose target is the hardcoded literal `gemini:antigravity`. Directory, port, CSRF and Chromium user-data isolation were all proven to work; only the token store is a per-Windows-user singleton, so the latest sign-in always wins. Evidence and reasoning are in KEYRING_ISOLATION_REPORT.md.
- **Sandboxie read fall-through proven:** sandboxed writes are isolated, but a sandboxed read falls through to the host store when the box has no local copy. `OpenCredentials=n` does not prevent it. The earlier P1 "credential isolation passed" result was a false positive because the host-side `cmdkey` write silently produced nothing in this environment.
- **Isolation primitive implemented and verified:** claiming a same-named entry inside each isolation scope blocks the fall-through. In a never-signed-in box, after `credential-shim --force --fresh` the scope reads its own entry instead of the host 503-byte entry, and the host entry is unchanged. See KEYRING_ISOLATION_REPORT.md.
- Release build after the change: 0 warnings, 0 errors; 30/30 tests pass.

## Release gate
Authenticated multi-account acceptance failed for the direct backend and the standard Sandboxie experiment. Keep the product experimental and Google-account-incompatible, preserve both original profile data directories, and do not claim separate-account support. Reopen the gate only after an official profile-scoped authentication contract or a separately approved Windows identity boundary proves callback routing, distinct concurrent accounts, restart persistence, logout non-interference and workspace write-through. No external sandbox backend is enabled by default.

## Deferred scope
Vendor workspace forwarding is unsupported by the verified launch contract; stored workspace metadata is implemented. Tray, standalone CLI/headless identity and Agent Safe Git worktrees remain optional future scope. Destructive profile-data purge is not provided.

## Working tree note
scripts/Inspect-InstalledApp.py already had uncommitted additions for loading-overlay/retry inspection before this continuation. Those changes were preserved and run successfully; do not silently include them in unrelated commits.
