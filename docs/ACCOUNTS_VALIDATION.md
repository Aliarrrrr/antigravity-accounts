# Accounts 0.4.0-preview.10: validation scope

This document distinguishes installed-client observations from checks with synthetic accounts, controlled processes and official program copies. The preview.10 results below were observed on 6 November 2026; earlier preview.8 results are retained with their original scope.

## Preview.10 installed application — 6 November 2026

Preview.10 was installed on Antigravity 2.29.0, retaining its account records and reapplying the existing Chinese localization. The installed preload was compared with the current source and matched. The model picker retained the official model options, account switching, View Usage and a single management entry.

During the preceding preview.9 check, the data-use preference was successfully saved as unchecked, but the modal stayed open: two React alternate parent chains both pointed to the current root, and the adapter read the older checked value. The regression check fails with that former resolver and passes with the child/sibling membership resolution used in preview.10.

After installing preview.10 and restarting, returning to the original account produced one Security Notice & Data Use modal. Observation recorded its checkbox becoming unchecked, followed by the modal disappearing automatically. The model picker then identified the original account as active and retained Gemini 3.8 Flash High. The temporary debugging session was closed and the App started normally without the debugging endpoint. This check involved restarts; it is not a same-process round-trip result for preview.10.

The agent sent **zero model requests**. Native OAuth refresh and background account/project/quota/settings requests were permitted for the check, but their total count was not measured. Data-use automation normally uses one native settings read and one save per eligible account-change modal; organization-disabled collection avoids those extra calls.

The management panel was checked separately with synthetic accounts and the native stylesheet under dark/light themes and a 390-pixel viewport. Checks covered trusted label editing, deletion cancellation, all three data-use preferences, scrolling and focus restoration. These results do not represent actual Google sign-in, deletion or labeling of a real account.

The component runner passed 39 checks. The final local Node run passed 217 checks without skips using separate original 2.29.0 program copies. Package inspection checks distribution contents; the release workflow records the exact committed build separately. No new live-account check was performed on 2.28.2 for preview.10.

## Preview.8 installed application (earlier results)

The final preview.8 runtime was installed on Antigravity 2.29.0. Its native model selector showed account groups, original model choices, Add account and View Usage. Account controls inherited the host font and theme. The same application window and workspace remained open, and the system-tray setting was restored.

After the user removed a secondary account that required Google verification, the remaining two accounts completed an authorized Account A → Account B → Account A metadata round trip. Native identity, project, model-list and quota confirmation completed. The application, language server and account component retained their process identities and start times; the original window, workspace, selected Gemini 3.8 Flash High model and empty input remained. Both official Security Notice & Data Use choices were handled by the user. No further account changes were made for this check.

The agent sent **zero model requests**. Native confirmation can cause additional Google background metadata requests; the result does not establish a total external-request count.

An earlier account returned `Verify your account to continue.` during quota confirmation. The host kept new sending paused and identified the need for Google verification. The user chose not to verify that account and removed it from the local list. This failure was not recorded as a successful account change.

## Local checks

| Area | What was checked | Limit |
| --- | --- | --- |
| Account component | Primary/secondary relationships, local deletion, transaction generations, cancellation, DPAPI storage, stdio handling and renewal races with controlled OAuth implementations | Does not verify Google's actual token-renewal behavior |
| Model menu and frontend | Grouping, original model/usage controls, keyboard focus, scrolling, input methods, listener cleanup and model preservation using native frontend code and synthetic state | Does not establish real generation or charging |
| Native adaptation | Reviewed 2.29.0 and 2.28.2 programs; diagnostic-route and project-refresh changes; rejection of unknown checked-source/native changes | No guarantee for an unknown future program |
| Installation and restoration | Program-copy apply/restore, refusal to overwrite updated programs, independent repair cache and shared account-state-directory selection | Controlled directories do not establish a future updater's behavior |
| Update handoff | External PowerShell process waited for controlled app/installer processes and changing files, then invoked the repair/start entry | No actual NSIS upgrade was performed |
| Update repair | Replacing installed files with reviewed same-version official files was followed by cached Accounts repair and reapplication of Chinese localization | Same-version replacement, not an unknown-version update |

The menu was also checked with the native stylesheet under light/dark themes, small window heights and synthetic account lists of different sizes. Account controls and model choices remained reachable by scrolling. The external Chinese localization toolkit was checked in its own workspace and is not included here.

## Not verified

- Actual model generation and quota charging to the selected account.
- Real expired-token renewal and generation continuing across account changes.
- Every combination of native tools, subagents, remote-control work and scheduled tasks.
- A real NSIS update, an unknown future Antigravity version or Marketplace installation.

The adapter waits when work is active or unreadable, and keeps sending paused when identity/project/quota confirmation fails. Those failure rules do not establish that every native task combination has been executed.

## Preview.8 repository release checks — 6 November 2026

After removing the former Profile Hub build targets, the standalone Accounts Release build and component runner passed all 37 checks. The Node run reported 190 passed and one skipped out of 191; the missing Electron helper was then supplied explicitly, and that skipped helper case passed when rerun. These checks used local test inputs and made no account, OAuth or model requests.

The package preflight verified 42 payload files plus its manifest. This checks distribution contents and does not represent a new installation or live-account run of the packaged artifact. The final release build and CI results are reported with the release itself.

## Reproducing source checks

Use .NET SDK 10.0.400 and locked JavaScript dependencies. From the repository root:

```powershell
./scripts/Build.ps1 -DotNet dotnet -LockedRestore -Test
npm ci --prefix packages/hostauth-adapter --ignore-scripts --no-audit --no-fund
node --test packages/hostauth-adapter/test/*.test.mjs packages/account-plugin/test/*.test.mjs packages/account-plugin/test/*.test.cjs
```

The .NET tests use a console runner; `dotnet test` alone does not execute it. Tests needing official program copies accept `ANTIGRAVITY_PROGRAM_ASAR`, `ANTIGRAVITY_LANGUAGE_SERVER` or `ACCOUNTS_TEST_INSTALL`; `ACCOUNTS_ELECTRON_EXECUTABLE` selects a local Electron helper. Supply separate original program copies, not an account-data directory. Missing optional program files cause relevant cases to be skipped; a skip is not a successful native integration check.

Raw local records, personal identifiers, credentials and official program copies are excluded from source and distribution. Package manifests record build provenance and content checks separately; they must not be interpreted as a fresh live-account test of every packaged build.
