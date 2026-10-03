# Antigravity Profile Hub Next

在 Windows 上管理多个 Antigravity Profile。每个 Profile 使用独立的 home、user-data 和文件令牌存储；用户在官方 Antigravity / Google 窗口手动登录，已登录的实例可以并行运行。

当前版本为 **0.5.0-next.3 实验版**，交付 .NET 10 / WPF GUI 与 `agprofile-next` CLI，包含锁定交付、异步协议操作、登录与停止竞态及保守恢复整改。采用相同生产实现的本地整改便携包（版本号 next.2）已验证 A/B 不同账号并行使用、普通重启后登录保留、日常 Chrome 真实回调及桥接撤销恢复。**用户于 2026-10-03 选择本轮跳过 SSH 实际连接、不再继续；强停后的登录保留及文件编辑入口尚未明确确认，`manualAcceptance=false` 保持不变。** 发布门槛与验收范围见 [验证记录](docs/VALIDATION.md)。

## 安装与首次使用

从 [v0.5.0-next.3 下载页](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.5.0-next.3) 下载 Windows x64 安装器或便携 ZIP。发行包包含 .NET 运行时，无需另装 SDK 或 PowerShell 7；桌面适配器和桥接使用 Windows 内置 PowerShell。

发布流程以锁定依赖重新构建 next.3，并在产物清单中绑定发布源码、依赖和文件摘要。本机既有验收结果来自整改便携包，不代表 next.3 已重新完成本机人工验收；[next.2 历史发行](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.5.0-next.2) 保留。

- 安装器默认位置：`%LOCALAPPDATA%/Programs/AntigravityProfileHubNext`。
- 便携版：解压完整 `AGProfileHubNext` 目录，双击 `Start Next.cmd`。保留其中的 `hub` 与 `cli` 目录。
- Profile 数据位置：`%LOCALAPPDATA%/AntigravityProfileHubNext`。更新程序时保留此目录。
- CLI 入口：包根的 `agprofile-next.cmd`；安装版可直接运行 `cli/agprofile-next.exe`。

先安装官方 Antigravity，再按以下顺序操作：

1. 打开 Next，点击“应用路径”，选择官方 `Antigravity.exe`。
2. 点击“探测兼容性”，等待无登录临时窗口完成检查；不要在探针窗口登录。
3. 创建 Profile，填写便于识别的账号标签，并选择 Git / SSH 桥接范围。
4. 点击“注册回调”，选中 Profile 并点击“启动”。登录前点击“准备登录”，然后在该 Profile 的官方窗口完成登录。
5. 先完成或取消当前 Profile 的授权等待，再准备下一个 Profile 的登录。已登录的窗口可以保持运行。

若官方窗口已登录，而 Next 仍保留等待状态，可选中对应 Profile 点击“取消等待”，再继续下一次登录。这只清除管理器的等待槽，不能据此认定该次回调链已经通过；本轮成功验收同时确认了真实回调接收、交接完成和目标窗口登录。

Next 仅保存 Workspace 路径；请在对应官方窗口手动打开项目。当前没有验证通过的自动 Workspace 转发接口。账号标签也由用户填写，管理器不读取登录账号或自动执行 Google 登录。

## CLI 常用操作

在解压后的包目录打开 PowerShell，运行：

```powershell
./agprofile-next.cmd configure-executable "$env:LOCALAPPDATA/Programs/antigravity/Antigravity.exe"
./agprofile-next.cmd probe
./agprofile-next.cmd compatibility --json
./agprofile-next.cmd new 工作A
./agprofile-next.cmd open 工作A
./agprofile-next.cmd configure-deeplink register
./agprofile-next.cmd begin-login 工作A
# 在工作A的官方窗口手动完成登录。
./agprofile-next.cmd ls --json
./agprofile-next.cmd cancel-login 工作A
```

| 操作 | 命令 |
| --- | --- |
| 查看状态 | `ls` 或 `status <profile>` |
| 启动、普通关闭、重启 | `open <profile>`、`close <profile>`、`restart <profile>` |
| 显式强制停止 | `close <profile> --force` |
| 修改账号标签 | `rename <profile> <name>` |
| 保存已有 Workspace 目录 | `workspace <profile> <path>` |
| 查看或调整桥接 | `bridge <profile>`、`bridge <profile> on/off` |
| 创建快捷方式 | `shortcut <profile> [directory]` |
| 打开数据或日志目录 | `open-data [profile]`、`open-logs` |
| 查看或还原回调注册 | `configure-deeplink status/restore` |
| 移除已停止的 Profile 注册 | `remove <profile>`，保留其磁盘数据 |
| 查看完整命令 | `help` |

`<profile>` 可使用完整名称、UUID 或唯一前缀。`--data-root` 可指定绝对数据目录，`--json` 便于脚本处理。普通关闭失败时不会自动强停；请先保存工作，再自行选择强制操作。

## 兼容性与回调

每台机器首次使用和官方应用升级后，都要重新执行 `probe`。证明绑定应用版本、可执行文件及运行组件的 SHA-256，以及探测策略修订号。通过探测允许实验启动；每次冷启动仍检查独立文件令牌存储和实际进程归属。指纹改变后旧证明失效。

Next 为 `antigravity://` 注册当前用户的回调处理器，并保存此前命令。初次注册允许空处理器或精确匹配已配置厂商的处理器；其他处理器须先由其所属程序还原。还原前重新检查归属，第三方接管后安全跳过；真实恢复失败保留记录并阻止卸载。注册后不要移动或删除对应程序目录，应先从新位置重新注册。授权等待槽一次只允许一个 Profile，过期或归属不明确的回调会被拒绝。交接失败保留等待槽；进程身份无法确认时不会猜测 PID 并终止进程。

