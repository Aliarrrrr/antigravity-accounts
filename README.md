# Antigravity Profile Hub 0.4.2 — 实验版

> **English (summary).** Antigravity Profile Hub is a native Windows manager that lets multiple official Antigravity Desktop windows run under one Windows user, each signed in to a different Google account. Per-profile isolation (own home, own file-based token store, fail-closed launch verification) needs no sandbox product and no administrator rights. Authentication always happens in official Antigravity/Google pages — the Hub never touches passwords, OAuth tokens, or cookies. Only **Windows x64 + Antigravity Desktop 2.12.2** are verified; full documentation is Chinese below.

Antigravity Profile Hub 是一个 Windows 原生的 Antigravity Desktop 多 Profile 管理器：让多个**官方** Antigravity Desktop 窗口在同一个 Windows 用户下各自保持**不同的 Google 账号**登录。

**0.4.0 起不再需要 Sandboxie 或任何沙盒软件，也全程无需管理员权限。** 每个 Profile 拥有独立的进程主目录和 Electron user-data，语言服务的登录令牌保存在各自的 Profile 目录里；Hub 在每次启动时验证这一点，验证不过的实例会被自动终止。

登录始终在官方 Antigravity / Google 页面内完成。Hub 不接收密码，不读取、复制或导出 OAuth token、Cookie 或 Credential Manager 中的任何凭据内容。

> [!WARNING]
> 当前版本只在 **Windows x64 + Antigravity Desktop 2.12.2** 上完成验收。Hub 会拒绝为未经验证的 Antigravity 版本猜测启动协议。升级 Antigravity 后，请重新运行 `doctor` 并重新进行人工验收。

## 工作原理（30 秒版）

1. **目录隔离**：每个 Profile 的 `USERPROFILE` 指向自己的托管目录，Antigravity 与语言服务的一切本地状态都落在 Profile 内。
2. **令牌分流**：注入一个环境变量后，语言服务主动改用**各自目录下的文件令牌存储**，而不是所有实例共享的那条 Windows 凭据（`gemini:antigravity`）。
3. **启动自证**：启动时 Hub 检查语言服务日志，确认实例确实切到了自己的文件存储——找不到证据就终止实例（`isolation_scope_unverified`），绝不让它共享你的登录。
4. **回调路由**：并发登录时 `antigravity://` 回调由 Hub 转给正在等待它的那个窗口（`configure-deeplink register` 一次性设置，可随时还原）。

详细机制与验收证据见 [KEYRING_ISOLATION_REPORT.md](KEYRING_ISOLATION_REPORT.md)。

## 当前能力与限制

| 能力 | 状态 |
|---|---|
| 两个不同 Google 账号的窗口**并发保持各自登录**（已登录后的并行运行） | ✅ 已人工验收（2.12.2，process-local 机制） |
| 并发**登录时**的 `antigravity://` 回调路由 | ⚠️ 机制在 0.4.1 修复（0.4.0 路由会丢弃全部回调），修复后的真实端到端并发登录验收**待完成**；验收通过前请勿依赖该场景 |
| Profile 独立启动 / 停止 / 重启 / 桌面快捷方式 | ✅ |
| 隔离作用域自动配置与自愈（`new` / `fix`，无提权） | ✅ |
| 按名字（显示名 / 唯一前缀 / UUID）操作 Profile | ✅ |
| GUI 与 CLI | ✅ |
| 官方 Antigravity / Google 登录 | ✅ 必须由用户在官方窗口手动完成 |
| 自动输入账号密码 / 授权码 | ❌ 不支持，也不会实现 |
| 启动时自动打开 Workspace | ❌ 暂不支持（Workspace 仅是元数据） |
| Profile 导入 / 导出 / 彻底清理 | ❌ 暂不支持 |
| 其他 Windows 用户、网络或系统级安全隔离 | ❌ 不提供 |

## 下载安装

### 前置条件

- Windows 10/11 x64
- 官方 Antigravity Desktop **2.12.2**

安装包与便携包都是自包含的，日常使用不需要安装 .NET。Hub 不捆绑、不下载、不静默安装 Antigravity Desktop。

### 安装

从 [v0.4.2 Release](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.4.2) 下载：

| 文件 | 适合 |
|---|---|
| `AGProfileHub-Setup-win-x64-0.4.2.exe` | 推荐日常使用（按当前用户安装） |
| `AGProfileHub-0.4.2-win-x64.zip` | 免安装 / 便携 |

校验（在下载目录）：

```powershell
$File = ".\AGProfileHub-Setup-win-x64-0.4.2.exe"
$Expected = (Get-Content "$File.sha256.json" -Raw | ConvertFrom-Json).hash
if ((Get-FileHash $File -Algorithm SHA256).Hash -ne $Expected) { throw "SHA-256 不匹配" }
"校验通过"
```

安装器为当前用户安装（默认 `%LOCALAPPDATA%\Programs\AntigravityProfileHub`），不修改 PATH。便携包解压到稳定位置（如 `D:\Apps\AGProfileHub`）后运行 `Start Hub.cmd`；不要只复制 `hub\AGProfileHub.Gui.exe`——快捷方式与回调路由需要包内的 `cli`。

