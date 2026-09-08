# Security model

Hub manages local app processes and paths, never Google credentials. Official app/OAuth does login. Never read/copy/export tokens, cookies, passwords, auth databases, Credential Manager secrets, or browser session data. Never patch binaries or quotas, proxy authentication, or impersonate Google identities.

Profiles are not security boundaries against the same Windows user. Synthetic home changes path resolution only. Credential Manager, DPAPI user context, registry protocol registration, installed binaries and network identity remain shared unless independently demonstrated otherwise. Manual A/B verification found that concurrently running profiles on Desktop 2.12.2 resolve to the Google account signed in later, so this version does not support simultaneous different Google accounts under one Windows user. Process/home/user-data probes do not evaluate authentication or change that finding.

Use immutable UUIDs for directory names. Canonicalize managed paths; reject traversal and reparse components before writes. Workspace is external metadata, never a cleanup target. Remove only unregisters. Atomic writes and backups protect metadata. No profile export/import in MVP.

Stop validates live process ownership and birth timestamp. Never kill by name or trust persisted PID. Reject ambiguous ownership. Probe cleans only its random owned temporary root after stopping owned children; retain data if cleanup safety checks fail.

Hub logs only allowlisted operation metadata. Vendor stdout/stderr is drained and discarded; vendor logs stay within its own profile data and are never copied into Hub logs. Child command lines may contain CSRF/bridge tokens; inspect only in memory where required and never persist them. Disallow arbitrary arguments that could alter isolation or introduce credentials.