人工验收与自动探测分开记录。当前未设置人工通过标记；后续完整执行 [人工验收计划](NEXT_MANUAL_TEST_PLAN.md) 并核对所有适用项目后，才可使用 `compatibility accept --manual-passed`。自动测试、安装和迁移不会替用户设置此标记。

## 桥接、配置继承与旧版迁移

新 Profile 默认桥接宿主 `.gitconfig` 和 `.ssh`；创建向导可关闭，CLI 可用 `bridge <profile> off` 撤销。Git 使用受管 include，SSH 使用整个目录的链接，所以相关配置及密钥可以供官方工具使用。撤销只删除本工具拥有的 include / 链接，保留宿主目标；被用户替换的项目会保留并报告冲突。`.gemini` 不共享。

`inherit-config <profile> <源User目录>` 默认只预览，增加 `--apply` 后写入有限的安全允许项。支持 JSONC 注释和尾逗号；已有冲突值与注释保留，写入前备份。任意扩展设置、keybindings 参数、snippets 正文、MCP 和插件不会自动复制。

旧版迁移先预览，再应用：

```powershell
./agprofile-next.cmd migrate "$env:LOCALAPPDATA/AntigravityProfileHub" --dry-run
./agprofile-next.cmd migrate "$env:LOCALAPPDATA/AntigravityProfileHub" --apply
```

迁移保留 Profile UUID、标签和 Workspace 元数据，并尝试继承允许的配置。源注册文件改变后必须重新预览。源数据保持原样；登录凭据、Cookie、认证数据库、运行记录和兼容证明不复制，迁移后需要在官方窗口重新登录。

## 更新、回滚与卸载

更新时保存工作，关闭 Next 管理器，替换或安装新程序，保留数据目录；重新注册回调，并检查兼容性是否要求重探测。

`rollback` 请求普通关闭 Next 的 Profile，并还原仍由 Next 拥有的协议。窗口无法关闭时操作会失败并保留协议；保存工作后可手动关闭，或明确使用 `rollback --force`。若需要独立恢复协议，可使用 `configure-deeplink restore`。安装器卸载同样先尝试普通回滚，失败就中止，Profile 数据保留。旧程序对 Antigravity 2.19.1 的兼容性没有保证。

## 开发与交付

开发要求 Windows x64、PowerShell 7 和精确的 .NET SDK 10.0.400；`global.json` 关闭版本滚动。构建脚本优先使用 `.tools/dotnet`，也支持 `AGPH_DOTNET` 或环境中的同版本 SDK。安装器编译要求 Inno Setup 6.7.3。入口脚本采用 UTF-8 BOM，Windows PowerShell 5.1 会明确提示版本要求。

```powershell
git clone https://github.com/Aliarrrrr/antigravity-profile-hub.git
cd antigravity-profile-hub
./scripts/Build.ps1 -Test -LockedRestore
./scripts/Build.ps1 -Test -LockedRestore -IncludeSystemTests
./scripts/Test-NextBuildContract.ps1
./hub.ps1 run
./hub.ps1 cli -CliArgs @('ls','--json')
./scripts/Publish-Next.ps1 -Installer
```

产物位于 `artifacts/next/0.5.0-next.3/<时间戳>`，包含自包含 GUI / CLI、逐文件校验清单、ZIP、安装器及 SHA-256。Core、CLI、GUI 各保留普通锁及 win-x64 自包含发布锁；发布缺少锁或应用依赖集合、版本、NuGet 内容哈希不一致就失败。清单记录提交、SDK、六份锁文件摘要及应用、平台和运行时依赖；SDK 运行时组件以产物文件 SHA-256 校验。

对完整便携目录运行 `Test-NextPackage.ps1 -PackageRoot <目录>` 可检查清单、CLI、GUI 和快捷方式；在没有真实 Profile 的干净 Windows 环境运行 `Test-NextInstaller.ps1 -Installer <文件>` 验证安装 / 卸载。PR CI 执行系统测试、完整打包与安装器冒烟；发布验证 job 只读，具备写权限的 job 仅上传已验证产物。安装器数字版本来自已发布 GUI 的 FileVersion，完整预发布版本用于显示及文件名。

| 保留的开发入口 | 用途 |
| --- | --- |
| `hub.ps1`、`scripts/Common.ps1`、`scripts/Build.ps1` | 运行、环境准备、锁定还原、构建和测试 |
| `scripts/Publish-Next.ps1`、`installer/AGProfileHubNext.iss` | 当前 Next 包与安装器 |
| `scripts/Test-NextPackage.ps1`、`scripts/Test-NextInstaller.ps1` | 包和安装器烟雾验证 |
| `scripts/Probe-NextContract.py`、`scripts/Probe-NextDesktop.ps1` | 无登录的厂商运行契约探测 |
| `tests/AGProfileHub.Tests` | 按 Next、共享生产组件、Legacy 和 runner 输出回归结果 |

默认测试跳过桌面适配器系统集成用例，`-IncludeSystemTests` 显式启用；测试器也支持 `--filter <名称子串>`。真实 junction 边界测试仍在独立临时目录默认运行。历史夹具中也使用共享生产的存储和路径组件，不能将全部 Legacy 用例视作无效覆盖。

旧启动实现仅编译进测试夹具，Next 生产程序集使用 `NextHubService`。`src/AGProfileHub.RustGui` 是另一任务开发中的实验界面，相关源码和开发入口保留；本次发行包为 .NET WPF Next，不包含 Rust GUI。

本版专注 Windows Profile 生命周期与应用状态分离。它不是 OS 安全沙箱；同一 Windows 用户下的进程仍可能访问其他可读文件。安全边界见 [SECURITY.md](SECURITY.md)，实现说明见 [ARCHITECTURE.md](ARCHITECTURE.md)。
