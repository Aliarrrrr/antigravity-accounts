# Antigravity Profile Hub 0.4.4 — 实验版

> **English (summary).** A native Windows manager that runs multiple official Antigravity Desktop windows under one Windows user, each signed in to a different Google account. Isolation is per-profile home + file-based token store with fail-closed launch proof — no sandbox product, no administrator rights. Login always happens in official Antigravity/Google pages; the Hub never reads passwords, OAuth tokens, or cookies. Only **Windows x64 + Antigravity Desktop 2.12.2** are verified. Full docs are in Chinese below.

Antigravity Profile Hub 让多个**官方** Antigravity Desktop 窗口在同一 Windows 用户下，各自保持**不同的 Google 账号**登录。

- **不需要 Sandboxie / 任何沙盒，全程无需管理员权限**（0.4.0+）
- 登录只在官方 Antigravity / Google 页面完成；Hub **不接收密码**，不读取、复制或导出 OAuth token、Cookie、Credential Manager 内容
- 每个 Profile：独立 home / user-data、独立文件令牌存储、启动自证（证明不过就终止）
- 0.4.3 起：未钉扎哈希的可执行文件**拒绝启动**；外启实例没有 Hub 自证时**拒绝当作健康隔离会话**

> [!WARNING]
> 仅在 **Windows x64 + Antigravity Desktop 2.12.2** 上完成验收。未知版本会 `version_unverified` 拒绝启动。升级 Antigravity 后请重跑 `doctor` 并按 [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md) 重做验收。

---

## 它解决什么

| 你想要 | Hub 做什么 |
|---|---|
| 同一台机器同时用两个 Google 账号开 Antigravity | 每个 Profile 独立 user-data + 文件令牌，互不顶号 |
| 登录时回调别串窗 | `configure-deeplink register` 把 `antigravity://` 转给正在等待的 Profile（可还原） |
| 不想装沙盒、不想给管理员权限 | process-local 作用域，普通目录即可 |
| 脚本化管理 | CLI 全动词 + `--json` + 明确退出码 |

**不是**安全沙箱：不隔离其他 Windows 用户、网络身份或已安装二进制。同一账号下任何进程仍可读 Profile 目录。

---

## 能力状态（0.4.5-dev）

| 能力 | 状态 |
|---|---|
| 两个不同 Google 账号窗口**并发保持登录** | ✅ 已人工验收（2.12.2） |
| 登录过程中 `antigravity://` 回调路由 | ✅ 已人工验收（2026-09-13）；pending 改为**多槽**（最多 4），多等待且无法归属时 fail-closed |
| 启动 / 停止 / 重启 / 桌面快捷方式 | ✅ |
| 隔离自动配置与自愈（`new` / `fix`，无提权） | ✅ |
| 按显示名 / 唯一前缀 / UUID 操作 | ✅ |
| GUI + CLI | ✅ |
| 官方 Google 登录 | ✅ 必须用户在官方窗口手动完成 |
| 自动填账号密码 / 授权码 | ❌ 不会做 |
| 启动时自动打开 Workspace | ❌ 2.12.2 无已验证契约；用 `open-workspace` 在资源管理器中打开 |
| 工具链桥接（宿主 `.ssh` / `.gitconfig`） | ⚠️ 实验：`iso-bridge` junction 进 Profile home |
| 原版对话 copy-only 导入 | ⚠️ 实验：`conversations import`（不碰 token） |
| Profile 内对话清单 | ⚠️ 实验：`conversations ls` |
| Profile 导入 / 导出 / 彻底删除数据 | ❌ 仍不支持身份导入/导出/删除 |
| 跨 Windows 用户 / 系统级安全隔离 | ❌ 不提供 |

---

## 安装

**前置：** Windows 10/11 x64 · 官方 Antigravity Desktop **2.12.2**

