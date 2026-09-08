# Manual verification — pending

September 8 user update: two-account checks have not yet been performed; the user will complete this checklist later. Engineering checks must not fill these account rows automatically.

Use two new Hub profiles; keep original installation profile untouched. Do not send credentials, OAuth codes, tokens, cookie exports or account screenshots to the developer. Record pass/fail and non-sensitive observations only.

1. Launch Pro A visibly. Confirm official Antigravity window is usable. Complete official Google A login yourself. Close A, relaunch; verify it still shows A.
2. Stop A while performing first login for Pro B, to reduce ambiguity in the shared OS deep-link handler. Launch B, sign into Google B officially. Close/relaunch B; confirm B persists.
3. Launch both A/B. Confirm two usable windows and distinct identities. Opening B must not focus/reuse A. Wait several minutes and recheck identities.
4. Restart A while B is active. Confirm B stays open and retains B; A retains A. Repeat reversing roles. Test launch-all and stop-all only for Hub-managed profiles.
5. Choose distinct themes/settings/projects in each app. Restart both and verify independence. Hub workspace association currently does not establish vendor positional workspace support.
6. With both running, test a required official reauthentication deliberately in the intended profile; verify callback returns to that profile. If callback goes to default/wrong instance, stop and report failure. Do not compensate by moving auth files.
7. Close Hub but leave profiles running. Reopen Hub, confirm correct status; duplicate launch must report running/focus. Close an app manually and refresh Hub.
8. Check synthetic-home tool behavior: Git identity, SSH, plugins and tool configuration may be absent. Configure normal non-secret preferences separately as needed; no automatic credential copying.
9. After vendor update, run doctor and repeat steps 1–6 before asserting long-term compatibility.
10. Create each profile's desktop shortcut. Close Hub, launch the shortcut and verify the intended identity. Rename a profile in Hub and confirm the original shortcut still launches that profile. If the portable package moves, recreate the shortcuts.

| Test | Status | Observation |
|---|---|---|
| Visible separate windows | observed (first-run only) | Two different windows rendered official onboarding; authenticated usability pending |
| A login survives restart | pending | Requires user OAuth |
| B login survives restart | pending | Requires user OAuth |
| A/B concurrently keep distinct identity | pending | Requires user observation |
| Restart/stop does not affect other identity | pending | Process-only stop separation observed automatically |
| Settings/project separation | pending | Directories observed, behavioral test pending |
| OAuth callback routing | pending | OS protocol registration may be shared |

Failure: mark auth isolation failed/unknown in report, preserve both data directories, stop product claims and revisit the driver. Never exchange tokens as a workaround.
