# Accounts 0.4.0-preview.8: validation scope

This document summarizes checks completed during preview.8 development on 5–6 November 2026. It distinguishes the installed application's observed behavior from checks using synthetic accounts, controlled processes or official program copies. Packaging and repository reorganization do not extend the runtime results below.

## Installed application

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

## Repository release checks — 6 November 2026

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
