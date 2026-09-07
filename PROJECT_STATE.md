# Project state

## Current Phase
Astra Phase 0 and the Sol implementation pass are complete. Release packaging and documentation are complete for the experimental build; authenticated Google A/B verification remains a human gate.

## Completed
- Read master requirements; initialized Git; original prompt left unmodified and excluded from commits.
- Windows/PowerShell/Git/vendor discovery; installed vendor code inspected read-only.
- Dynamic two-profile probe: separate roots/backends/state paths; stop A leaves B; cleanup completed.
- Architecture, decisions, security, phased implementation plan, manual test checklist.
- Native four-project scaffold and local .NET 10.0.400 SDK; Core domain/storage/driver/process implementation builds cleanly.
- 12 Core/process safety assertions pass; the visible probe observed two separate official first-run windows with no login or privacy interactions.
- CLI concurrency and real-process integration scripts pass, including duplicate launch, two roots, workspace metadata, restart/stop isolation, and unregister-with-data-preserved.
- WPF GUI builds and the packaged GUI starts with a visible window.
- Self-contained win-x64 package was produced under `artifacts/package/20260907-213404/`.

## In Progress
- Human validation of two authenticated Google profiles and OAuth callback routing.

## Pending
Complete the manual Google login checklist, including cross-restart persistence and callback routing. Workspace forwarding, shortcuts, tray integration, standalone CLI/headless identity, and Git worktree orchestration remain future work.

## Known Issues
- Antigravity 2.12.2 uses both userData and process-home .gemini; user-data-only is insufficient.
- Follow-up visible probe observed two rendered first-run windows and clean shutdown; authenticated behavior still pending.
- Shared OAuth protocol/Credential Manager/DPAPI are not proved isolated.
- Synthetic home can hide existing tool config; never copy credentials to work around it.
- Vendor stdout contains ephemeral secrets; probe now discards output.

## Key Decisions
.NET 10/WPF; N UUID profiles; independent process home + user-data; CLI first; metadata workspace until verified; conservative process ownership; no profile-data purge.

## Build Status
.NET SDK 10.0.400 is installed locally in `.tools/dotnet`. `scripts/Build.ps1 -Test` builds all four projects with zero warnings/errors and reports 12/12 tests passed. `scripts/Publish.ps1` produced a self-contained win-x64 package and SHA-256 sidecar at `artifacts/package/20260907-213404/`.

## Test Status
On September 7, 2026: Core/process assertions 12/12 passed; `scripts/Test-CliConcurrency.py` passed 12 checks; `scripts/Test-Integration.ps1` passed 6 checks; packaged CLI help and packaged GUI startup smoke passed. Phase 0 probe evidence remains under `artifacts/probe`. Google login persistence, account separation, and OAuth callback routing remain manual verification requirements.

## Next Actions
Run `MANUAL_TEST_PLAN.md` with two test Google accounts. Preserve `manual-verification-required` and `unknown` capability statuses unless the user records direct evidence. Do not copy or inspect credentials while performing the test.
