# Implementation plan

Apply READ → PLAN → IMPLEMENT → BUILD → TEST → VERIFY → DOCUMENT to every phase. Update PROJECT_STATE.md after each completed milestone. Never claim manual tests passed without a user observation.

| Phase | Goal / implementation | Files | Tests / acceptance | Rollback |
|---|---|---|---|---|
| 0 | Inspect installed desktop; disposable dual-process probe | scripts/Probe-Antigravity.ps1, CAPABILITY_REPORT.md | Separate roots and generated state; cleanup; probe does not evaluate Google authentication | Stop only owned processes, remove only checked probe root |
| 1 | Domain/config/UUID/path/schema/locks | Core domain and storage | Roundtrip, rename stability, traversal/reparse rejection, corrupt/future schemas, concurrent writes | Preserve backups, refuse failed migration |
| 2 | Discovery/version/capability/launch plan | Core driver | Exact arguments and scoped environment; no arbitrary args; unknown capabilities honest | No profile mutation on compatibility errors |
| 3 | Create/list/rename/remove/workspace metadata | Core managers | N profiles; remove preserves data; workspace validation | Registration backup |
| 4 | Launch/status/restart/stop/reconcile | Core Windows process adapter | PID reuse/foreign process/duplicate launch; process-only A/B stop; recover after Hub exit | Preserve running vendor processes if ownership unknown |
| 5 | Usable CLI including doctor and JSON | Cli | temp create→launch→status→stop; useful exit codes; documented commands | Preserve profile data |
| 6 | Human Google A/B acceptance | MANUAL_TEST_PLAN.md, keyring/isolation evidence | Per-box keyring claim passed distinct concurrent identity, restart persistence and cross-Profile non-interference on the tested versions | Close only the affected dedicated box; don't automate login |
| 7 | WPF management GUI | Gui | Build plus visible UI smoke; core reuse and async responsive actions | CLI remains usable |
| 8 | Desktop shortcuts; tray optional | Core shortcut/Gui | Shortcut opens correct UUID via CLI | Remove only Hub-created link |
| 9 | Workspace metadata; investigate launch support separately | Core workspace/Gui | Missing path handled; no unsupported argument presented as success | Retain metadata and explicitly report unsupported launch |
| 10 | Update guard, structured logs | Core diagnostics/settings | Version change warning, unknown state, redaction; no vendor log collection | Read-only diagnose |
| 11 | Local portable Windows package | scripts/Publish.ps1, README, FINAL_REPORT | clean build/tests, CLI help smoke and GUI startup | Replace binaries without touching LocalAppData profiles |
| 12 optional | Worktree/Agent Safe mode | deferred | Dedicated future design and destructive safeguards | Do not delete existing worktrees |

Release gate: automated tests/build pass, runnable CLI/GUI delivered, limitations explicit, and Phase 6 manual identity acceptance passed for the exact tested versions. Workspace host write-through and logout non-interference remain separate manual gates; workspace launch forwarding remains unsupported.

September 9 update: the September 8 directory-only failure was traced to the fixed Windows credential target `gemini:antigravity`. A fresh per-Profile Sandboxie box with a local same-name claim passed Phase 6. The backend is now routed through normal Core/CLI/GUI/shortcut operations, while the old direct route remains serial. Phase 9 metadata is complete and direct-write configuration is detectable; the actual workspace write test remains pending. Phase 12 and tray remain deferred.

## Astra completion plan — September 8

This historical September 8 plan produced the safe serial fallback and its launch barrier. It is retained because those invariants still protect unprepared/direct Profiles; ADR-012 and the September 9 update supersede its conclusion that concurrent support is permanently blocked.

| Step | Change | Acceptance evidence |
|---|---|---|
| 1 | Move launch coordination to one fixed per-Windows-user state root shared by every `--data-root`. Add an atomic launch barrier before process creation and retain it until the created root is verified or proven exited. | Two independent Hub services with different data roots cannot both create a root. A delayed/inconclusive launch leaves the barrier and blocks the second profile. Invalid/future barrier data fails closed. |
| 2 | Reconcile a retained barrier using exact PID, creation time, executable and managed user-data identity. Distinguish a confirmed pre-create failure from an indeterminate post-create failure. | Confirmed create failure clears the reservation; post-create verification failure, cancellation and runtime-write failure cannot reopen the launch window. |
| 3 | Keep every entry point consistent with the single-root rule. Refresh GUI state after failed mutations and disable `Launch all` when more than one profile is registered. | CLI, shortcuts, single launch, restart and GUI batch launch all use the same Core gate; no partial batch start occurs. |
| 4 | Add deterministic Core race tests and real independent CLI-process integration. Limit external-root claims to roots detected for the configured executable. | Core suite, CLI concurrency suite, real-process integration and package smoke all pass; exactly one concurrent CLI launch succeeds. |
| 5 | Historical serial-release documentation step; later superseded by ADR-012. | At that time Doctor stated direct-path incompatibility; the current Doctor separately reports verified box isolation and serial direct fallback. |

Current stop conditions: do not inspect authentication contents, automate Google login, patch vendor binaries, claim an already-used box as fresh, or treat a missing write-through rule as harmless. Any dependency version change requires a clean A/B manual revalidation before retaining the concurrent-account claim.