当前构建未做代码签名，SmartScreen 可能提示"未知发布者"，请核对 SHA-256 后再运行。

## 首次运行检查

```powershell
$Hub = "$env:LOCALAPPDATA\Programs\AntigravityProfileHub\agprofile.cmd"   # 安装版
# 或便携版：$Hub = "D:\Apps\AGProfileHub\agprofile.cmd"
& $Hub setup
```

重点确认 `executableDiscovery` 为 `Verified`、版本为 2.12.2。如果找不到 Antigravity：

```powershell
& $Hub configure-executable "C:\Users\你\AppData\Local\Programs\antigravity\Antigravity.exe"
```

## 五分钟快速开始

```powershell
& $Hub new "工作A"        # 创建 Profile + 自动准备隔离（无提权、无沙盒）
& $Hub open  "工作A"      # 启动；窗口出现后在官方页面登录账号 #1

& $Hub new "工作B"
& $Hub open  "工作B"      # 登录账号 #2
```

- **冷启动 1–2 分钟属正常**（语言服务首次下载内置组件）。
- 两个窗口互不干扰，各自保持各自的登录；重启不丢账号。

日常三件事：

```powershell
& $Hub ls                # 列表
& $Hub open  "工作A"     # 启动
& $Hub close "工作A"     # 停止（先保存工作）
```

所有命令都接受**显示名、唯一前缀或 UUID**；匹配到多个会报错并列出候选，不会猜。

## 多账号并发登录

并发登录前做一次回调路由设置（幂等，会备份原始注册，随时可还原）：

```powershell
& $Hub configure-deeplink register
```

之后可以两个窗口同时打开、同时登录不同的 Google 账号，回调会被转给正在等待登录的那个窗口（每个 pending 授权 30 分钟有效，启动失败或超时都会自动作废）。登录会话保存在各 Profile 自己的令牌文件里，验证标准：

1. 两个窗口同时处于已登录状态、显示不同账号；
2. `& $Hub restart "工作A"` 后 A 不需要重新登录。

> [!WARNING]
> 回调路由在 0.4.1 修复（此前路由会把每个回调都丢弃）。**修复后的首次真实并发登录端到端验收尚未完成**（见上方能力表）；依赖该场景前请先按 `MANUAL_TEST_PLAN.md` 完成一次 A/B 登录验收。

测完想还原系统注册：`& $Hub configure-deeplink restore`。

## GUI

从开始菜单 / 桌面快捷方式 / `Start Hub.cmd` 打开。

- 顶部：**+ New profile**（创建并自动准备隔离）、**Open all**、**Stop all**、**Refresh**、**Doctor**、**Callbacks**（注册/还原回调路由）、**Settings**、**Data**、**Logs**
- 每行：主按钮 **Open / Close**；`⋯` 菜单含 Launch、Stop、Force stop、Restart、Force restart、Fix isolation、Prepare isolation、Workspace…、Rename…、Open data folder、Create shortcut、Remove…
- **Isolation 列**：`Direct / serial`（未准备隔离，只能串行）/ `Needs fix` / `Ready`
- 两个及以上 Profile 尚未注册回调路由时，顶部会出现一次性提示（可关闭）；窗口标题含当前版本号

日常只需要 **Refresh → Open**；顶部出现提示条时按行 `Fix isolation` 即可。

## CLI 参考

```powershell
& $Hub help        # 完整帮助；所有命令支持 --json 输出
```

| 日常命令 | 作用 |
|---|---|
| `setup` / `doctor` | 环境检查（可执行文件检查失败时退出码 1，脚本可用它做闸门） |
| `new <名字> [--workspace <路径>] [--backend <process-local>]` | 创建 Profile 并自动准备隔离（`create` 是它的别名） |
| `open <名字>` | 补齐缺失配置后启动 |
| `close <名字> [--force]` | 停止（未保存完时与 `stop` 一样返回 `graceful_close_pending`，退出码 1） |
| `fix <名字>` | 自检并修复隔离 |
| `status [名字]` / `ls` | 状态 / 列表 |

高级命令：`launch`、`stop`、`restart`、`launch-all`、`stop-all`、`rename`、`remove`、`workspace`、`show`、`iso-prepare`、`iso-launch`、`iso-stop`、`iso-status`、`open-data`、`open-logs`、`configure-executable`、`configure-isolation <process-local>`、`configure-deeplink <register\|restore>`、`shortcut`、`agent-probe`。退出码表见 `agprofile help`。

- `configure-isolation` 设置**新建** Profile 的默认后端；已有 Profile 保持各自记录。已删除的 `sandboxie` 后端名不再接受。
- `--direct` 跳过隔离（旧的串行路径，多实例会互相覆盖登录，仅特殊场景使用）。
- 停止隔离 Profile 会终止其整个进程树——**先在 Antigravity 里保存工作**。

常用错误码：

