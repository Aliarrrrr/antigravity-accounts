# Manual verification — process-local multi-account (0.4.3)

Real Google authentication is never automated. Do not send credentials, OAuth codes, token/cookie exports, account identifiers, or screenshots containing private account information. Record only pass/fail and non-sensitive behavior.

**0.4.0+ does not use Sandboxie.** Scope preparation needs no elevation. The only verified vendor build remains Antigravity Desktop **2.12.2** on Windows x64.

## Gate before any run

1. `agprofile setup` (or `doctor`): `executableDiscovery` is Verified, version is 2.12.2.
2. `configure-executable <path to Antigravity.exe>` if doctor reports `executable_not_pinned` — launch and callback handoff refuse unpinned binaries.
3. For two or more profiles that will sign in concurrently: `configure-deeplink register` (or GUI **Callbacks**).

## A. Already-signed-in concurrent windows (previously accepted)

1. Create A and B (`new`), each with a ready isolation scope (`Ready` in GUI / `fix` if needed).
2. Open A, sign in to Google account #1 in the official window.
3. Open B, sign in to Google account #2 in the official window.
4. Confirm both windows stay on different accounts without logging each other out.
5. `restart A` — A must not require a fresh sign-in.
6. Confirm B's window and process count stay unchanged while A restarts.

Pass criteria: two visible official windows, distinct accounts, restart persistence.

## B. Concurrent sign-in callback routing (0.4.1 repair — **required, still outstanding**)

With deep-link routing registered:

1. Stop A and B completely.
2. Start A and B at the same time; open Google sign-in in both windows before completing either.
3. Complete sign-in for A, then for B (or interleave if the vendor allows).
4. Confirm each OAuth callback lands in the window that is waiting (the account that finishes is the one shown in that profile).
5. Confirm no third Antigravity window appears that is not A or B.
6. `configure-deeplink restore` when finished, and confirm `antigravity://` returns to the previous handler if the vendor owns it.

Pass criteria: both profiles end signed in to the intended accounts; Hub logs/`callback` verb do not show dropped handoffs. **Do not treat concurrent sign-in as supported until this section passes once on 2.12.2.**

## C. Isolation proof and warm path

1. Open A through the Hub (GUI Open or `open A`). After cold start, `profiles/<uuid>/scope-attestation.json` exists.
2. Manually start `Antigravity.exe --user-data-dir=<A user-data>` **without** the Hub (no WSL_DISTRO_NAME). Confirm the next Hub `open A` refuses `isolation_scope_unverified` until the foreign instance is stopped and A is opened through the Hub.
3. After a Hub-verified open, `open A` again while it is running reports already-running / does not start a second root.

## D. Destructive and lifecycle entries

1. GUI **Force stop** and **Stop all** require an explicit confirmation.
2. CLI `stop --force` / `stop-all --force` still work for scripts (documented; no interactive prompt).
3. Remove registration keeps profile data on disk.

## E. Update revalidation

After Antigravity changes version, repeat A–C before treating multi-account isolation as supported. Unknown Antigravity versions remain blocked by the driver (`version_unverified`).
