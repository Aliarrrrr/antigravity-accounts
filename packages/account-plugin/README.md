# Antigravity Accounts

Group independently authorized Google accounts inside Antigravity's native model selector. Keep native model choices, the host theme and View Usage, and select which account the native language server uses. Each account retains its own quota; quotas are not pooled or transferred.

## Requirements and installation

This Windows x64 preview requires the complete Accounts 0.4.0-preview.16 distribution and a locally installed, reviewed Antigravity 2.32.1, 2.31.2, 2.31.0, 2.29.0 or 2.28.2 program. Preview.16 includes the unreleased preview.15 compatibility work and updates account-switch window access for BaseWindow/WebContentsView hosts. Follow the distribution's root `README.md`. Exit Antigravity completely, restore an existing Accounts installation with the new package's `Restore.cmd` when the host has not been updated, run the new `Install-Preview.cmd`, then launch through `Start-With-Accounts.cmd`. After an official update has replaced the programs, the new installer retains the previous installation record and prepares the reviewed new programs directly. Account authorizations are retained. With Chinese localization, restore the Chinese layer before modifying Accounts and reapply it after installing new Accounts. SSH remote sessions are outside this independent-account support scope.

Installing this plugin directory alone supplies metadata; it does not install the required Electron/HostAuth adaptation or enable switching. The preview is not Marketplace-listed. Unknown native-program or checked-interface changes are refused for patching. A complete official update can start through an explicit native-login confirmation, preserving saved Accounts authorizations for later recovery. Partial updates, modified Account hooks and incomplete recovery remain blocked.

If update handoff preparation fails, official installation continues and the application remains closed afterward. Use the existing Chinese shortcut to confirm the account for native sign-in. This path needs a Chinese launcher updated for preview.14 confirmation. Older Chinese launchers can still refuse unsupported updates. The external Chinese toolkit is maintained separately and is not included in the Accounts release.

## Usage

Open the native model menu and choose **账号设置与管理… (Account settings and management)**. This themed panel adds accounts through Google's website, edits labels, reauthorizes accounts, sets the primary account and confirms removal of inactive secondary accounts. The model picker keeps account selection and this single management entry. Fully restart once after initial configuration to enable independent HostAuth. Later selections wait for native work to finish and confirm identity/project/model/quota metadata without restarting the app. If a model is unavailable or confirmation fails, sending remains paused until resolved.

The panel offers automatic unchecked confirmation (default), automatic checked confirmation, or manual handling for data-use prompts after an account change. It confirms only after the native preference is saved. First-use terms, changed terms types, unrecognized dialogs and failed reads or saves remain visible. Preview.10 fixes a stale React-state read that previously prevented confirmation after a successful save. On installed Antigravity 2.29.0, switching back to the original account was observed to uncheck and close the prompt; normal startup was also checked after another restart, with no model requests.

Preview.14 was installed on Antigravity 2.31.2 with the current local Chinese launcher. Startup, native account groups and management were observed, with both saved accounts present and the default data-use option unchecked. This check made zero account switches and zero model submissions. The earlier short text-context check on 2.29.0 retains its recorded scope in `VALIDATION.md`. Real official updates, unknown-version identity changes, quota charging and expired-token renewal remain unverified.

Preview.15 passed the complete 264-test Node suites with separate 2.32.1 program inputs. Isolated native copies confirmed synthetic identity/project refresh and initial idle-state decoding, with zero model-generation requests. These checks do not establish a saved-account switch or quota charging on 2.32.1.

The installed preview.15 menu check made no saved-account switches and missed a BrowserWindow-only lookup in the switching path. Preview.16 passed 269 Node tests without skips. On installed 2.32.1, saved accounts completed A to B to A switching with the project and model retained, ending on the original primary account. This check made no model submissions and does not establish quota charging or cross-account generation. Full scope is recorded in `VALIDATION.md`.

The component encrypts independently added credentials with Windows DPAPI CurrentUser. It does not import the official login database, Cookie store or Windows Credential Manager. The native language server sends model requests directly to Google. See the distribution's `VALIDATION.md` for each check's behavior and limits.

Run `Restore.cmd` after fully exiting Antigravity to restore matching original programs. Account authorizations are retained locally. For documentation, source, support and the MIT license, see [the project](https://github.com/Aliarrrrr/antigravity-accounts).
