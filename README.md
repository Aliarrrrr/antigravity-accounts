# 🎛️ Antigravity Profile Hub Next

> Windows 上的 Antigravity 多账号管理器。为工作、个人和测试账号建立各自的 Profile，在官方窗口完成登录，让多个已登录实例一起运行。

本开发分支增加了共享组与选择性文件导入入口，见 [共享工作空间说明](docs/SHARED_WORKSPACES.md)。Hub 不管理账号凭据或自动获取邮箱。无感账号切换尚不可用；会话索引尚未导入，文件导入不代表原客户端可恢复。默认多开方式保留。

[![版本](https://img.shields.io/badge/version-0.5.0--next.3-orange)](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.5.0-next.3)
![Windows x64](https://img.shields.io/badge/Windows-x64-0078D4)
![.NET 10 / WPF](https://img.shields.io/badge/.NET_10-WPF-512BD4)
[![MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**[下载安装](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.5.0-next.3) · [使用指南](#使用指南) · [命令行](#命令行) · [验证记录](docs/VALIDATION.md)**

## ✨ 核心功能

| 功能 | 你可以做什么 |
| --- | --- |
| 多账号并行 | 按 Profile 分别启动和关闭官方窗口，多个已登录账号同时使用 |
| 独立应用状态 | 每个 Profile 使用自己的 home、用户数据目录和文件令牌存储，分别保留登录与应用状态 |
| 图形化管理 | 在 WPF 界面中创建 Profile、查看运行状态、准备登录、修改标签和创建快捷方式 |
| 官方窗口登录 | 在官方 Antigravity 和浏览器中手动授权，由管理器把回调交给正在等待的 Profile |
| 可选 Git / SSH 桥接 | 复用宿主 Git 配置和 SSH 目录，或撤销本工具创建的共享项 |
| 配置继承预览 | 先查看可以继承的有限设置，再决定是否写入；保留已有冲突值并备份 |
| CLI 与诊断 | 用命令管理 Profile，输出 JSON 状态，打开数据和日志目录 |

当前源码为 **0.5.0-next.4 实验版**，下方下载入口仍指向已发布的 next.3。历史本机整改包（当时版本号为 next.2）已验证不同账号并行使用、普通重启后的登录保留、真实 Chrome 授权回调和桥接撤销恢复；这些记录不能替代 next.4 产物的实际验收。完整人工验收仍未完成，保持 `manualAcceptance=false`；既有范围见 [验证记录](docs/VALIDATION.md)。

## 🚀 快速安装

先安装官方 Antigravity，再选择一种方式安装管理器。发行包自带 .NET 运行时，**无需另装 .NET SDK、Python 或 PowerShell 7**。

| 方式 | 下载 | 使用方法 |
| --- | --- | --- |
| 安装器，适合日常使用 | [下载 Windows x64 安装器](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/download/v0.5.0-next.3/AGProfileHubNext-Setup-win-x64-0.5.0-next.3.exe) | 运行安装器，使用安装后的管理器入口 |
| 便携版 | [下载 Windows x64 ZIP](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/download/v0.5.0-next.3/AGProfileHubNext-0.5.0-next.3-win-x64.zip) | 完整解压后，双击 `AGProfileHubNext/Start Next.cmd` |

两份文件各附一份 SHA-256 校验文件，可在 [发行页](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.5.0-next.3) 下载。仓库当前为私有，下载时需登录有访问权限的 GitHub 账号。

便携版需保留完整目录，包括 `hub`、`cli` 和启动脚本。注册回调后，系统会记住程序所在位置；更换位置时，从新位置重新注册回调。

## 使用指南

### 1. 配置应用并探测兼容性

打开管理器，点击 **“应用路径”**，输入已安装的官方 `Antigravity.exe` 的完整路径；然后点击 **“探测兼容性”**。

探测会启动一个无登录的临时窗口，检查独立应用状态、进程归属和协议恢复。等待完成即可，**不要在探针窗口登录**。每台机器首次使用，以及官方 Antigravity 升级后，都需要重新探测。

### 2. 创建第一个 Profile

点击 **“创建 Profile”**，填写便于识别的标签，例如“工作”或“个人”，再选择桥接 `on` 或 `off`。

标签由你填写，管理器不会读取登录账号。桥接 `on` 会共享宿主 `.gitconfig` 和整个 `.ssh` 目录；`off` 则不创建这些共享项。两者的具体含义见下文“桥接与配置继承”。

### 3. 启动并完成登录

1. 选中 Profile，点击 **“启动”**。
2. 点击 **“准备登录”**。管理器会注册或重申回调入口，并让这个 Profile 等待授权。
3. 在**这个 Profile 的官方窗口**中发起登录，在常用浏览器中完成授权，并允许浏览器打开 Antigravity。
4. 回到目标官方窗口，确认账号和工作界面正常。

授权应逐个完成：先完成或取消当前等待，再为下一个 Profile 准备登录。已登录的窗口可以保持运行。

如果官方窗口已经登录，但管理器仍显示等待，可点击 **“取消等待”**。它只清除管理器的等待槽，不会注销账号，也不能单独作为回调成功的证明。

### 4. 添加其他账号并打开项目

为其他账号创建新的 Profile，重复启动和登录步骤。需要查看状态时点击 **“刷新”**；以后从管理器或创建好的快捷方式启动对应 Profile。

请在各自的官方窗口里手动打开项目。管理器的 **“Workspace”** 按钮只保存项目路径，当前自动 Workspace 转发为 `unsupported`。

### 5. 日常关闭与重启

保存工作后，选中 Profile 并点击 **“关闭”**。重新启动即可继续使用该 Profile；本机整改包普通关闭、重启后的登录保留已验证。

关闭失败时检查诊断，必要时再选择 **“强制停止…”**。普通关闭不会自动升级为强停；强停可能丢失未保存工作。关闭管理器窗口本身不会等同于关闭官方窗口。

## 命令行

在完整便携包的根目录打开 PowerShell，运行 `agprofile-next.cmd`。安装版也可使用安装目录中的 `agprofile-next.cmd` 或 `cli/agprofile-next.exe`。

### 从创建到登录

```powershell
# 按实际安装位置修改官方应用路径。
./agprofile-next.cmd configure-executable "$env:LOCALAPPDATA/Programs/antigravity/Antigravity.exe"
./agprofile-next.cmd probe

./agprofile-next.cmd new 工作
# 新建默认关闭桥接。需要共享宿主 Git / SSH 时明确执行：
# ./agprofile-next.cmd bridge 工作 on
./agprofile-next.cmd open 工作
./agprofile-next.cmd configure-deeplink register
./agprofile-next.cmd begin-login 工作
# 接下来在“工作”的官方窗口及浏览器中手动完成授权。

./agprofile-next.cmd ls --json
```

### 常用操作

下列命令都接在 `./agprofile-next.cmd` 后面：

| 操作 | 命令示例 |
| --- | --- |
| 查看所有 Profile / 指定状态 | `ls --json` / `status 工作` |
| 启动 / 普通关闭 / 重启 | `open 工作` / `close 工作` / `restart 工作` |
| 显式强制停止 | `close 工作 --force` |
| 准备登录 / 取消等待 | `begin-login 工作` / `cancel-login 工作` |
| 修改标签 | `rename 工作 项目账号` |
| 保存项目路径 | `workspace 工作 "D:\Projects\demo"`，仍需在官方窗口手动打开 |
| 查看 / 开启 / 关闭桥接 | `bridge 工作` / `bridge 工作 on` / `bridge 工作 off` |
| 创建启动快捷方式 | `shortcut 工作` |
| 打开数据 / 日志目录 | `open-data 工作` / `open-logs` |
| 查看 / 注册 / 还原回调 | `configure-deeplink status` / `configure-deeplink register` / `configure-deeplink restore` |
| 移除已停止的 Profile 登记 | `remove 工作`，磁盘数据保留 |
| 查看版本 / 完整帮助 | `version` / `help` |

Profile 可用完整标签、UUID 或唯一前缀指定。全局参数 `--data-root <绝对目录>` 可以选择其他数据目录，`--json` 用于脚本读取结果。

## 🔗 桥接与配置继承

### Git / SSH 桥接

新 Profile 默认关闭桥接；需要共享时，在创建向导明确选择 `on`，或通过 **“桥接范围”** / `bridge <profile> on` 开启。CLI 新建直接保存关闭策略，实验 Rust GUI 也默认不勾选共享，不会先开启再撤销。

- `.gitconfig`：使用受管 include 读取宿主 Git 配置。
- `.ssh`：链接整个宿主 SSH 目录，目录中的配置和密钥可供官方工具使用。
- `.gemini`：不共享。

这是共同开启或关闭的桥接范围。撤销只删除本工具拥有的 include / 链接，宿主目标保留；遇到被用户替换的项目会保留并报告冲突。桥接提供配置和目录访问，远端连接与认证仍取决于你的 SSH 配置和仓库权限；本轮验证范围见 [验证记录](docs/VALIDATION.md)。

已有明确 `on` 的桥接记录继续有效，明确 `off` 保持关闭。没有桥接清单或清单缺少开启字段时按关闭处理；迁入的新 Profile 同样默认关闭。损坏或不受支持的清单拒绝处理，保留清单与现有文件以便恢复。清单缺失时无法证明旧项目归属，本工具不会自动删除未登记的文件或链接；默认关闭不等于清除未知旧链接，也不构成操作系统沙箱。

### 继承有限设置

GUI 中点击 **“配置继承预览”**，输入源 `User` 目录的完整路径；先查看结果，再决定是否应用。CLI 同样默认只预览：

```powershell
./agprofile-next.cmd inherit-config 工作 "<源 User 目录>"
# 确认预览后，增加 --apply 写入允许项。
./agprofile-next.cmd inherit-config 工作 "<源 User 目录>" --apply
```

仅继承有限的允许设置，已有冲突值和 JSONC 注释保留，写入前备份。主题、扩展、MCP、快捷键和 snippets 不会自动复制；登录凭据也不属于配置继承范围。

旧版管理器的 Profile 元数据可先用 `migrate <旧数据根> --dry-run` 预览，再用 `--apply` 迁移。源数据保留，登录凭据、Cookie、认证数据库和兼容证明不搬运；迁移后在官方窗口重新登录。

## 📁 数据、更新与卸载

| 内容 | 默认位置 |
| --- | --- |
| 安装版程序 | `%LOCALAPPDATA%/Programs/AntigravityProfileHubNext` |
| Profile 和管理器数据 | `%LOCALAPPDATA%/AntigravityProfileHubNext` |
| 管理器诊断日志 | 数据目录下的 `logs` |

**更新程序：**保存工作，关闭管理器，安装或替换新程序，保留数据目录。随后重新注册回调，在兼容性页确认是否需要重探测。

**回滚管理器：**保存所有 Next Profile 的工作，再运行 `./agprofile-next.cmd rollback`。它会请求普通关闭这些 Profile，并还原仍由 Next 拥有的回调。关闭失败时回滚中止并保留协议，可先手动处理窗口；仅需还原协议时使用 `configure-deeplink restore`。旧管理器对当前官方版本的兼容性需要另外确认。

**卸载安装版：**安装器先尝试普通回滚，失败就中止卸载；Profile 数据保留。CLI 的 `remove` 也仅移除登记。需要清理磁盘数据时，先确认目标目录和备份，其中可能包含登录状态、配置和工作记录。

默认卸载回滚默认数据目录。使用 `--data-root` 自定义数据目录时，可运行安装目录中的 `unins000.exe /HubDataRoot="D:\你的数据目录"`，使普通回滚处理该目录；回调恢复继续核验当前用户协议的所有权。参数须为一个非空的绝对盘符或 UNC 路径，支持空格和中文；相对路径、重复参数、嵌入引号或控制字符会在卸载开始前被拒绝。只处理选定数据目录中的 Profile，保留数据文件。

## 🔐 工作原理与安全边界

```text
Profile Hub Next（WPF / CLI）
├─ Profile 工作 → 官方窗口 A → 独立 home / user-data / 文件令牌存储
├─ Profile 个人 → 官方窗口 B → 独立 home / user-data / 文件令牌存储
└─ 浏览器授权回调 → 唯一等待槽 → 正在等待的 Profile
```

管理器为每个 Profile 建立独立目录和启动环境，使用官方应用运行组件的 SHA-256 与版本绑定兼容证明，每次冷启动核对实际进程归属和文件令牌存储。官方应用升级或组件变化后，旧证明失效。

协议注册保存此前处理器，还原时重新核对所有权；第三方接管后安全跳过，真实恢复失败保留记录并阻止卸载。回调失败保留等待槽，进程身份无法确认时拒绝猜测 PID 后终止进程。管理器的结构化回调诊断不记录完整 URI 或认证内容。

这里隔离的是应用状态。同一 Windows 用户下的进程仍按该用户的权限访问可读文件；开启 SSH 桥接会让工具使用宿主目录。请保管好 Profile 数据，不要提交登录数据到仓库或共享存储。

## 🛠️ 开发与文档

开发环境：**Windows x64、PowerShell 7、.NET SDK 10.0.400**。安装器构建使用 Inno Setup 6.7.3。SDK 版本固定且关闭自动滚动；脚本支持仓库 `.tools/dotnet` 或 `AGPH_DOTNET` 指定的同版本 SDK。

```powershell
git clone https://github.com/Aliarrrrr/antigravity-profile-hub.git
cd antigravity-profile-hub

./scripts/Build.ps1 -LockedRestore -Test
./scripts/Build.ps1 -LockedRestore -Test -IncludeSystemTests
./scripts/Test-NextBuildContract.ps1
./hub.ps1 run
./scripts/Publish-Next.ps1 -Installer
```

默认测试跳过桌面适配器集成用例，`-IncludeSystemTests` 显式启用。构建使用锁定依赖；产物位于 `artifacts/next/0.5.0-next.4/<时间戳>`，包含程序、逐文件清单、ZIP、安装器和校验文件。发布流水线在干净 Windows 环境通过构建、测试、便携包及安装/卸载验证后，上传已验证产物。

当前发行提供 WPF GUI 和 CLI。仓库中的 Rust GUI 为实验开发内容，不在本次发行包内。

| 文档 | 内容 |
| --- | --- |
| [更新记录](CHANGELOG.md) | 各版本变更 |
| [验证记录](docs/VALIDATION.md) | 自动与真实使用验证范围、未确认项目 |
| [人工验收计划](NEXT_MANUAL_TEST_PLAN.md) | 现场验收步骤 |
| [架构说明](ARCHITECTURE.md) | 组件与运行流程 |
| [安全边界](SECURITY.md) | 数据、进程和协议保护 |
| [next.3 发布验证](https://github.com/Aliarrrrr/antigravity-profile-hub/actions/runs/37118260812) | 干净 Windows 构建与发布证据 |

## 📄 许可与参考

本项目采用 [MIT License](LICENSE)。Antigravity 与 Google 的相关品牌及商标归各自所有者所有。

本文档的组织方式参考 [ParaGravity 中文 README](https://github.com/edison-land/paragravity/blob/main/README_zh.md)，功能、命令与验证范围按 Profile Hub Next 的当前实现编写。
