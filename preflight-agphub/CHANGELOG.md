# Changelog

## 0.2.0 experimental — 2026-09-11
- 集中产品版本、默认 .NET 10 目标框架和编译选项；GUI 保留 Windows/WPF 目标，Core 保留 WMI 所需的 System.Management 10.0.0。
- 增加兼容 PowerShell 5.1/7 的统一入口、稳定版 SDK 发现、项目内缓存与环境恢复，以及锁定还原和控制台测试入口；系统测试须显式启用。
- 增加先构建/测试、版本化独立目录、schema 2 发布清单及 SHA256 校验；正式发布检查干净工作区、HEAD 版本标签和变更记录，不自动创建标签。
- 增加 Windows CI、版本管理与配置升级/回滚说明；数据、配置和 Token 不进入发布包。
- 本条目不代表测试已通过。Sandboxie 双账号能力仍属实验性质，未在本条目中宣称完成人工验证。

## 0.1.2 experimental — 2026-09-08
- Moved launch coordination to a fixed per-Windows-user state root shared by every `--data-root`.
- Added an atomic pre-create launch barrier that remains on delayed discovery, post-create cancellation, indeterminate process identity and runtime-state write failure; invalid or future barrier data fails closed.
- Added direct process-liveness reconciliation and a distinct confirmed-pre-create failure path, so only a process proven not to have started can clear its reservation immediately.
- Expanded the Core suite to 26/26 and real-process integration to 12/12, including simultaneous independent CLI launches within one data root and across two data roots; each race created exactly one root.
- GUI refreshes state after failed operations and disables `Launch all` when multiple Profiles are registered. Doctor reports retained or invalid global launch barriers.
- Kept Google account isolation `Incompatible` for Desktop 2.12.2; the single-root gate is a safety mitigation, not evidence of multi-account support.

## 0.1.1 experimental — 2026-09-08
- Added CLI/GUI desktop shortcuts that bind immutable UUIDs and the current data root; existing links are not overwritten.
- Added GUI executable settings, isolated `--data-root` startup, operation locking, readable status details, and explicit incomplete-stop errors.
- Added regression checks for unverified versions, changed executable attestations, reused PIDs, Windows shortcut contents, and the single-root launch gate; 19/19 checks pass.
- Re-ran CLI concurrency (12/12) and real-process integration (6/6); exercised GUI create, rename, launch and graceful stop against an isolated test root.
- Added reproducible packaged-shortcut lifecycle smoke script. Manual Google A/B acceptance found that concurrent profiles resolve to the later-signed-in account, so this version blocks a second Antigravity root and does not claim separate-account support.
- Packaged .lnk launch/rename/duplicate/stop smoke plus the launch-all gate passed 6/6; verified self-contained GUI startup and executable picker.

## 0.1.0 development — 2026-09-07
- Established native Windows project plan and security boundaries.
- Identified Desktop 2.12.2 home-based .gemini storage; rejected user-data-only assumption.
- Observed two disposable process trees with separate state paths and independent stop.
- Added reproducible metadata-only probe and manual authentication test plan.
- Added UUID-backed profile storage with atomic JSON writes, schema checks, path safety, profile locks, and structured Hub logs.
- Added Antigravity Desktop driver and process supervisor with verified PID/start-time/executable/user-data ownership, duplicate-launch handling, graceful/forced stop, and restart isolation.
- Added CLI lifecycle commands and WPF profile hub UI without credential inputs.
- Added 12 Core/process assertions, CLI concurrency checks, and real-process integration checks.
- Published and smoke-tested the self-contained win-x64 package under `artifacts/package/20260907-213404/`.
- Kept Google account persistence and OAuth callback routing explicitly `manual-verification-required`.
