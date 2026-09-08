# Implementation plan

Apply READ → PLAN → IMPLEMENT → BUILD → TEST → VERIFY → DOCUMENT to every phase. Update PROJECT_STATE.md after each completed milestone. Never claim manual tests passed without a user observation.

| Phase | Goal / implementation | Files | Tests / acceptance | Rollback |
|---|---|---|---|---|
| 0 | Inspect installed desktop; disposable dual-process probe | scripts/Probe-Antigravity.ps1, CAPABILITY_REPORT.md | Separate roots and generated state; cleanup; auth remains manual | Stop only owned processes, remove only checked probe root |
| 1 | Domain/config/UUID/path/schema/locks | Core domain and storage | Roundtrip, rename stability, traversal/reparse rejection, corrupt/future schemas, concurrent writes | Preserve backups, refuse failed migration |
| 2 | Discovery/version/capability/launch plan | Core driver | Exact arguments and scoped environment; no arbitrary args; unknown capabilities honest | No profile mutation on compatibility errors |
| 3 | Create/list/rename/remove/workspace metadata | Core managers | N profiles; remove preserves data; workspace validation | Registration backup |
| 4 | Launch/status/restart/stop/reconcile | Core Windows process adapter | PID reuse/foreign process/duplicate launch; independent A/B stop; recover after Hub exit | Preserve running vendor processes if ownership unknown |
| 5 | Usable CLI including doctor and JSON | Cli | temp create→launch→status→stop; useful exit codes; documented commands | Preserve profile data |
| 6 | Human login and visible window verification | MANUAL_TEST_PLAN.md, capability attestation | Google A/B, restart, callback routing, settings separation; pending human | Close only affected instance; don't logout other account |
| 7 | WPF management GUI | Gui | Build plus visible UI smoke; core reuse and async responsive actions | CLI remains usable |
| 8 | Desktop shortcuts; tray optional | Core shortcut/Gui | Shortcut opens correct UUID via CLI | Remove only Hub-created link |
| 9 | Workspace metadata; investigate launch support separately | Core workspace/Gui | Missing path handled; no unsupported argument presented as success | Retain metadata and explicitly report unsupported launch |
| 10 | Update guard, structured logs | Core diagnostics/settings | Version change warning, unknown state, redaction; no vendor log collection | Read-only diagnose |
| 11 | Local portable Windows package | scripts/Publish.ps1, README, FINAL_REPORT | clean build/tests, CLI help smoke and GUI startup | Replace binaries without touching LocalAppData profiles |
| 12 optional | Worktree/Agent Safe mode | deferred | Dedicated future design and destructive safeguards | Do not delete existing worktrees |

Release gate: automated tests/build pass, runnable CLI/GUI delivered, limitations explicit. Full product acceptance additionally requires Phase 6. Use an experimental label until then. Workspace CLI forwarding is a separate unverified capability, not a reason to misreport Phase 6 success.

September 8 update: Phase 8 desktop shortcuts are implemented and verified through COM assertions and actual packaged .lnk lifecycle tests. GUI executable settings and operation/status feedback are implemented. Phase 6 remains pending user testing; Phase 9 workspace association is implemented as metadata, while vendor forwarding is unsupported. Phase 12 and tray remain deferred.
