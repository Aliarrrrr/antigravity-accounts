# Verification matrix

This tracks release evidence, not merely the presence of code. Authenticated Google behavior is intentionally excluded from automation.

| Requirement | Evidence required | Current result |
|---|---|---|
| N profiles / rename | Create >2 UUID-backed profiles, rename without moving directory | passed (unit + CLI concurrency) |
| Configuration safety | Unknown schema and corrupt JSON rejected without overwrite; concurrent creates retained | passed (unit + CLI concurrency) |
| Path handling | Traversal, invalid UUID, reparse paths rejected; Unicode/spaces work | passed (unit; reparse/Unicode coverage remains limited) |
| Launch construction | User-data argument indivisible; scoped home/appdata; no inherited debug env | passed (unit) |
| Ownership | PID birth time and exact executable/user-data; foreign roots not stopped | passed for tested roots; fake-world ownership guards plus real integration reconciliation |
| CLI survives exit | Start via one CLI process, query/stop via another | passed (real integration) |
| Duplicate launch | Re-launching a running profile keeps one verified root | passed (unit + real integration) |
| Two instances | Two roots/backends and independent state locations | observed Phase 0 |
| Two visible windows | Two distinct first-run windows rendered | observed visible probe |
| Stop/restart isolation | A stops/restarts while B identity/root remains | passed (real integration) |
| Removal preserves data | Unregister profile, marker file remains | passed (unit + CLI concurrency + real integration) |
| Workspace honesty | Unsupported forwarding produces clear error; metadata association validated | passed (CLI concurrency script stores/clears metadata; forwarding intentionally unsupported) |
| Update guard | Version/path changes invalidate previous exact-version evidence | implemented; dedicated regression test pending |
| Logging | Allowed event schema; no vendor output or secret-shaped parameters | implemented; manual log review recommended |
| GUI | Real create/rename/select/status/doctor interactions | build + startup smoke passed; interactive operations remain manual |
| Shortcut | Link targets CLI + immutable UUID, no vendor argument duplication | pending tests |
| Packaging | Self-contained win-x64 CLI help and GUI startup | passed (`artifacts/package/20260907-213404`) |
| Real Google A/B | Distinct identities survive restarts and concurrent operation | manual-verification-required |
| Callback routing | Reauthentication reaches intended profile with two apps active | manual-verification-required |

Authenticating a test profile is never part of automation. Any observed failure is recorded rather than converted into supported status.
