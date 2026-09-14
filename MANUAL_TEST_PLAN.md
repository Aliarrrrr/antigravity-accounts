# Manual verification — process-local multi-account (0.4.3+)

Real Google authentication is never automated. Do not send credentials, OAuth codes, token/cookie exports, account identifiers, or screenshots containing private account information. Record only pass/fail and non-sensitive behavior.

**0.4.0+ does not use Sandboxie.** Scope preparation needs no elevation. The only verified vendor build remains Antigravity Desktop **2.12.2** on Windows x64.

## Acceptance record — 2026-09-13 (0.4.3-p1)

| Section | Result | Notes |
|---|---|---|
| Gate (pin, doctor, deeplink register) | passed | `configure-executable` required; doctor showed 2.12.2 |
| A. Already-signed-in concurrent windows | passed | Distinct accounts; restart A kept login; B unaffected |
| B. Sign-in with callback routing | passed | Two roots only; no third window; OAuth landed in the waiting profile; user confirmed no cross-window callback |
| C. Isolation proof / warm path | passed | Attestation written on cold start; warm `open` = already running; foreign `--user-data-dir` start → `isolation_scope_unverified` until Hub reopen |
| D. Destructive confirmations | GUI code path | CLI force remains unprompted by design |

Deep-link registration was restored to the vendor handler after B (`configure-deeplink restore`).

Pending grants are multi-slot from the native-parity branch (max 4): two mid-OAuth waiters without an attributable `state` fail closed (`deeplink_pending_ambiguous`) instead of last-writer steal.

## Gate before any run

1. `agprofile setup` (or `doctor`): `executableDiscovery` is Verified, version is 2.12.2.
2. `configure-executable <path to Antigravity.exe>` if doctor reports `executable_not_pinned` — launch and callback handoff refuse unpinned binaries.
3. For two or more profiles that will sign in concurrently: `configure-deeplink register` (or GUI **Callbacks**).

## A. Already-signed-in concurrent windows (accepted 2026-09-13)

1. Create A and B (`new`), each with a ready isolation scope (`Ready` in GUI / `fix` if needed).
2. Open A, sign in to Google account #1 in the official window.
3. Open B, sign in to Google account #2 in the official window.
4. Confirm both windows stay on different accounts without logging each other out.
5. `restart A` — A must not require a fresh sign-in.
6. Confirm B's window and process count stay unchanged while A restarts.

Pass criteria: two visible official windows, distinct accounts, restart persistence.

## B. Concurrent sign-in callback routing (accepted 2026-09-13)

With deep-link routing registered:

1. Stop A and B completely.
2. Start A and B at the same time; open Google sign-in in both windows before completing either.
3. Complete sign-in for A, then for B (or interleave if the vendor allows).
4. Confirm each OAuth callback lands in the window that is waiting (the account that finishes is the one shown in that profile).
5. Confirm no third Antigravity window appears that is not A or B.
6. `configure-deeplink restore` when finished, and confirm `antigravity://` returns to the previous handler if the vendor owns it.

Pass criteria: both profiles end signed in to the intended accounts; Hub does not drop handoffs. Multi-slot pending keeps both waiters; if the callback cannot be attributed, Hub reports `deeplink_pending_ambiguous` and does not guess a window.

## C. Isolation proof and warm path (accepted 2026-09-13)

1. Open A through the Hub (GUI Open or `open A`). After cold start, `profiles/<uuid>/scope-attestation.json` exists.
2. Manually start `Antigravity.exe --user-data-dir=<A user-data>` **without** the Hub (no WSL_DISTRO_NAME). Confirm the next Hub `open A` refuses `isolation_scope_unverified` until the foreign instance is stopped and A is opened through the Hub.
3. After a Hub-verified open, `open A` again while it is running reports already-running / does not start a second root.

## D. Destructive and lifecycle entries

1. GUI **Force stop** and **Stop all** require an explicit confirmation.
2. CLI `stop --force` / `stop-all --force` still work for scripts (documented; no interactive prompt).
3. Remove registration keeps profile data on disk.

## E. Update revalidation

After Antigravity changes version, repeat A–C before treating multi-account isolation as supported. Unknown Antigravity versions remain blocked by the driver (`version_unverified`).

## F. Native-parity extras (pending desktop acceptance)

Record pass/fail only. Do not copy conversation contents or key material into notes.

### F1. Toolchain bridge

1. With a prepared Profile A **stopped**: `iso-bridge status A` → `.ssh`/`.gitconfig` Ready (or Absent if the host has no such files).
2. `iso-bridge prepare A` is idempotent; `home/.ssh` is a junction to `%USERPROFILE%\.ssh`; `home/.agph-bridge.json` lists the entry.
3. Open A through the Hub; inside a workspace run `git config --global --list` and confirm host `.gitconfig` is visible; `ssh -G example.com` (or equivalent) resolves the host `.ssh` config. **Do not print private keys.**
4. Create a harmless file from the Agent in the workspace; confirm it appears on the host path immediately (process-local, no sandbox).
5. Stop A; `iso-bridge remove A` deletes the junction; `%USERPROFILE%\.ssh` still exists and is intact.
6. Repeat `fix A` / `new C` with bridge enabled in settings: prepare still yields Ready without elevation.

Pass criteria: Agent tooling sees host SSH/Git config; host paths survive remove.

### F2. Conversation import and list

1. Identify a **disposable** source under `%USERPROFILE%\.gemini\antigravity` (or a copy). Prefer a scratch copy for the first run.
2. `conversations plan A --from <source>` lists `*.db` counts/sizes; no files are written.
3. `conversations import A --from <source> --dry-run` matches the plan.
4. With A stopped and destination empty: `conversations import A --from <source>` copies databases + `brain/<id>/`.
5. `conversations ls A` shows ids/sizes/mtimes; titles may be empty.
6. Open A through the Hub; confirm the vendor UI lists the imported conversations (or none if the vendor rejects foreign DBs — record which).
7. Import again without `--force` → `import_destination_not_empty`. With `--force` → prior DBs land under `conversations-backup-*`.
8. Confirm no token/Cookie files were copied (source `installation_id` / credential paths absent from the Profile).

Pass criteria: copy-only import works or fails closed; host token store untouched.

### F3. Workspace open fallback

1. `workspace A <existing folder>`; `open-workspace A` opens that folder in Explorer.
2. `launch A --workspace <path>` still returns `workspace_forwarding_unsupported` / equivalent — Desktop 2.12.2 has no auto-open contract.
3. Inside the opened Antigravity window, manually Open Folder once; conversation remains associated by the vendor, not by Hub.

Pass criteria: no fake launch argument; Explorer open works.

### F4. Multi-slot OAuth (optional, slow)

1. Register deeplink; stop A and B; open both; start Google sign-in in both before completing either.
2. Complete A, then B. Both end signed in; no third root.
3. If the Hub reports `deeplink_pending_ambiguous`, finish one side first and retry — that is a pass for fail-closed behavior, not a regression.

Pass criteria: no cross-window steal; ambiguous case fails closed.
