# Combined compatibility updates

The compatibility bundle updates Accounts, Chinese localization and their reviewed program descriptions together. Official Antigravity updates take priority. A future language-service program that has not been reviewed is not patched; independently saved authorizations remain available for a later compatible bundle. Native sign-in still requires explicit identity confirmation.

## Installation and normal use

The standalone Accounts ZIP remains available. Existing combined installations use `AntigravityCompatibility-<version>-win-x64.zip`, which includes the Accounts distribution, the localization engine and its production dependencies. It contains no official program, account authorization or conversation files.

Run the bundled `compatibility/Install-Compatibility.ps1` with explicit paths for `BundleRoot`, `InstallRoot`, `StateRoot`, `LocalizationStateRoot`, `AccountsStateRoot`, `ToolkitRoot` and `NodePath`. `StateRoot` is the new compatibility-manager directory; the two existing component state directories retain their original locations. The registered independent Node runtime is reused. The installer replaces the existing shortcut and Startup targets while retaining their old originals; it asks the old watcher to exit without uninstalling the previous toolkit.

The stable bootstrap selects an immutable tool-version directory. The watcher checks this repository's preview releases on startup and when the installed program changes, then no more frequently than every six hours for an unchanged program. Downloads are checked against the release tag commit, bundle manifest and ZIP checksum before preparation. The manager does not upload local logs or account data. Network errors do not prevent normal startup.

Preparing a bundle does not modify the application. Both layers are constructed in a separate directory. Application, updater and account-component processes must exit before installation. Installation records describe each file's before/after contents so a later invocation can recover an interrupted replacement. Recovery refuses unknown file contents or a changed host; it never writes an older Antigravity program over a newer update.

## Component status and identity

Installation status and runtime status are separate. A completed installation remains `runtimeState: pending` until the actual local Hub view is checked. Accounts checks the host window registry, main WebContents/frame/session/origin, native frontend bindings and model state before exposing switching. A failed view check disables switching with a bounded error code and retains the selected identity. Existing switching confirmation and send protection still apply.

If Chinese localization is unsupported but Accounts can be prepared, Accounts remains available in the original interface language. If Accounts is unsupported, the manager leaves the complete current native program intact and retains independent authorizations. It does not silently change to native sign-in. Incomplete files and mixed program versions remain errors rather than native-login fallback cases.

## Compatibility and recovery limits

Exact reviewed JavaScript is accepted first. The related-function fallback accepts only changes outside the reviewed functions, dependencies and top-level execution structure. Authentication, IPC, local/remote isolation, language-service arguments and unknown side effects remain checked. Native diagnostics, project refresh, OAuth configuration and activity decoding share one reviewed version description, including the identities of all adapted programs.

The manager retains the previous tool engine for a failed-engine startup. Program rollback is limited to the same host transaction, and does not move the account store. Directly running the official EXE or another unregistered entry can bypass the guarded launcher. SSH independent-account switching, arbitrary future native protocols and real vendor-installer behavior are not implied by file, module or synthetic update checks. Refer to `ACCOUNTS_VALIDATION.md` for the actual checks performed for each release.
