# Security policy

Antigravity Accounts is an unofficial Windows host adapter. It modifies reviewed local Antigravity program files and runs an independent account component as the current Windows user. The supported preview and client combinations are listed in [README.md](README.md#compatibility-and-updates).

## Report a vulnerability

For a suspected credential exposure, account-identity mix-up or unsafe file replacement, arrange a private report before sharing technical details. Use GitHub private vulnerability reporting if it is available for this repository. Otherwise, open a minimal issue asking the [maintainer](https://github.com/Aliarrrrr) for a private reporting channel, without including the sensitive details.

For an ordinary installation or compatibility issue, include only:

- Antigravity and Accounts versions.
- Reproduction steps and the expected versus observed result.
- A bounded error code or a small, redacted excerpt that contains no credentials, account identifiers or conversation content.

Do not upload an Accounts state directory, encrypted account store, OAuth callback URL, access or refresh token, Cookie, official authentication database, full language-service log, or personal project/session files. Redact personal paths and email addresses from screenshots.

## Account and credential flow

Accounts are added by the owner through Google's official OAuth page. The independent component stores these credentials with Windows DPAPI CurrentUser encryption and uses them for the selected account. The menu and public component state expose presentation metadata rather than tokens.

The local HostBridge supplies the selected independent account's credentials to the native language service. That service sends model requests directly to Google. The account component has no prompt-submission method, and the adapter does not proxy inference requests or import the official login database, Cookies or Windows Credential Manager entries.

The authorization flow uses `accounts.google.com`; token exchange and renewal use `oauth2.googleapis.com`; account identity lookup uses `openidconnect.googleapis.com`. Requested scopes and the installed-client configuration are described in [authorization and data](docs/ACCOUNTS_PLUGIN.md#授权与本机数据).

DPAPI CurrentUser protects stored credentials for the Windows user. The component and native language service run with that user's permissions; this plugin does not provide process or operating-system isolation.

Deleting an account removes its record from this plugin. It does not delete the Google account or revoke the server-side Google authorization. Use the Google account's access settings if revocation is also required.

## Switching and task handling

The adapter pauses new submissions, waits for native generation, tools and background work to finish, then commits the new account selection. It confirms account identity, project, model and quota metadata before allowing new submissions. An unconfirmed identity remains blocked; it does not silently select another account.

An available concrete model is preserved. If the selected account lacks it, the user must choose an available model in the native list. Cancelling a pending switch leaves the original task running. The current account and primary account cannot be removed while the independent provider is running; inactive attached-account deletion requires confirmation.

Google security and data-use prompts remain decisions for the account owner. The plugin does not complete these prompts on the owner's behalf.

## Installation, updates and recovery

The installer checks reviewed host scripts, RPC dependencies and native program profiles before changing the local installation. Originals are preserved for restore. Unknown relevant changes are refused, and old program backups are not written over a newer official installation.

The registered account directory is shared by the component, provider marker and update recovery. Public configuration stores that location so a different Windows launch context cannot silently substitute an empty default directory. Missing or conflicting explicit locations stop the affected startup or recovery path.

Repair and launch use per-install locks and stopped-process checks. An App or installer that is still running defers file replacement. The update handoff uses an external helper so it can wait for the App and installer to exit before repair and relaunch.

The distribution contains the independent component and adapter files. It excludes personal account data and official Antigravity binaries; local adapted copies are produced during installation. Restoring the official programs retains account records unless the owner separately removes them.

## Validation limits

Metadata-only account switching has been checked in an installed Antigravity 2.29.0 window. This does not establish model generation, quota charging, cross-account generation continuation or an actual NSIS upgrade. Version-specific results are kept in [the validation record](docs/ACCOUNTS_VALIDATION.md).

The earlier Profile Hub implementation remains in Git history. Its behavior and prior security notes should not be treated as the current Accounts contract.