| 错误码 | 含义 |
|---|---|
| `executable_not_found` / `version_unverified` | 没找到官方 Antigravity / 版本未验收（2.12.2 是唯一验收过的版本） |
| `executable_attestation_changed` | 配置的可执行文件与配置时的 SHA-256 不符（可能被替换或更新），拒绝启动/回调；确认后重新 `configure-executable` |
| `profile_name_conflict` | 已存在同名 Profile（名字必须唯一） |
| `profile_name_ambiguous` | 名字/前缀匹配到多个 Profile，用更长前缀 |
| `isolation_scope_unverified` | 实例未能证明使用独立令牌存储，已被自动终止；重试或检查版本 |
| `isolation_not_ready` / `isolation_not_prepared` | 隔离未就绪 / 未准备，运行 `fix` |
| `process_enumeration_unavailable` | 进程列表不可读（停止/启动被拒绝而不是谎报）；稍后重试 |
| `deeplink_callback_invalid` | 回调 URI 不是合法的 `antigravity://` 地址，已丢弃 |
| `graceful_close_pending` | 普通关闭超时，确认保存后 `close --force` |
| `profile_not_stopped` | 运行中的 Profile 不能注销 |

## 数据、备份与卸载

```text
%LOCALAPPDATA%\AntigravityProfileHub
├─ config\profiles.json / settings.json     Profile 与设置（有 schema，勿手改）
├─ profiles\<uuid>\                         独立 home / user-data（含各自的登录令牌文件）
├─ isolation\state.json                     Profile 与作用域映射
├─ runtime\                                 运行期状态、回调路由记录
└─ logs\                                    结构化日志
```

- 备份：停止全部 Profile 后复制整个数据目录。其中包含各 Profile 的登录令牌文件，请像个人应用数据一样保管，不要上传公开仓库。
- 升级：直接覆盖安装；数据与登录保留。Antigravity 大版本更新后建议重跑 `doctor` 与登录验收。
- 卸载：删除程序与数据目录即可，外部 Workspace 不受影响。

## 故障排查

| 现象 | 处理 |
|---|---|
| 找不到 Antigravity / 版本未验收 | `configure-executable <绝对路径>`；不要绕过版本检查 |
| 冷启动很慢 | 正常（首次下载内置组件），等 1–2 分钟 |
| `isolation_scope_unverified` | 重试一次；持续失败说明厂商行为变了，运行 `doctor` 并带日志提 Issue |
| 并发登录回调错乱 | 确认已 `configure-deeplink register`；测完可 `restore` |
| 状态显示 Unknown | `fix <名字>`；仍异常时保留 `open-logs` 输出 |
| Doctor 报 retained launch barrier | 关闭残留实例后再 `status` / `doctor`，不要手删状态文件 |
| SmartScreen 警告 | 核对 Release 的 SHA-256 后再运行 |

报告问题时只提供错误码、非敏感日志与复现步骤——**不要**提交密码、授权码、token、Cookie 或账号截图。

## 从源码构建

- Windows x64 + .NET 10 SDK（安装器另需 Inno Setup 6）

```powershell
git clone https://github.com/Aliarrrrr/antigravity-profile-hub.git
cd antigravity-profile-hub
pwsh -NoProfile -File .\hub.ps1 build -Test     # Release 构建 + 测试
pwsh -NoProfile -File .\scripts\Publish.ps1     # 便携包
```

推送 `v*` 标签会触发 GitHub Actions 自动构建便携包、安装器并创建 Pre-release。

## 安全边界

- Google 登录完全由官方 Antigravity 完成；Hub 不保存密码、不导出 token、不复制 Cookie。
- Hub 写入磁盘的只有各 Profile 自己目录内的登录令牌文件（由官方应用生成）。
- Profile 作用域解决的是**同一 Windows 用户**下的应用状态与认证冲突，不是 Windows 用户之间的安全边界。
- 配置过的 Antigravity 可执行文件会被记录 SHA-256 并在每次启动与回调转交前校验；被替换的二进制会被拒绝（`executable_attestation_changed`）。
- `antigravity://` 回调只接受合法 scheme、长度受限、无控制字符的 URI；pending 授权 30 分钟过期，启动失败即作废，且只属于正在启动的那个 Profile。
- 进程列表不可读时 Hub 拒绝启动/停止并如实报错，不会把"看不见"当作"没有"。
- 机制验证工具在 `scripts/`（`Probe-*.py`）：用一次性 TEMP 数据根验证文件令牌存储与进程环境传播；Antigravity 升级后按 `MANUAL_TEST_PLAN.md` 重跑一遍即可确认机制仍然有效。会改动真实数据的一次性历史脚本已隔离到 `scripts/legacy/`，不要在日常环境运行。

进一步阅读：[SECURITY.md](SECURITY.md) · [KEYRING_ISOLATION_REPORT.md](KEYRING_ISOLATION_REPORT.md) · [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md) · [ARCHITECTURE.md](ARCHITECTURE.md) · [DECISIONS.md](DECISIONS.md) · [CHANGELOG.md](CHANGELOG.md) · [PROJECT_STATE.md](PROJECT_STATE.md)

## 许可证

本项目以 [MIT License](LICENSE) 发布。
