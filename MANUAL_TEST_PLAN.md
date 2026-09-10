# Manual verification — isolated multi-account backend

Real Google authentication is never automated. Do not send credentials, OAuth codes, token/cookie exports, account identifiers, or screenshots containing private account information. Record only pass/fail and non-sensitive behavior.

## Accepted on 2026-09-09

Using Antigravity Desktop 2.12.2 and Sandboxie Plus 1.18.2 / core 5.73.2:

| Test | Result | Non-sensitive observation |
|---|---|---|
| Separate rendered windows | passed | One visible official Desktop window per box |
| Distinct accounts concurrently | passed | User confirmed A and B displayed their intended different accounts |
| Box-local authentication state | passed | A/B/host credential entry metadata and one-way fingerprints differed; token contents were not exported |
| Restart persistence | passed | A retained its intended account after full box-process restart |
| Cross-Profile restart isolation | passed | B root PID and process count stayed unchanged while A restarted |
| Host login non-interference | passed | Host entry size/fingerprint stayed unchanged |

The earlier directory-only and empty-box Sandboxie failures remain valuable historical evidence: a missing box-local `gemini:antigravity` read fell through to the host. The passing setup first claims that name inside each fresh box.

## Required setup checks for every Profile

1. `iso-status` reports `backendAvailable=true`, `boxExists=true`, and `keyringClaimed=true`.
2. `iso-writethrough <id>` reports `ready=true`. If not, save work and stop the affected box, then run the emitted `Set-IsolationWriteThrough.ps1` command from an elevated prompt.
3. `status <id>` reports the expected box name and exact owned root/user-data identity.
4. Never use `iso-prepare --fresh` on a box that has already signed in; it is only an acknowledgement for an unregistered, never-used box.

## Remaining manual gates

### A. Workspace direct-write

1. Create a disposable workspace outside the managed Profile root and associate it with A.
2. Apply the exact write-through rules reported by `iso-writethrough A`.
3. In A, create and modify a harmless marker file in that workspace.
4. Confirm the host filesystem and a normal editor see the change immediately.
5. Restart A and confirm the same file remains visible.
6. Confirm B cannot accidentally redirect A's workspace through its own Profile mapping. Do not test simultaneous edits to the same repository; Agent Safe worktrees are deferred.

### B. Logout non-interference

1. With A and B running and showing their intended accounts, log out of A using the official UI.
2. Confirm B remains logged into B without a process restart or account change.
3. Reauthenticate A through the official flow and confirm B remains unchanged.
4. Repeat with B logging out while A stays active.

### C. Product entry points

1. Launch A from GUI, B from its desktop shortcut, then run `launch-all`; no duplicate root should appear.
2. Restart A from GUI; B must remain running.
3. Close/reopen Hub while both remain active; both rows must reconcile to Running with their box names.
4. Save work, stop each isolated Profile, and confirm only its dedicated box reaches zero processes.

### D. Update revalidation

After Antigravity or Sandboxie changes version, repeat setup checks and tests A-C before treating account isolation as supported. Unknown Antigravity versions must remain blocked by the driver.

## Current machine note

Both current default Profile mappings now report `writeThroughReady=true` with no missing paths. This proves the intended bare `OpenFilePath` rules are configured; it does not replace the live workspace file creation/restart test in section A.
