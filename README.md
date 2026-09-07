# Antigravity Profile Hub — 实验版

Windows 原生 Antigravity Desktop 多实例管理器。管理任意数量的本地 Profile，让它们使用独立数据目录和进程主目录。Google 登录仍由官方 Antigravity 完成。

**兼容性范围：本机 Antigravity Desktop 2.12.2。** 已观察到独立进程树、状态目录和两个可见的首次运行窗口。**两个 Google 账号能否长期保持独立登录仍待人工验证**，尤其是系统 OAuth 回调归属。不能把实验结果当作全部登录功能验收通过。

## 运行便携包

1. 解压 `AGProfileHub-win-x64.zip` 到普通本地目录，保留 `hub` 和 `cli` 的相对位置。
2. 双击 `Start Hub.cmd`，或直接运行 `hub/AGProfileHub.Gui.exe`。
3. 创建 `Pro A`、`Pro B`，分别启动并在官方应用中完成首次设置和登录。首次登录建议一次只开一个 Profile。
4. 登录后同时启动两者，按 `MANUAL_TEST_PLAN.md` 核对身份、重启保持和回调归属。

默认自包含 win-x64 包内含 .NET 运行时；无需单独安装 .NET。使用 Hub 前需要已经安装官方 Antigravity。Hub 不会导入原应用的登录信息。

## CLI

便携包目录中可以使用 `agprofile.cmd`，或直接调用 `cli/AGProfileHub.Cli.exe`。以实际 `help` 输出为准。Profile 参数使用创建/列表结果中的 UUID，不使用显示名称。

```powershell
.\agprofile.cmd doctor
.\agprofile.cmd create "Pro A"
.\agprofile.cmd create "Pro B"
.\agprofile.cmd list
.\agprofile.cmd launch <profile-id>
.\agprofile.cmd status
.\agprofile.cmd restart <profile-id>
.\agprofile.cmd stop <profile-id>
.\agprofile.cmd launch-all
.\agprofile.cmd stop-all
.\agprofile.cmd rename <profile-id> "新的显示名称"
.\agprofile.cmd show <profile-id>
.\agprofile.cmd workspace <profile-id> D:\Projects\Example
.\agprofile.cmd open-data <profile-id>
.\agprofile.cmd open-logs
.\agprofile.cmd remove <profile-id>
```

`--json` 输出 camelCase 机器可读 JSON；`--data-root <绝对路径>` 用于独立测试/便携数据目录。**remove 只移除登记，保留 Profile 数据**。请先停止该 Profile。不要把 Profile 数据目录上传到 Git 或当普通诊断日志分享。

GUI 的 `Workspace` 按钮只保存默认工作区元数据，`Data` 和顶部的 `Open logs` 按钮分别打开受管理的目录。当前版本没有声称把工作区参数转发进 Antigravity Desktop。

## 数据与限制

默认 Hub 数据位于 `%LOCALAPPDATA%/AntigravityProfileHub`：config 保存普通配置，profiles 保存各实例数据，runtime 保存运行线索，logs 保存经过限制的 Hub 操作日志。工作区在用户自己的项目目录中，和 Profile 分开。

- 独立 `--user-data-dir` 加进程级主目录覆盖是当前实测方案。Desktop 2 还使用主目录中的 `.gemini`，旧 IDE 的参数不能直接照搬。
- 这是路径隔离，不是安全沙箱。仍是同一 Windows 用户；Credential Manager、DPAPI、系统 URL 协议和应用安装本体可能共享。
- 独立主目录会改变 Git、SSH、插件和其他子工具寻找配置的位置。Hub 不自动复制密钥、Token 或浏览器数据。
- 当前工作区关联只是元数据。没有验证 Desktop 2 接受工作区启动参数；不能据此声称启动时已切换官方应用内项目。
- 应用更新后重新执行 doctor/probe；未知能力保持 unknown。完整账号验证步骤见 `MANUAL_TEST_PLAN.md`。
- 独立 Antigravity CLI/headless、旧版 IDE、Git Worktree 编排和账号配额管理不属于本版已验证范围。

## 从源码构建

需要 PowerShell 7、.NET 10 SDK（global.json 起点 10.0.400）和 Windows x64。本次机器使用仓库本地 `.tools/dotnet`，不更改系统 PATH。

```powershell
pwsh -NoProfile -File scripts/Build.ps1 -Test
pwsh -NoProfile -File scripts/Test-Integration.ps1
pwsh -NoProfile -File scripts/Publish.ps1
```

第一条构建解决方案并运行断言测试程序。第二条会启动真实的无登录测试实例，观察 CLI 生命周期，并把证据保存在被 Git 忽略的 artifacts/integration 随机目录。第三条先构建/测试，再产生带运行时的便携包和 SHA-256 文件；`-FrameworkDependent` 可输出依赖本机运行时的版本。

## 独立能力探针

```powershell
python scripts/Inspect-InstalledApp.py
pwsh -NoProfile -File scripts/Probe-Antigravity.ps1
pwsh -NoProfile -File scripts/Probe-Antigravity.ps1 -Visible -ObserveSeconds 35
```

静态检查只读取已安装应用代码，并输出证据布尔值与源码摘要。动态探针支持 `-Executable <路径>`，创建两个随机临时目录，检查进程和目录元数据，然后停止自己启动的进程、清理经过路径检查的测试目录。它不登录、不读取凭据。Antigravity 自身仍可能检查更新/协议注册。机器报告在 `artifacts/probe/capability-report.json`。

## 工程文档

- `PROJECT_STATE.md`：当前阶段、构建/测试状态、下一步。
- `CAPABILITY_REPORT.md`：实测事实、未知能力、官方资料。
- `ARCHITECTURE.md`、`DECISIONS.md`：技术分层和关键决策。
- `IMPLEMENTATION_PLAN.md`、`ASTRA_HANDOFF.md`：阶段顺序和模型交接。
- `SECURITY.md`、`MANUAL_TEST_PLAN.md`、`VERIFICATION_MATRIX.md`：安全边界与验收。
- `FINAL_REPORT.md`：交付结果、实际验证和延期功能。
