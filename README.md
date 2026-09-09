# Antigravity Profile Hub 0.2.1 — 实验版

Windows 原生 Antigravity Desktop 多 Profile 管理器。每个 Profile 使用独立的进程主目录、Electron user-data 和 Sandboxie 隔离盒；Google 登录仍完全由官方 Antigravity 完成。

本机验收范围为 **Antigravity Desktop 2.12.2 + Sandboxie Plus 1.18.2 / core 5.73.2**。在这个组合上，两个官方 Desktop 窗口已实测可并发登录不同 Google 账号，并在重启其中一个实例后分别保持。直接目录隔离仍只能串行使用；并发账号必须为每个 Profile 准备独占隔离盒。

## 依赖与首次设置

- 已安装官方 Antigravity Desktop 2.12.2。
- 已安装 Sandboxie Plus。Hub 不捆绑或静默安装它。
- 新建 Sandboxie 盒、配置 `OpenFilePath` 需要一次管理员权限；之后的日常启动不需要提权。

日常使用优先运行 `AGProfileHub-Setup-win-x64-<version>.exe`；也可以解压 `AGProfileHub-win-x64.zip`，保留 `hub`、`cli`、`scripts` 的相对位置并双击 `Start Hub.cmd`。安装器按当前用户安装到 `%LOCALAPPDATA%\Programs\AntigravityProfileHub`，卸载不会删除 `%LOCALAPPDATA%\AntigravityProfileHub` 中的 Profile 数据或 Sandboxie box。推荐流程：

1. 创建 Profile，在 GUI 点击 `Prepare isolation`，或运行 `agprofile iso-prepare <profile-id>`。
2. 如果盒子尚未创建，按错误提示在管理员 PowerShell 中运行包内 `scripts/New-IsolationBox.ps1`，随后用 `iso-prepare <id> --fresh` 确认该既有盒从未登录过并完成 keyring 声明。
3. 首次配置直写前保存工作并停止所有 Sandboxie 盒。运行 `agprofile iso-writethrough <id>`，再从管理员 PowerShell 执行它输出的带 `-ConfirmAllBoxesStopped` 命令。
4. 用 `agprofile iso-status` 或 GUI 的 Isolation 列确认 `boxExists=true`、`keyringClaimed=true`、`writeThroughReady=true`。
5. 分别启动 A/B，在官方窗口完成登录；随后可使用 `launch-all` 并发启动。

`--fresh` 会在该盒内写入一个明确无效的同名占位凭据，只能用于从未登录过的新盒。Core 会拒绝在宿主环境执行此写入，也不会读取、复制或输出真实 token。

## CLI

```powershell
.\agprofile.cmd doctor
.\agprofile.cmd create "Pro A"
.\agprofile.cmd list
.\agprofile.cmd iso-prepare <profile-id>
.\agprofile.cmd iso-status
.\agprofile.cmd iso-writethrough <profile-id>
.\agprofile.cmd launch <profile-id>
.\agprofile.cmd launch-all
.\agprofile.cmd status [profile-id]
.\agprofile.cmd restart <profile-id>
.\agprofile.cmd stop <profile-id>
.\agprofile.cmd stop-all
.\agprofile.cmd rename <profile-id> "新的显示名称"
.\agprofile.cmd workspace <profile-id> D:\Projects\Example
.\agprofile.cmd shortcut <profile-id>
.\agprofile.cmd open-data <profile-id>
.\agprofile.cmd open-logs
.\agprofile.cmd remove <profile-id>
```

`--json` 输出 camelCase JSON；`--data-root <绝对路径>` 可指定独立 Hub 数据根。Profile 参数使用 UUID，不使用显示名称。`remove` 只注销登记并保留 Profile 与沙箱数据；请先停止实例。隔离 Profile 的普通 `launch`、`stop`、`restart`、快捷方式、GUI 和批处理都会自动走同一 Core 隔离路由；未准备的 Profile 继续使用旧串行路径。

`workspace` 目前保存默认工作区元数据，并把路径纳入直写检查；尚未发现 Desktop 2.12.2 支持启动时转发 workspace 的正式参数。不能把“已关联”理解为官方应用已自动打开该项目。

## 数据与安全边界

默认数据根为 `%LOCALAPPDATA%/AntigravityProfileHub`：

- `config`：Profile 与普通设置；
- `profiles/<uuid>`：独立 home、roaming、local、temp、user-data；
- `isolation/state.json`：Profile 到盒子的原子、加锁映射，不包含 token；
- `runtime`、`logs`：进程线索和白名单操作日志；
- `global-launch-state`：所有 Hub 数据根共享的启动协调状态。

隔离盒解决的是同一 Windows 用户下的应用状态与认证冲突，不是不同 Windows 用户之间的安全边界。Hub 不保存 Google 密码、不导出 OAuth token、不复制 Cookie、不代理认证、不修改服务端认证，也不清理原版 Antigravity 数据。盒内停止是按专属盒终止；操作前请保存工作。

应用或 Sandboxie 更新后必须重新运行 `doctor` 并复验。独立 Antigravity CLI/headless 身份、Tray、Git Worktree/Agent Safe Mode 和配额管理仍不在当前交付范围。

## 构建与验证

需要 PowerShell 7、.NET 10 SDK 和 Windows x64：

```powershell
pwsh -NoProfile -File scripts/Build.ps1 -Test
pwsh -NoProfile -File scripts/Test-Integration.ps1
pwsh -NoProfile -File scripts/Publish.ps1
pwsh -NoProfile -File scripts/Publish.ps1 -Installer
pwsh -NoProfile -File scripts/Test-Package.ps1 -PackageRoot <发布包中的AGProfileHub目录>
```

`-Installer` 需要 Inno Setup 6，并从同一次自包含发布生成按用户安装的 Setup EXE。推送与拉取请求会运行 GitHub Actions 构建测试；推送与版本一致的 `v*` 标签会生成便携 ZIP、安装器、校验文件和 GitHub Release。公开分发前仍建议为 EXE 添加可信代码签名；未签名自用包可能触发 SmartScreen 提示。

真实账号登录永不自动化。`KEYRING_ISOLATION_REPORT.md` 记录根因和通过证据，`MANUAL_TEST_PLAN.md` 记录仍需人工确认的登出与 workspace 直写项目，`PROJECT_STATE.md` 是当前恢复入口。
