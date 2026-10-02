# Astra → GPT-5.6 Sol handoff

Read PROJECT_STATE.md, this file, ARCHITECTURE.md, IMPLEMENTATION_PLAN.md, DECISIONS.md, CAPABILITY_REPORT.md, SECURITY.md, then git status, build and tests. Work from the established evidence rather than starting over.

Verified local facts: Windows 11 x64; PowerShell 7.6.5; Git 2.55; no preinstalled SDK. Antigravity 2.12.2 desktop is in LocalApplicationData/Programs/antigravity. ASAR paths code uses os.homedir()/.gemini independently of Electron userData. Independent USERPROFILE/HOME/appdata/temp environment plus --user-data-dir created TWO persistent roots and separate backend/state directories in one Windows account. Stop A did not stop B. Probe cleaned temporary resources. No original account data or credentials were opened.

Follow-up observed two visible first-run windows via computer-use; no onboarding/privacy/login interaction. Unverified: authenticated usable dual windows, real two-account persistence, callback routing, keychain/DPAPI interactions, settings behavior, workspace arguments and standalone CLI. NEVER label these supported based on our directory/process probe. Independent process home is experimental; not an OS sandbox. Packaged main.js registers the same antigravity URL protocol without profile args; callback routing is a concrete risk.

Implement Core and CLI first with safe config/locks/process ownership and tests; build after each phase. Then WPF GUI, CLI shortcuts and runnable portable package. Keep GUI thin. Profile remove unregisters only. Workspace is metadata unless separately proved launchable. Read installed code only for capability, never credential stores. Do not print or capture vendor logs or child command lines because those contain runtime secrets.

Driver must centralize all launch arguments/environment. Persistent Hub data uses actual LocalApplicationData; only CHILD launch receives synthetic homes. Reject untrusted UUID/path/reparse input. Reconcile existing processes using exact parsed user-data flag, executable path, PID/start time. Avoid broad tree termination when ownership is uncertain. Runtime is not authoritative. Log allowlists only.

Phase order/acceptance/rollback in IMPLEMENTATION_PLAN.md. Human OAuth is a release limitation, not an excuse to fabricate completion. Build a usable experimental manager with the limitation visible; leave manual checklist pending. Update all state and final report with exact implemented versus deferred capabilities. Do not rewrite architecture unless you have new evidence; record evidence and ADR changes first.
