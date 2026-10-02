# Verification matrix

This tracks release evidence, not merely code presence. Google authentication remains user-observed; automated tests cover routing, state and safety invariants without handling real credentials.

| Requirement | Evidence required | Current result |
|---|---|---|
| N profiles / rename | Create >2 UUID-backed profiles, rename without moving directory | passed (unit + CLI concurrency) |
| Configuration safety | Unknown schema and corrupt JSON rejected without overwrite; concurrent creates retained | passed (unit + CLI concurrency) |
| Path handling | Traversal, invalid UUID, reparse paths rejected; Unicode/spaces work | passed (unit; reparse/Unicode coverage remains limited) |
| Launch construction | User-data argument indivisible; scoped home/appdata; no inherited debug env | passed (unit) |
| Ownership | PID birth time and exact executable/user-data; foreign roots not stopped | passed for tested roots; fake-world ownership guards plus real integration reconciliation |
| CLI survives exit | Start via one CLI process, query/stop via another | passed (real integration) |
| Duplicate launch | Re-launching a running profile keeps one verified root | passed (unit + real integration) |
| Two instances | Two roots/backends, boxes and independent state locations | passed process probe and authenticated box acceptance |
| Two visible windows | Two distinct authenticated official windows rendered | passed manual acceptance |
| Stop/restart lifecycle | Restart A without replacing/stopping B | passed manual acceptance; deterministic Core routing test passed |
| Removal preserves data | Unregister profile, marker file remains | passed (unit + CLI concurrency + real integration) |
| Workspace honesty | Unsupported forwarding produces clear error; metadata association validated | passed (CLI concurrency script stores/clears metadata; forwarding intentionally unsupported) |
| Update guard | Version/path changes invalidate previous exact-version evidence | passed: unknown-version launch leaves directories untouched; executable change clears attestation |
| Global launch gate | Independent Hub/CLI processes cannot race through different data roots; uncertain launches fail closed | passed: deterministic cross-root, delayed-discovery, cancellation and runtime-write-failure tests plus two real CLI races |
| Logging | Allowed event schema; no vendor output or secret-shaped parameters | ten integration events checked: only allowed fields and one fixed sanitized command template |
| Isolation mapping safety | Atomic/locked schema, unique boxes, strict shim result, missing-box refusal | passed deterministic tests; host-scope shim guard passed before/after metadata check |
| Batch isolation routing | All prepared Profiles route through boxes; mixed set starts none | passed deterministic Core tests |
| Write-through detection | Expected Profile/workspace paths match actual `OpenFilePath.N` rules | implemented; current A/B correctly report five missing paths each |
| GUI | Isolation state/actions plus existing lifecycle controls render and route through Core | source and self-contained package visual smoke passed; Doctor state verified |
| Shortcut | Link targets CLI + immutable UUID, no vendor argument duplication | COM regression and actual packaged .lnk lifecycle passed, including rename, duplicate launch and stop |
| Packaging | Self-contained win-x64 package includes required setup scripts and capability docs | passed manifest/script/help/GUI smoke; full real-process shortcut smoke deferred while accepted A/B are active |
| Real Google A/B | A and B retain distinct identities concurrently and through restart | passed manual acceptance on Desktop 2.12.2 + Sandboxie 1.18.2/core 5.73.2 |
| Logout/re-auth isolation | Logging out/re-authenticating one Profile leaves the other unchanged | pending manual acceptance |
| Workspace host writes | Boxed edit appears immediately on host and survives restart | pending after correcting current rules |

The older directory-only A/B failure remains part of the evidence: it established that path separation alone is insufficient. Only the tested per-box keyring claim changes the account-isolation result. Preserve all Profile/box data and never substitute automated inference for the remaining manual rows.
