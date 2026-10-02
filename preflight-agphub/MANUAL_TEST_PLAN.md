# Manual verification — authenticated account-isolation failure

## Sandboxie diagnostic (closed as failed)

The original direct-profile failure remains authoritative. A separate diagnostic used Sandboxie Plus 1.18.2 and did not reuse or alter the old A/B data. It is closed as a failed backend experiment; the product does not enable it.

Current state file: `C:\Users\ZLF\AppData\Local\AntigravityProfileHub\sandboxie-p2\6a4933eec3f843b99a569e33e977b6da\state.json`.

1. Profile A official login was reported complete in `AGPH_P2_6a4933eec3_A`. Post-login inspection found all vendor processes in A and no unboxed callback process. No account or callback content was inspected.
2. A was stopped with sandbox data preserved and relaunched; the user confirmed its login persisted.
3. B was launched concurrently in fresh `AGPH_P2_6a4933eec3_B`; process inspection found no cross-box or unboxed vendor PID, but the user reported that B displayed A's existing session. This fails the Sandboxie route.
4. A was stopped with data preserved and B was launched alone for a bounded five-second read-only resource trace. The report is `artifacts/sandboxie-resource-trace/650eedee8550456490d9468204ed8342/report.json` (43,407 session entries, 18,157 B entries, 2,872 unique resources, 337 unmapped entries discarded, 144 truncated). It showed no direct B reads of the host user's existing Google/Antigravity configuration, cookie store, Local State, or credential/vault/DPAPI data; loading Windows System32 `DPAPI.DLL` was observed, and B's own user-data `Cookies-wal` is not host authentication storage. It did show shared authentication-related objects `protected_storage`, `SBIE_ProtectedStorage_Section`, and `SBIE_BOXED_ServiceCrypto_Mutex1`. No safe `ClosedFilePath`/`ClosedIpcPath` rule was derived.
5. A disposable privacy-mode C box launched, then ended after Sandboxie's approximately five-minute no-certificate evaluation. No login was attempted. Privacy Mode cannot be a dependency. Preserve P2 A/B data for diagnosis; do not integrate Sandboxie.

September 8 user update: A and B passed sequential restart-persistence checks. B's first launch showed A's existing session; after the user signed out in B and completed a later login, B retained that session after a Hub stop/relaunch. When A and B then ran concurrently, both showed the account signed in later. Authenticated account isolation failed. Preserve both profile data directories and do not claim separate-account support. Engineering checks must not fill these account rows automatically.

Use two new Hub profiles; keep original installation profile untouched. Do not send credentials, OAuth codes, tokens, cookie exports or account screenshots to the developer. Record pass/fail and non-sensitive observations only.

1. Launch Pro A visibly. Confirm official Antigravity window is usable. Complete official Google A login yourself. Close A, relaunch; verify it still shows A.
2. Stop A while performing first login for Pro B, to reduce ambiguity in the shared OS deep-link handler. Launch B, sign into Google B officially. Close/relaunch B; confirm B persists.
3. Do not launch A/B concurrently for different Google accounts on the current Desktop version. This check already failed: both windows displayed the account signed in later. A future retry requires an official profile-scoped authentication contract or separate Windows identity boundary.
4. Do not test cross-profile restart while both are authenticated on the current Desktop version. The Hub now blocks a second root; process-only restart behavior remains covered by disposable integration tests.
5. Choose distinct themes/settings/projects in each app. Restart both and verify independence. Hub workspace association currently does not establish vendor positional workspace support.
6. Do not trigger concurrent reauthentication on the current Desktop version. The shared `antigravity://` protocol has no Profile identifier, and concurrent authenticated use is incompatible. Do not compensate by moving auth files.
7. Close Hub but leave profiles running. Reopen Hub, confirm correct status; duplicate launch must report running/focus. Close an app manually and refresh Hub.
8. Check synthetic-home tool behavior: Git identity, SSH, plugins and tool configuration may be absent. Configure normal non-secret preferences separately as needed; no automatic credential copying.
9. After vendor update, run doctor and repeat steps 1–6 before asserting long-term compatibility.
10. Create each profile's desktop shortcut. Close Hub, launch the shortcut and verify the intended identity. Rename a profile in Hub and confirm the original shortcut still launches that profile. If the portable package moves, recreate the shortcuts.

| Test | Status | Observation |
|---|---|---|
| Visible separate windows | observed (first-run only) | Two different windows rendered official onboarding; authenticated usability pending |
| A login survives restart | passed (sequential only) | `Manual Pro A` was stopped and relaunched through Hub; user confirmed its then-current session remained signed in. This does not establish identity independence after B was used. |
| B login survives restart | passed (sequential only) | Initial B launch displayed A's existing session. After the user signed out in B and completed a later login, Hub stop/relaunch preserved that session. |
| A/B concurrently keep distinct identity | failed | Direct backend converged to the later account; the Sandboxie P2 user observation also reported B displaying A's existing session. |
| A/B process ownership and stop separation | passed | A/B process inventory showed correct box ownership, no PID overlap or unboxed vendor process, and stopping A left B running; this does not establish account isolation. |
| Settings/project separation | pending | Directories observed, behavioral test pending |
| OAuth callback routing | blocked | Concurrent authenticated operation is incompatible; shared OS protocol registration has no Profile identifier. |

Authenticated account isolation failed: preserve both data directories, do not claim separate-account support, and keep the Sandboxie experiment outside the product. Never exchange tokens, move auth files, or create synthetic entries for a real credential target as a workaround.
