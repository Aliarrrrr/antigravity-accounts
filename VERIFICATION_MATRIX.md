# Verification matrix

This tracks release evidence, not merely the presence of code. Authenticated Google behavior is intentionally excluded from automation. The disposable probe evaluates process and directory behavior only; it does not evaluate authentication.

| Requirement | Evidence required | Current result |
|---|---|---|
| N profiles / rename | Create >2 UUID-backed profiles, rename without moving directory | passed (unit + CLI concurrency) |
| Configuration safety | Unknown schema and corrupt JSON rejected without overwrite; concurrent creates retained | passed (unit + CLI concurrency) |
| Path handling | Traversal, invalid UUID, reparse paths rejected; Unicode/spaces work | passed (unit; reparse/Unicode coverage remains limited) |
| Launch construction | User-data argument indivisible; scoped home/appdata; no inherited debug env | passed (unit) |
| Ownership | PID birth time and exact executable/user-data; foreign roots not stopped | passed for tested roots; fake-world ownership guards plus real integration reconciliation |
| CLI survives exit | Start via one CLI process, query/stop via another | passed (real integration) |
| Duplicate launch | Re-launching a running profile keeps one verified root | passed (unit + real integration) |
| Two instances | Two roots/backends and independent state locations | observed Phase 0; process/directory evidence only, not Google identity isolation |
| Two visible windows | Two distinct first-run windows rendered | observed visible probe; probe does not evaluate authentication |
| Stop/restart lifecycle | The only running root stops cleanly and restart replaces its PID | passed (unit + real integration; concurrent authenticated operation is intentionally blocked) |
| Removal preserves data | Unregister profile, marker file remains | passed (unit + CLI concurrency + real integration) |
| Workspace honesty | Unsupported forwarding produces clear error; metadata association validated | passed (CLI concurrency script stores/clears metadata; forwarding intentionally unsupported) |
| Update guard | Version/path changes invalidate previous exact-version evidence | passed: unknown-version launch leaves directories untouched; executable change clears attestation |
| Global launch gate | Independent Hub/CLI processes cannot race through different data roots; uncertain launches fail closed | passed: deterministic cross-root, delayed-discovery, cancellation and runtime-write-failure tests plus two real CLI races |
| Logging | Allowed event schema; no vendor output or secret-shaped parameters | ten integration events checked: only allowed fields and one fixed sanitized command template |
| GUI | Real create/rename/select/status/doctor interactions | create/rename/launch/graceful stop, status and doctor observed September 8; packaged executable picker opens; exhaustive interaction coverage remains partial |
| Shortcut | Link targets CLI + immutable UUID, no vendor argument duplication | COM regression and actual packaged .lnk lifecycle passed, including rename, duplicate launch and stop |
| Packaging | Self-contained win-x64 CLI help and GUI startup | passed: newest timestamped self-contained package; package shortcut lifecycle and launch-all gate 6/6 |
| Real Google A/B | A and B retain their chosen identity after restart and remain distinct during concurrent operation | failed manual verification: sequential A/B restart persistence passed, but both concurrent profiles showed the account signed in later; Desktop 2.12.2 does not support simultaneous different Google accounts under one Windows user |
| Callback routing | Reauthentication reaches intended profile with two apps active | blocked: concurrent authenticated operation is unsupported on Desktop 2.12.2; the shared protocol has no Profile identifier |

Authenticating a test profile is never part of automation. The observed Real Google A/B failure is recorded rather than converted into supported status; preserve profile data while the cause is investigated. Probe results cannot overturn or evaluate this authentication finding.
