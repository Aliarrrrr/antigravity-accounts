# Antigravity Accounts

[English](README.md) · [简体中文](README.zh-CN.md)

**Use your authorized Google accounts from Antigravity’s native model picker.**

![Preview 0.4.0-preview.11](https://img.shields.io/badge/preview-0.4.0--preview.11-8b5cf6)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
[![MIT License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Antigravity Accounts adds account groups to the existing model menu, with the host’s model options, theme and View Usage entry. One primary account anchors the configuration; additional accounts can be selected in the same window. Each account uses its own quota and service eligibility.

This is an **unofficial Windows host-adapter preview**, distributed as a ZIP. It is not listed in the Antigravity Marketplace, and installing `plugin.json` alone does not enable account switching.

[Download preview.11](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.11) · [Installation guide](docs/ACCOUNTS_PLUGIN.md) · [Changelog](CHANGELOG.md) · [Validation](docs/ACCOUNTS_VALIDATION.md)

## Install

| Requirement | Supported configuration |
| --- | --- |
| Operating system | Windows x64 |
| Antigravity App | Reviewed 2.29.0 and 2.28.2 program combinations |
| Additional runtimes | None for the packaged release; .NET is self-contained and installer helpers use the installed Electron runtime |

1. Download and fully extract [AntigravityAccounts-0.4.0-preview.11-win-x64.zip](https://github.com/Aliarrrrr/antigravity-accounts/releases/download/accounts-v0.4.0-preview.11/AntigravityAccounts-0.4.0-preview.11-win-x64.zip).
2. Exit Antigravity completely. If it stays in the system tray, choose **Exit** from the tray menu.
3. Run `Install-Preview.cmd` from the extracted `AntigravityAccounts` directory, then start Antigravity normally.

The installer checks the installed programs, preserves originals and applies the local adaptation. It does not modify `Antigravity.exe`. Keep the complete extracted package and its checksum file for recovery.

When upgrading an existing Accounts installation, exit Antigravity and run `Restore.cmd` from the **newly extracted preview.11 package**, then run its `Install-Preview.cmd`. Restore reads the existing installation receipt and saved originals; account authorizations are retained. Older helpers can incorrectly treat a verified Node sidecar as a running App when Windows path casing differs. With Chinese localization, use this order: **restore the Chinese layer → restore existing Accounts with the new package → install new Accounts → reapply the Chinese layer**. See the [detailed guide](docs/ACCOUNTS_PLUGIN.md#安装).

For a custom installation, set `ANTIGRAVITY_INSTALL_ROOT` to the directory containing `Antigravity.exe` before running the installer. This repository is private; downloads and issues require repository access.

## Features

- **Native model menu:** compact account rows separate names and status, with dedicated selection, waiting, chevron and settings icons alongside the official model options.
- **Account switching in one window:** subsequent switches do not require restarting the App or language service. The adapter waits for generation, tools and background work to finish before committing a switch.
- **Model selection protection:** an available concrete model is preserved. If it is unavailable on the selected account, sending waits for an explicit choice in the native model list.
- **Account settings and management:** one themed panel with account-status labels, fewer repeated emails, consistent controls and narrow-window layout for adding, deleting, labeling and reauthorizing accounts, setting the primary account and choosing a data-use prompt preference.
- **Update recovery:** cached adapter files and supported launch paths can reapply the adaptation after compatible program replacement.

## Use

Management actions live in **账号设置… (Account settings)**. The model picker keeps account selection and this single management entry.

1. Open the model picker, choose **账号设置… (Account settings)**, then **添加账号 (Add account)**. Complete authorization in the official Google page.
2. In the management panel, set one account as **主账号 (Primary account)**. After the first setup, exit and restart Antigravity once to activate the independent account provider.
3. Select another account row in the model picker. The adapter waits for native work to become idle, commits the selection and confirms the account’s metadata.
4. Continue with the official model options. If the previous model is unavailable, select an available model before sending.

The current group shows **正在使用 (Active)** after confirmation. Waiting can be cancelled without cancelling the original task. A failed confirmation pauses new submissions and provides a **重新确认此账号 (Reconfirm account)** action.

To remove an inactive attached account, open the management panel, choose **删除账号 (Delete account)**, check the email, then choose **确认删除 (Confirm deletion)**. Switch away from the current account first. The primary account cannot be removed while the provider is running. Removal deletes the plugin’s local account record; it does not delete the Google account.

The panel also edits labels and reauthorizes saved accounts. A pending Google authorization can be cancelled there without changing saved accounts. Its **数据使用授权 (Data-use consent)** setting offers three choices:

| Choice | Behavior after an account change |
| --- | --- |
| Automatic, unchecked — default | Save data collection as unchecked, then confirm the native prompt after the save succeeds |
| Automatic, checked | Save data collection as checked, then confirm after the save succeeds |
| Manual | Leave the native prompt for you to handle |

First-use terms, a changed terms type, unrecognized dialogs and failed reads or saves remain visible. Google identity verification and eligibility prompts still require the account owner. Preview.10 fixes a stale React-state read that could leave the prompt open after an unchecked preference had already been saved.

## Compatibility and updates

Compatibility is checked against reviewed host scripts, RPC dependencies and native program profiles. A version-number change can be accepted when those checked program contents still match. Unknown native or relevant source changes are refused.

For compatible replacements, use `Start-With-Accounts.cmd` before launching. When the existing Chinese launch protection is installed, its launcher and watcher restore Accounts before applying Chinese localization. The App’s update handoff prepares an external helper that waits for the App and installer to exit before repair and relaunch.

Directly opening an EXE after an unknown update can bypass these launch checks. Preserve the saved originals and follow the [recovery guide](docs/ACCOUNTS_PLUGIN.md#更新与恢复); do not copy an old program backup over a newer official installation.

### Verification scope

The preview.11 UI build was installed on Antigravity 2.29.0 with Chinese localization retained. The native menu, management dialog, current/primary status labels, removal of duplicate identity emails, Escape dismissal and return of the focus ring to the model trigger were observed. This visual check made **zero new model submissions, account switches or account edits**. Light/dark themes, a 390 × 620 viewport, long labels, Enter-to-save, login cancellation and Tab wrapping were checked separately with synthetic browser state.

Preview.8 on installed Antigravity 2.29.0 completed a metadata-only **Account A → Account B → Account A** round trip while retaining the App and language-service processes, window, workspace and available concrete model.

After installing preview.10 on 2.29.0, switching back to the original account was observed to uncheck the data-use option and close the prompt automatically. The original account and model were restored, and normal startup was checked after another restart. The agent initiated **zero model requests**. This does not establish a real-account switching result for 2.28.2.

On 7 November, before the preview.11 visual changes, an authorized development-package check completed three pure-text submissions in one conversation across Account A → Account B → Account A. It retained the window, project, conversation ID, drafts, model and process identities; later replies recalled markers supplied only to Account A. This establishes that short text-context case, rather than a repeat of the final visual build. Exact quota charging, attachments, tool/subagent history, long-context continuation, expired-token renewal, sends queued during switching and a real NSIS upgrade remain unverified. See the [validation record](docs/ACCOUNTS_VALIDATION.md) and [implementation audit](docs/ACCOUNTS_AUDIT_2026-11-07.md).

Native plugin discovery and persisted enabled preferences worked in an isolated service check. The Accounts HostAuth lifecycle is not yet controlled by the native plugin switch; see [integration feasibility](docs/ACCOUNTS_EXTENSION_INTEGRATION.md).

## Uninstall / restore

Exit Antigravity completely and run:

```powershell
.\Restore.cmd
```

This restores the saved official programs and removes the adapter runtime. Saved account records remain for a later reinstall. To remove an account record, use the management panel’s deletion action before restoring. Keep the `restore-*` directories in the registered Accounts state directory.

## Data and authorization

- Accounts are added through official Google OAuth. The component stores their credentials with **Windows DPAPI CurrentUser** encryption.
- The installer records the actual account-directory location. The component, provider marker and update recovery use the same directory even when Windows maps LocalAppData differently for the launching process.
- The native language service sends model requests directly to Google. The plugin does not proxy inference requests or import the official login database, Cookies or Windows Credential Manager entries.
- The release ZIP contains the independent component and adapter sources, not personal account data or official Antigravity program binaries.

Authorization endpoints, requested scopes and credential flow are documented in [Data and authorization](docs/ACCOUNTS_PLUGIN.md#授权与本机数据). For sensitive reports, read [SECURITY.md](SECURITY.md).

## Development

Building requires **PowerShell 7**, **.NET SDK 10.0.400** and **Node.js 24**. The SDK is pinned in `global.json`; build scripts do not install a toolchain.

```powershell
.\scripts\Build.ps1 -LockedRestore -Test
.\scripts\Publish-Accounts.ps1 -DotNet (Get-Command dotnet).Source
```

JavaScript tests:

```powershell
npm ci --prefix packages/hostauth-adapter --ignore-scripts --no-audit --no-fund
node --test packages/account-plugin/test/*.test.cjs packages/account-plugin/test/*.test.mjs packages/hostauth-adapter/test/*.test.mjs
```

The package command produces a ZIP, its JSON checksum and `SHA256SUMS.txt`. A release build adds `-Release` and requires a clean checkout at `accounts-v<version>`. `scripts/Test-AccountsPackage.ps1 -PackageRoot <extracted-package-directory>` checks the packaged file list and contents.

Native-program tests need unmodified program copies supplied with `ANTIGRAVITY_PROGRAM_ASAR` and `ANTIGRAVITY_LANGUAGE_SERVER`; package tests also use `ACCOUNTS_TEST_INSTALL`. Missing inputs can cause skips, so check the test summary. Never commit or distribute those official program copies or local account files.

| Path | Purpose |
| --- | --- |
| `src/Antigravity.Accounts` | OAuth, encrypted account storage and stdio component |
| `packages/account-plugin` | Installation, recovery and plugin metadata |
| `packages/hostauth-adapter` | Native menu, HostAuth integration and switching coordination |
| `tests/Antigravity.Accounts.Tests` | Component checks with synthetic accounts |
| `scripts` | Build, test and packaging commands |
| `docs` | Installation and validation details |

The earlier Profile Hub application is retained in Git history. The active project and release package are Antigravity Accounts. See [project history](docs/HISTORY.md).

## Support and contributing

Use [Issues](https://github.com/Aliarrrrr/antigravity-accounts/issues) for installation or compatibility problems and Pull Requests for changes. Include the Antigravity version, plugin version, reproduction steps and a bounded error code. Omit account files, callback URLs, credentials and conversation content.

For code changes, run the affected component or JavaScript checks and state which client behavior was actually checked. Maintained by [Aliarrrrr](https://github.com/Aliarrrrr).

## License

[MIT](LICENSE).
