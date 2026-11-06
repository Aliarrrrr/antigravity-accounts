# Antigravity Accounts

Group independently authorized Google accounts inside Antigravity's native model selector. Keep native model choices, the host theme and View Usage, and select which account the native language server uses. Each account retains its own quota; quotas are not pooled or transferred.

## Requirements and installation

This Windows x64 preview requires the complete [Accounts 0.4.0-preview.10 distribution](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.10) and a locally installed, reviewed Antigravity 2.29.0 or 2.28.2 program. Follow the distribution's root `README.md`: exit Antigravity completely, restore an existing Accounts installation with its old `Restore.cmd`, run the new `Install-Preview.cmd`, then launch through `Start-With-Accounts.cmd`. Account authorizations are retained. With Chinese localization, restore the Chinese layer before restoring old Accounts and reapply it after installing new Accounts.

Installing this plugin directory alone supplies metadata; it does not install the required Electron/HostAuth adaptation or enable switching. The preview is not Marketplace-listed. Unknown native-program or checked-interface changes are refused.

## Usage

Open the native model menu and choose **账号设置与管理… (Account settings and management)**. This themed panel adds accounts through Google's website, edits labels, reauthorizes accounts, sets the primary account and confirms removal of inactive secondary accounts. The model picker keeps account selection and this single management entry. Fully restart once after initial configuration to enable independent HostAuth. Later selections wait for native work to finish and confirm identity/project/model/quota metadata without restarting the app. If a model is unavailable or confirmation fails, sending remains paused until resolved.

The panel offers automatic unchecked confirmation (default), automatic checked confirmation, or manual handling for data-use prompts after an account change. It confirms only after the native preference is saved. First-use terms, changed terms types, unrecognized dialogs and failed reads or saves remain visible. Preview.10 fixes a stale React-state read that previously prevented confirmation after a successful save. On installed Antigravity 2.29.0, switching back to the original account was observed to uncheck and close the prompt; normal startup was also checked after another restart, with no model requests.

The component encrypts independently added credentials with Windows DPAPI CurrentUser. It does not import the official login database, Cookie store or Windows Credential Manager. The native language server sends model requests directly to Google. See the distribution's `VALIDATION.md` for checked behavior and limits; actual generation, quota charging, expired-token renewal and cross-account generation have not been verified.

Run `Restore.cmd` after fully exiting Antigravity to restore matching original programs. Account authorizations are retained locally. For documentation, source, support and the MIT license, see [the project](https://github.com/Aliarrrrr/antigravity-accounts).
