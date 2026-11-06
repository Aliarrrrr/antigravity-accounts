# Antigravity Accounts

Group independently authorized Google accounts inside Antigravity's native model selector. Keep native model choices, the host theme and View Usage, and select which account the native language server uses. Each account retains its own quota; quotas are not pooled or transferred.

## Requirements and installation

This Windows x64 preview requires the complete [Accounts 0.4.0-preview.8 distribution](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.8) and a locally installed, reviewed Antigravity 2.29.0 or 2.28.2 program. Follow the distribution's root `README.md`: exit Antigravity completely, run `Install-Preview.cmd`, then launch through `Start-With-Accounts.cmd`.

Installing this plugin directory alone supplies metadata; it does not install the required Electron/HostAuth adaptation or enable switching. The preview is not Marketplace-listed. Unknown native-program or checked-interface changes are refused.

## Usage

Open the native model menu, choose Add account and complete authorization on Google's website. Set a unique primary account, select an account and fully restart once to enable independent HostAuth. Later selections wait for native work to finish and confirm identity/project/model/quota metadata without restarting the app. If a model is unavailable or confirmation fails, sending remains paused until resolved. A non-selected secondary account can be removed from the same menu with confirmation.

The component encrypts independently added credentials with Windows DPAPI CurrentUser. It does not import the official login database, Cookie store or Windows Credential Manager. The native language server sends model requests directly to Google. See the distribution's `VALIDATION.md` for checked behavior and limits; actual generation, quota charging, expired-token renewal and cross-account generation have not been verified.

Run `Restore.cmd` after fully exiting Antigravity to restore matching original programs. Account authorizations are retained locally. For documentation, source, support and the MIT license, see [the project](https://github.com/Aliarrrrr/antigravity-accounts).