从 [Releases](https://github.com/Aliarrrrr/antigravity-profile-hub/releases) 下载 `v0.4.4`（或最新）：

| 文件 | 适合 |
|---|---|
| `AGProfileHub-Setup-win-x64-*.exe` | 按当前用户安装（推荐） |
| `AGProfileHub-*-win-x64.zip` | 免安装 / 便携 |

```powershell
$File = ".\AGProfileHub-Setup-win-x64-0.4.4.exe"
$Expected = (Get-Content "$File.sha256.json" -Raw | ConvertFrom-Json).hash
if ((Get-FileHash $File -Algorithm SHA256).Hash -ne $Expected) { throw "SHA-256 不匹配" }
"校验通过"
```

- 安装器：`%LOCALAPPDATA%\Programs\AntigravityProfileHub`，**不改 PATH**
- 便携包：解压到稳定路径后运行 `Start Hub.cmd`；**不要只拷走 `hub\Gui.exe`**（快捷方式与回调需要包内 `cli`）
- 未做代码签名，SmartScreen 可能拦：先核 SHA-256 再运行
- Hub **不捆绑、不下载** Antigravity Desktop

---

## 首次运行（必做）

```powershell
$Hub = "$env:LOCALAPPDATA\Programs\AntigravityProfileHub\agprofile.cmd"  # 安装版
# 便携：$Hub = "D:\Apps\AGProfileHub\agprofile.cmd"

& $Hub doctor
& $Hub configure-executable "C:\Users\你\AppData\Local\Programs\antigravity\Antigravity.exe"
```

确认：

1. `executableDiscovery = Verified`，版本 **2.12.2**
2. 已执行一次 **`configure-executable`**（0.4.3 起：**未钉扎哈希则拒绝 launch / 回调**，错误码 `executable_not_pinned`）
3. 需要双账号并发登录时：`& $Hub configure-deeplink register`（随时 `restore`）

---

## 五分钟上手

```powershell
& $Hub new "工作A"     # 创建 + 自动准备隔离
& $Hub open "工作A"    # 窗口出现后在官方页登录账号 #1

& $Hub new "工作B"
& $Hub open "工作B"    # 登录账号 #2
```

- 冷启动 **1–2 分钟**正常（语言服务首次拉组件）
- 日常：`ls` → `open` → `close`（先保存工作）
- 名字支持显示名 / 唯一前缀 / UUID；多匹配会报错列出候选，**不猜**

### 并发登录（可选）

```powershell
& $Hub configure-deeplink register   # 一次即可，备份原注册
# 两边都进 Google 登录页后：先完成一侧，再完成另一侧（单槽 pending）
& $Hub configure-deeplink restore    # 用完还原
```

验收过的标准：双窗不同账号 · 回调进等待窗 · 无第三个根进程 · `restart A` 不丢号且 B 不受影响。

---

## GUI

开始菜单 / 桌面快捷方式 / `Start Hub.cmd`。

| 区域 | 内容 |
|---|---|
| 工具栏 | + New profile · Open all · Stop all · Refresh · Doctor · Callbacks · Settings · Data · Logs |
| 每行 | 主按钮 Open/Close；`⋯` 菜单：Launch/Stop/**Force stop（需确认）**/Restart/**Force restart（需确认）**/Fix isolation/Prepare isolation/Workspace/Rename/Open data/Create shortcut/Remove |
| Isolation 列 | 语义色 badge：`Ready` 绿 · `Needs fix` 红 · `Direct / serial` 黄 |
| 冷启动 | 状态栏提示 1–2 分钟，勿强杀窗口 |
| Stop all | 危险样式 + 确认 |

两个及以上 Profile 未注册回调时，顶部会提示 **Callbacks**。

---

## CLI

```powershell
& $Hub help          # 全部命令；均支持 --json
```

| 日常 | 作用 |
|---|---|
| `setup` / `doctor` | 检查环境；可执行文件失败退出码 1 |
| `new <名字>`（别名 `create`） | 创建并自动准备隔离 |
| `open` / `close [--force]` | 启动 / 停止 |
| `fix` | 自检修复隔离 |
| `ls` / `status [名字]` | 列表 / 状态 |
| `open-workspace <名字>` | 在资源管理器中打开 Workspace 元数据路径（不自动进项目） |
| `iso-bridge prepare\|status\|remove` | 把宿主 `.ssh`/`.gitconfig` junction 进 Profile home |
| `conversations ls\|plan\|import` | 列出或 copy-only 导入对话库 |

高级：`launch`、`stop`、`restart`、`launch-all`、`stop-all`、`rename`、`remove`、`workspace`、`show`、`iso-*`、`open-data`、`open-logs`、`configure-executable`、`configure-isolation`、`configure-deeplink`、`shortcut`、`agent-probe`。

- `--direct`：跳过隔离的旧串行路径（多实例会抢共享凭据，仅特殊场景）
- 停止隔离 Profile = 终止其进程树 —— **先保存工作**
- 源码树可用 `.\scripts\Invoke-HubCli.ps1 <args>`（走仓库自带 .NET 10）

### 常用错误码

| 码 | 含义 |
|---|---|
| `executable_not_found` / `version_unverified` | 没找到 Antigravity / 版本未验收 |
| `executable_not_pinned` | 未钉扎哈希；`configure-executable` 后再启动 |
| `executable_attestation_changed` | 二进制与配置时 SHA-256 不符；确认后重新 configure |
| `isolation_scope_unverified` | 未能证明独立令牌存储（含外启无自证）；停掉后用 Hub 重开 |
| `process_enumeration_unavailable` | 进程列表不可读 → 拒绝启停，不谎报空闲 |
| `deeplink_callback_invalid` | 非法 `antigravity://` URI |
| `graceful_close_pending` | 普通关闭未完成；确认后 `close --force` |
| `profile_name_conflict` / `profile_name_ambiguous` | 重名 / 多匹配 |
| `profile_not_stopped` | 运行中不能注销注册 |

---

## 数据目录

```text
%LOCALAPPDATA%\AntigravityProfileHub
├─ config\profiles.json / settings.json
├─ profiles\<uuid>\user-data|home|...     含各 Profile 的文件令牌
├─ profiles\<uuid>\scope-attestation.json 冷启动自证（0.4.3）
├─ isolation\state.json
├─ runtime\                               运行态、deeplink pending/registration
└─ logs\
```

- **备份**：先 `stop-all`，再整目录复制；内含登录令牌，勿上传公开仓库
- **升级**：覆盖安装即可；Antigravity 大版本变更后重跑 doctor + 人工验收
- **卸载**：删程序与数据目录；Workspace 不受影响

---

## 故障排查

| 现象 | 处理 |
|---|---|
| 未钉扎拒绝启动 | `configure-executable <绝对路径>` |
| 版本未验收 | 勿绕过；等 Hub 支持或换回 2.12.2 |
| 冷启动很慢 | 正常，等 1–2 分钟 |
| `isolation_scope_unverified` | 外启实例需 `close --force` 后 Hub 重开；持续失败看 doctor + Issue |
| 并发登录回调串窗 | 确认已 register；避免两侧同时点 OAuth（单槽） |
| Unknown / retained barrier | `fix`；勿手删 runtime 状态文件 |
| SmartScreen | 核对 Release SHA-256 |

提 Issue：只给错误码、非敏感日志、复现步骤。**不要**附密码、授权码、token、Cookie、账号截图。

---

## 安全边界（摘要）

- 登录在官方应用完成；Hub 不碰密码 / token / Cookie
- 冷启动：语言服务日志证明切到文件令牌，否则终止
- 热路径：必须有 Hub 写的 `scope-attestation.json`，外启不算数
- 可执行文件：configure 时钉 SHA-256；未钉扎或哈希变化 → 拒绝 launch/handoff
- 回调：scheme/长度/控制字符校验；handoff 用 ProfileId 推导 user-data；pending 30 分钟过期
- `antigravity://` 注册可随时 restore；重断言会按「CLI 路径 + 本 data root」重算命令
- 进程枚举失败 = fail-closed，不当作空闲

详见 [SECURITY.md](SECURITY.md) · 验收记录 [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md)

**进一步阅读：** [CHANGELOG](CHANGELOG.md) · [ARCHITECTURE](ARCHITECTURE.md) · [KEYRING_ISOLATION_REPORT](KEYRING_ISOLATION_REPORT.md) · [DECISIONS](DECISIONS.md) · [PROJECT_STATE](PROJECT_STATE.md)

---

## 从源码构建

Windows x64 + .NET 10 SDK（装机包另需 Inno Setup 6）。

```powershell
git clone https://github.com/Aliarrrrr/antigravity-profile-hub.git
cd antigravity-profile-hub
pwsh -NoProfile -File .\hub.ps1 build -Test
pwsh -NoProfile -File .\scripts\Publish.ps1
```

推送 `v*` 标签会触发 CI 构建便携包、安装器并创建 Pre-release。

---

## 许可证

[MIT License](LICENSE)
