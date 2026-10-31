# Antigravity Accounts — independent developer preview

Antigravity Accounts is a Windows x64 preview that groups separately authorized Google accounts inside Antigravity's native model selector. It uses the host's styling and keeps native model choices and View Usage. There is no separate account window. The official language server sends model requests directly to Google.

## Current implementation

Version 0.4.0-preview.5 is locally installed on Antigravity 2.28.1. It currently requires a version-specific host adaptation. The installer changes app.asar to add startup, HostBridge and session-preload hooks. It replaces the installed language-server program with a copy containing a limited diagnostic-route change; independent-account mode runs a second copy that also changes one project-cache call. Original program files are retained for restoration. Native menu integration uses private React/Redux interfaces, and unknown program contents are rejected.

Each account is added through Google's web OAuth flow. A separate component stores its credentials using Windows DPAPI CurrentUser and supplies the selected identity through HostBridge. The preview uses the matching installed desktop program's public OAuth client configuration and does not import its saved login database. Whether this client configuration may be used for third-party Marketplace distribution requires confirmation from Google.

## What has been checked

Two metadata-only account changes on Antigravity 2.28.1 completed identity, project and usage-metadata confirmation, then restored the original account, project and model. The App, language server and account component did not restart. The agent sent zero model requests. Missing-model handling and protection against automatic default-model overrides were checked separately with native frontend code and synthetic data.

Actual generation, quota charging, expiry renewal and generation continuing across accounts have not been verified. The compressed account component is 37,627,797 bytes; the distribution does not include official program copies or personal account data.

## Marketplace and compatibility status

This preview is not Marketplace-listed and is not yet a standard plugin-only implementation. Source and release artifacts for this preview have not been publicly published. Future App versions require a separate compatibility review.

The goal is normal Marketplace installation using supported interfaces for native model-menu account groups, selection of the identity used for native model and quota requests, safe changes while work is idle, and confirmed identity refresh with model-selection preservation. The plugin and sidecar interfaces reviewed in Antigravity 2.28.1 did not expose these native account-group and quota-identity registration capabilities; existing plugin multi-account authentication was used by external MCP providers.

This document supports a request to Google for a supported implementation path and Marketplace review. It does not represent approval from or affiliation with Google.

Publisher: [Aliarrrrr, independent developer](https://github.com/Aliarrrrr).
