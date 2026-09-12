# Antigravity Profile Hub 0.3.0 — 实验版

Antigravity Profile Hub 是一个 Windows 原生的 Antigravity Desktop 多 Profile 管理器。它为每个 Profile 分配独立的进程主目录和 Electron user-data，并让语言服务把登录令牌保存在各自的 Profile 目录里，让多个官方 Antigravity Desktop 窗口可以在同一个 Windows 用户下使用不同的 Google 账号——**不需要安装 Sandboxie 或任何沙盒软件**。

登录始终在官方 Antigravity / Google 页面内完成。Hub 不接收密码，不读取、复制或导出 OAuth token、Cookie 或 Credential Manager 中的凭据内容。

> [!WARNING]
> 当前版本只在 **Windows x64 + Antigravity Desktop 2.12.2** 上完成验收。Hub 会拒绝为未经验证的 Antigravity 版本猜测启动协议。升级 Antigravity 后，请重新运行 `doctor` 并重新进行人工验收。

> [!IMPORTANT]
> 多账号并发必须为每个 Profile 准备独占的隔离作用域（默认后端自动完成）。未准备隔离作用域的 Profile 会走旧的“Direct / serial”路径，只能串行运行，并共享当前 Windows 用户的宿主凭据。

> [!TIP]
> 隔离配置全部自动完成，**全程无需管理员权限**。日常只需要 `hub new`、`hub open`、`hub close`、`hub fix` 四条命令：创建作用域、登记映射、以及启动时的隔离自证，都由 Hub 自己完成。旧命令仍然保留，作为高级操作与恢复手段。

## 目录

- [当前能力与限制](#当前能力与限制)
- [下载安装](#下载安装)
- [首次运行检查](#首次运行检查)
- [五分钟快速开始](#五分钟快速开始)
- [完整隔离配置教程](#完整隔离配置教程)
- [GUI 使用教程](#gui-使用教程)
- [CLI 使用教程](#cli-使用教程)
- [日常使用与 Workspace](#日常使用与-workspace)
- [数据、备份、升级与卸载](#数据备份升级与卸载)
- [故障排查](#故障排查)
- [从源码构建和发布](#从源码构建和发布)
- [安全边界](#安全边界)

## 当前能力与限制

| 能力 | 当前状态 |
|---|---|
| 独立 Profile 目录、Electron user-data 和进程树 | 已实现 |
| 两个不同 Google 账号并发运行 | 已在指定兼容版本上人工验收 |
| Profile 独立启动、停止、重启和桌面快捷方式 | 已实现 |
| 隔离作用域的自动配置与自愈 | 已实现（`new` / `fix`，无需管理员权限） |
| 按名字（显示名 / 唯一前缀 / UUID）操作 Profile | 已实现 |
| GUI 和 CLI 管理 | 已实现（GUI 含主题、状态徽章与自修复入口） |
| 官方 Antigravity / Google 登录 | 支持，必须由用户在官方窗口手动完成 |
| 自动输入账号、密码或授权码 | 不支持，也不会实现 |
| Workspace 路径登记和直写检查 | 已实现 |
| 启动时自动让 Antigravity 打开 Workspace | 暂不支持 |
| Profile 导入、导出或彻底清理 | 暂不支持 |
| Tray、独立 headless 身份、Agent Safe Git worktree、配额管理 | 暂不支持 |
| 其他 Windows 用户、网络或系统级安全隔离 | 不提供 |

为什么不能只复制 `user-data` 目录：Antigravity Desktop 2.12.2 的语言服务使用固定的 Windows Credential Manager 目标 `gemini:antigravity`。仅改变 HOME、APPDATA 或 `--user-data-dir` 时，多个实例仍会读取同一个宿主凭据。当前方案把每个实例的进程主目录指向它自己的 Profile 目录，并注入一个环境变量让语言服务改用**各自目录下的文件令牌存储**；启动时 Hub 还会检查语言服务日志，确认实例确实切到了自己的文件存储，未通过检查的实例会被自动终止。详细证据见 [KEYRING_ISOLATION_REPORT.md](KEYRING_ISOLATION_REPORT.md)。

## 下载安装

### 1. 前置条件

日常运行需要：

- Windows 10/11 x64；
- 官方 Antigravity Desktop **2.12.2**。

安装包和默认便携包是自包含的，日常用户不需要另装 .NET。只有从源码构建时才需要 PowerShell 7 和 .NET 10 SDK。

Hub 不会捆绑、下载或静默安装 Antigravity Desktop。

### 2. 选择安装包或便携包

从 [v0.3.0 Release](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.3.0) 下载文件。

| 文件 | 适合场景 | 说明 |
|---|---|---|
| `AGProfileHub-Setup-win-x64-0.3.0.exe` | 推荐日常使用 | 按当前用户安装，开始菜单入口稳定，升级方便 |
| `AGProfileHub-win-x64.zip` | 免安装、U 盘或测试 | 解压后直接运行，移动目录后需重建快捷方式 |
| 对应的 `.sha256.json` | 完整性校验 | 保存 Release 构建时计算的 SHA-256 |

### 3. 校验下载文件

在下载目录打开 PowerShell。安装包示例：

```powershell
$File = ".\AGProfileHub-Setup-win-x64-0.3.0.exe"
$Expected = (Get-Content "$File.sha256.json" -Raw | ConvertFrom-Json).hash
$Actual = (Get-FileHash $File -Algorithm SHA256).Hash

if ($Actual -ne $Expected) {
    throw "SHA-256 不匹配，请删除文件并重新下载。"
}

"SHA-256 校验通过：$Actual"
```

校验便携 ZIP 时把第一行替换为：

```powershell
$File = ".\AGProfileHub-win-x64.zip"
```

### 4. 使用安装器部署

1. 双击 `AGProfileHub-Setup-win-x64-0.3.0.exe`。
2. 确认来源和 SHA-256 后完成安装。当前构建尚未进行可信代码签名，Windows SmartScreen 可能显示“未知发布者”。
3. 可选勾选桌面快捷方式。
4. 安装完成后，从开始菜单打开 **Antigravity Profile Hub**。

默认安装目录：

```text
%LOCALAPPDATA%\Programs\AntigravityProfileHub
```

安装器仅为当前用户安装，不修改 PATH，也不会安装外部依赖。升级或卸载不会删除 Hub Profile 数据。

因为安装器不加入 PATH，在 PowerShell 中推荐先定义：

```powershell
$InstallRoot = "$env:LOCALAPPDATA\Programs\AntigravityProfileHub"
$Hub = Join-Path $InstallRoot "agprofile.cmd"
& $Hub help
```

### 5. 使用便携包部署

1. 完整解压 ZIP，不要直接在压缩包预览窗口中运行。
2. 将解压后的 `AGProfileHub` 目录放到稳定位置，例如 `D:\Apps\AGProfileHub`。
3. 保持 `Start Hub.cmd`、`agprofile.cmd`、`hub`、`cli` 和 `scripts` 的相对位置不变。
4. 双击 `Start Hub.cmd` 打开 GUI。
5. 在包根目录打开 PowerShell，通过 `.\agprofile.cmd` 使用 CLI。

```powershell
Set-Location "D:\Apps\AGProfileHub"
$Hub = Join-Path (Get-Location) "agprofile.cmd"
& $Hub doctor
```

不要只复制 `hub\AGProfileHub.Gui.exe`。GUI 创建快捷方式和深度链接回调路由时还需要同一包内的 `cli` 与 `scripts`。

## 首次运行检查

安装或解压完成后先运行：

```powershell
& $Hub setup
```

（`setup` 与 `doctor` 等价，名字更直白。）重点检查：

- `Antigravity executable` 指向真实的 `Antigravity.exe`；
- `Version` 为 `2.12.2`；
- `Experimental compatible` 为 `True`；
- `executableDiscovery` 为 `Verified`。

尚未创建 Profile 时，隔离相关提醒可以暂时存在。

如果找不到 Antigravity：

- GUI：点击 **Settings**，选择正确的 `Antigravity.exe`；
- CLI：

```powershell
& $Hub configure-executable "$env:LOCALAPPDATA\Programs\antigravity\Antigravity.exe"
& $Hub doctor
```

如果安装路径不同，请传入真实的绝对路径。不要通过修改版本信息绕过 `version_unverified`；未经验证的版本可能改变进程、目录或认证行为。

## 五分钟快速开始

下面假设已经定义 `$Hub`。

### 1. 创建并启动第一个 Profile

```powershell
& $Hub new "Work A"
& $Hub open "Work A"
```

`new` 会一次完成：创建 Profile、创建隔离作用域、登记映射。`open` 会补齐缺失的隔离配置再启动，并在启动后验证实例确实使用自己的令牌存储。全程无需管理员权限。

### 2. 第二个账号

```powershell
& $Hub new "Work B"
& $Hub open "Work B"
```

在各自弹出的官方 Antigravity 窗口内完成 Google 登录即可。

### 3. 日常

```powershell
& $Hub ls                 # 列表
& $Hub open "Work A"      # 启动（名字即可，不用 UUID）
& $Hub close "Work A"     # 停止
& $Hub fix "Work A"       # 自检并修复隔离
& $Hub launch-all         # 全部就绪后一键启动
```

名字寻址：所有命令都接受显示名、唯一前缀或 UUID，不再强制 32 位 UUID。

> [!NOTE]
> 旧的手工步骤（`iso-prepare` 等）仍然可用，但只作为高级操作或恢复手段，见「完整隔离配置教程」。日常只需要 `new` / `open` / `close` / `fix`。

## 完整隔离配置教程

隔离配置由 `hub new` 和 `hub fix` 自动完成，每个 Profile 只需完整配置一次。本节说明每个阶段实际发生的事情，供排障或恢复时参考。

### 第一步：创建 Profile 并保存 UUID

```powershell
$Profile = & $Hub --json create "Client A" | ConvertFrom-Json
$ProfileId = $Profile.id
$ProfileId
```

如果工作区已经存在，也可以在创建时登记：

```powershell
$Profile = & $Hub --json create "Client A" --workspace "D:\Projects\ClientA" | ConvertFrom-Json
$ProfileId = $Profile.id
```

Workspace 目录必须已经存在。

### 第二步：准备隔离作用域

```powershell
& $Hub iso-prepare $ProfileId
```

Hub 根据 UUID 生成唯一作用域名，例如：

```text
AGPH_123456781234123412341234567890ab
```

准备动作只是创建目录布局并写入一个作用域标记文件，不需要提权，也不会触碰系统配置或凭据。

### 第三步：登记 Workspace

如果创建时没有设置：

```powershell
& $Hub workspace $ProfileId "D:\Projects\ClientA"
```

清除登记：

```powershell
& $Hub workspace $ProfileId none
```

### 第四步：验证状态

```powershell
& $Hub --json iso-status
& $Hub status $ProfileId
```

预期结果：作用域存在、进程数正常；GUI 的 Isolation 列显示 `Ready`。

### 第五步：启动并在官方窗口登录

```powershell
& $Hub launch $ProfileId
```

只在弹出的官方 Antigravity 窗口中登录。不要把密码、OAuth code、Cookie、token 或包含这些信息的截图交给 Hub、脚本、Issue 或日志。

启动时 Hub 会等待语言服务日志出现"已切换到文件令牌存储"的证据；一个冷 Profile 首次启动需要下载内置组件，可能需要一到两分钟。如果实例始终无法证明自己离开了共享凭据条目，Hub 会终止它并返回 `isolation_scope_unverified`——这是有意的安全行为。

### 第六步：多账号并发登录

```powershell
& $Hub open "Work A"
& $Hub open "Work B"
```

两个窗口可以同时登录不同的 Google 账号。首次并发登录前，运行一次 `configure-deeplink register`，让 `antigravity://` 回调找到正在等待它的窗口（Hub 会备份原始注册，`configure-deeplink restore` 一键还原）。

更完整的人工验收清单见 [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md)。

## GUI 使用教程

从开始菜单、桌面快捷方式或 `Start Hub.cmd` 打开 GUI。

### 顶部操作

| 按钮 | 作用 |
|---|---|
| **+ New profile** | 创建 Profile 并自动准备其隔离作用域；名称为 1–80 个可打印字符 |
| **Open all** | 启动全部 Profile；多 Profile 时要求每个 Profile 都有可用的隔离盒 |
| **Stop all** | 停止全部 Profile；先保存工作 |
| **Refresh** | 重新读取 Profile、进程和隔离状态（并行刷新） |
| **Doctor** | 检查程序路径、兼容版本、隔离能力和风险警告 |
| **Settings** | 手动选择 `Antigravity.exe` |
| **Data** | 打开 Hub 数据根目录 |
| **Logs** | 打开 Hub 结构化日志目录 |

当存在多个 Profile 且至少一个还没有隔离映射时，**Open all** 会被禁用。按钮可用时也应先确保每个 Profile 的 Isolation 列为 `Ready`。

### Profile 行操作

每行只有两个操作：主按钮和 `⋯` 菜单。

| 操作 | 作用与注意事项 |
|---|---|
| **Open / Close**（主按钮） | 按当前状态显示。`Open` 会先补齐缺失的隔离配置再启动；`Close` 停止该 Profile，先保存工作 |
| `⋯` → **Launch** | 直接启动，不做前置补齐 |
| `⋯` → **Stop / Force stop** | 停止；Force stop 用于普通关闭失败时 |
| `⋯` → **Restart / Force restart** | 停止后重新启动；Force restart 用于普通关闭失败时 |
| `⋯` → **Fix isolation** | 自检并自动修复隔离（创建作用域、登记映射），无需管理员权限 |
| `⋯` → **Prepare isolation** | 只做作用域创建与登记 |
| `⋯` → **Workspace…** | 登记或清除 Workspace 元数据 |
| `⋯` → **Rename…** | 修改显示名称，不改变 UUID |
| `⋯` → **Open data folder** | 打开该 Profile 的托管数据目录 |
| `⋯` → **Create shortcut** | 在桌面创建绑定 UUID 和当前数据根的启动快捷方式 |
| `⋯` → **Remove…** | 只取消登记；Profile 数据会保留 |

其他界面元素：

- **Status 徽章**：`Running`（绿）/ `Stopped`（灰）/ `Unknown`（黄）。`Unknown` 通常表示进程状态与隔离盒不一致，用 `⋯ → Fix isolation` 或 `Doctor` 排查。
- **Isolation 列**：`Direct / serial` 未准备隔离作用域（只能串行）；`Needs fix` 作用域未就绪；`Ready` 已就绪。
- **顶部提示条**：当有 Profile 隔离未就绪时出现，并指出需要修复的数量；可关闭。
- **空状态**：没有 Profile 时显示引导入口，直接创建第一个 Profile。

## CLI 使用教程

### 调用方式

安装版：

```powershell
$InstallRoot = "$env:LOCALAPPDATA\Programs\AntigravityProfileHub"
$Hub = Join-Path $InstallRoot "agprofile.cmd"
```

便携版：

```powershell
Set-Location "D:\Apps\AGProfileHub"
$Hub = Join-Path (Get-Location) "agprofile.cmd"
```

显示帮助：

```powershell
& $Hub help
```

### 日常命令

这四条覆盖绝大多数使用场景：

```powershell
& $Hub setup                                  # 首次运行：检查 Antigravity / 兼容版本
& $Hub new "Pro A"                            # 建 Profile + 自动创建隔离作用域
& $Hub new "Pro B" --workspace "D:\Projects\ProB"
& $Hub ls                                     # 列表
& $Hub open "Pro A"                           # 补齐缺失配置后启动
& $Hub close "Pro A"                          # 停止
& $Hub fix "Pro A"                            # 自检并自动修复隔离，输出每一步是否被应用
```

可选参数：

| 参数 | 作用 |
|---|---|
| `--workspace <path>` | 登记默认工作区元数据 |
| `--backend <sandboxie\|process-local>` | 为这一个 Profile 指定隔离后端 |
| `--direct` | 跳过隔离，使用旧的串行 Direct 路径 |
| `--fresh` | 明确确认“该作用域从未登录过”，允许接管一个已存在的未登记作用域 |

### Profile 寻址

所有命令都接受三种写法，不必再记 UUID：

```powershell
& $Hub open "Pro A"              # 显示名（不区分大小写）
& $Hub open "Pro"                # 唯一前缀
& $Hub open 3f2a1b8c-...         # UUID
& $Hub open 3f2a1b8c             # UUID 前缀
```

匹配到多个时命令会失败并列出候选，不会猜。可以用 `ls` 或 `status` 查看当前名称与 UUID。

### Profile 管理

```powershell
& $Hub create "Pro A"
& $Hub create "Pro B" --workspace "D:\Projects\ProB"
& $Hub list
& $Hub show <name-or-id>
& $Hub rename <name-or-id> "新的显示名称"
& $Hub workspace <name-or-id> "D:\Projects\Example"
& $Hub workspace <name-or-id> none
& $Hub remove <name-or-id>
```

注意：

- Profile 名称不是身份，重命名不会影响 UUID；
- Workspace 目录必须存在；
- `remove` 前必须停止 Profile；
- `remove` 只移除登记，不删除磁盘数据。

### 隔离管理（高级）

> [!NOTE]
> 以下命令是 `new` / `fix` 的手工等价形式，用于排障、恢复或需要精细控制时。日常不需要使用。

```powershell
& $Hub iso-prepare <name-or-id>
& $Hub iso-prepare <name-or-id> --fresh
& $Hub iso-status
```

`iso-launch` 和 `iso-stop` 是要求 Profile 已经存在隔离映射的显式命令：

```powershell
& $Hub iso-launch <name-or-id>
& $Hub iso-stop <name-or-id>
```

日常无需特意选择它们；普通 `launch`、`stop` 和 `restart` 会根据 Profile 映射自动选择隔离或 Direct 路由。

### 启动、状态和停止

```powershell
& $Hub launch <name-or-id>
& $Hub launch-all
& $Hub status
& $Hub status <name-or-id>
& $Hub restart <name-or-id>
& $Hub stop <name-or-id>
& $Hub stop-all
```

Direct Profile 普通关闭失败时，先保存工作，再显式使用：

```powershell
& $Hub stop <name-or-id> --force
& $Hub restart <name-or-id> --force
& $Hub stop-all --force
```

对隔离 Profile，停止操作会终止该 Profile 的整个进程树，不是文档保存协议。始终先在 Antigravity 中保存工作。

`launch-all` 会在启动任何 Profile 前先做整体预检：只要有一个 Profile 缺少映射或作用域未就绪，就不会启动任何一个。

### 文件夹与快捷方式

```powershell
& $Hub open-data
& $Hub open-data <name-or-id>
& $Hub open-logs
& $Hub shortcut <name-or-id>
& $Hub shortcut <name-or-id> --directory "D:\Shortcuts"
```

自定义快捷方式目录必须是已经存在的绝对路径。Hub 不覆盖同名旧快捷方式；需要更新路径时，请删除旧快捷方式后重新创建。

### JSON 和独立数据根

`--json` 输出 camelCase JSON，适合 PowerShell 或其他脚本：

```powershell
$Profiles = & $Hub --json list | ConvertFrom-Json
$Status = & $Hub --json status <name-or-id> | ConvertFrom-Json
```

使用独立数据根：

```powershell
$LabRoot = "D:\AGProfileHub-Lab"
& $Hub --data-root $LabRoot create "Lab"
& $Hub --data-root $LabRoot list
```

GUI 也支持独立数据根，但需要直接启动 GUI 可执行文件：

```powershell
& "$InstallRoot\hub\AGProfileHub.Gui.exe" --data-root "D:\AGProfileHub-Lab"
```

不同数据根拥有不同的 Profile 配置，但仍共享当前 Windows 用户的全局启动协调状态。`--data-root` 不是绕过并发或进程所有权保护的开关。

### CLI 退出码

| 退出码 | 含义 |
|---:|---|
| `0` | 成功 |
| `1` | 一般 Hub 错误或未预期错误 |
| `2` | 命令参数错误 |
| `3` | Profile 不存在（含名称无匹配） |
| `4` | 当前能力或版本不支持 |
| `5` | 无法安全确认进程所有权 |
| `130` | 用户取消 |

常用错误码：

| 错误码 | 含义与处理 |
|---|---|
| `isolation_scope_unverified` | 启动后实例未能证明自己使用独立的文件令牌存储，已被自动终止 | 运行 `fix <name-or-id>` 后重试；持续失败时检查 Antigravity 版本 |
| `isolation_not_ready` | `open` 前置补齐后仍未就绪；运行 `fix <name-or-id>` 查看具体阶段 |
| `profile_name_ambiguous` | 名称/前缀匹配到多个 Profile，用更长前缀或 UUID |

内部命令 `callback` 不属于日常 CLI 接口，不要手动调用。

## 日常使用与 Workspace

推荐的日常流程：

1. 打开 Hub，点击 **Refresh**，确认目标 Profile 的 Isolation 列为 `Ready`。若不是，用 `⋯ → Fix isolation` 修好，或直接点 **Open**（它会先补齐）。
2. 点击该行的 **Open**，或使用该 Profile 的桌面快捷方式。
3. 多个 Profile 都已准备完成时，可以使用 **Open all**。
4. 在官方 Antigravity 窗口内工作和登录。
5. 停止或重启前保存全部文件。
6. 使用 Hub 的 **Close** / `⋯ → Restart`，不要按进程名批量结束 `Antigravity.exe`。

顶部一旦出现提示条，说明有 Profile 的隔离未就绪；它只统计数量，具体修复按行操作即可。

快捷方式绑定 Profile 的不可变 UUID、创建时使用的数据根以及当时包内 CLI 的绝对路径。安装版升级路径固定，旧快捷方式通常可继续使用；便携版移动目录后需要重建。

### Workspace 的真实含义

`workspace` 当前只完成两件事：

1. 在 Profile 中保存默认工作区元数据。

它**不会**自动让 Antigravity Desktop 在启动时打开该项目。当前没有发现 Desktop 2.12.2 可验证的 workspace 转发参数，因此：

- 启动 Profile 后仍需在官方应用内手动打开项目；
- `launch <id> --workspace <path>` 会明确返回“不支持”，不会假装成功；
- 同一个仓库不要同时交给多个 Profile 修改；
- Workspace 是外部路径，`remove` 不会删除它。

## 数据、备份、升级与卸载

### 默认数据目录

```text
%LOCALAPPDATA%\AntigravityProfileHub
```

| 路径 | 内容 |
|---|---|
| `config\profiles.json` | Profile UUID、名称和 Workspace 元数据 |
| `config\settings.json` | Antigravity 可执行文件等普通设置 |
| `profiles\<uuid>\` | 独立 home、roaming、local、temp 和 user-data |
| `isolation\state.json` | Profile 与作用域的映射，不包含 token |
| `runtime\` | 进程状态、锁、深度链接回调路由与运行期记录 |
| `logs\` | Hub 白名单结构化日志 |
| `global-launch-state\` | 所有 Hub 数据根共享的启动协调状态 |

### 备份

Hub 当前没有正式的 Profile 导出/导入功能。如果需要本机灾备：

1. 在所有 Antigravity 窗口保存工作。
2. 通过 Hub 停止全部 Profile。
3. 确认没有 Hub 写入操作。
4. 复制整个 `%LOCALAPPDATA%\AntigravityProfileHub` 到受保护的位置。

该目录可能包含官方应用生成的本地状态，应像其他个人应用数据一样保护，不要上传到公开仓库或 Issue。

Hub 数据备份不包含登录状态本身：令牌保存在各 Profile 目录内的文件中（属于被备份的数据），但跨机器迁移仍应预期通过官方页面重新登录，而不是复制或导出认证秘密。

### 查看数据但不要手改

```powershell
& $Hub open-data
& $Hub open-logs
```

不要在运行时手动修改 `profiles.json`、`settings.json`、`isolation\state.json` 或 `global-launch-state`。这些文件有 schema、原子写入、锁和进程安全约束，手改可能使 Hub fail closed。

### 安装版升级

1. 查看新版本的 CHANGELOG 和兼容版本说明。
2. 保存工作并停止所有 Profile。
3. 退出 Hub。
4. 下载并校验新版安装器。
5. 直接安装到默认目录，覆盖程序文件。
6. 重新运行 `doctor` 和 `iso-status`。
7. 如果 Antigravity 也升级过，重新完成多账号和 Workspace 人工验收。

### 便携版升级

1. 停止全部 Profile 并退出旧版 Hub。
2. 把新版 ZIP 解压到新的完整目录，不要只覆盖个别 EXE。
3. 运行新版 `Start Hub.cmd`。
4. 默认数据位于 `%LOCALAPPDATA%`，会被新版继续读取。
5. 如果目录位置变化，重新创建桌面快捷方式。

### 卸载

在 Windows“已安装的应用”中卸载 **Antigravity Profile Hub**。卸载会删除安装目录中的程序文件，但会保留：

- `%LOCALAPPDATA%\AntigravityProfileHub` 中的 Profile 数据；
- 外部 Workspace；
- 用户自己创建的项目文件。

重新安装后通常可以继续看到原有 Profile。当前版本没有“彻底清理所有数据”按钮；如需彻底删除，请先备份并确认目标，避免误删项目。

## 故障排查

| 现象或错误 | 原因 | 处理 |
|---|---|---|
| `executable_not_found` | 没找到官方 Antigravity | GUI 点击 **Settings**，或运行 `configure-executable <绝对路径>` |
| `version_unverified` | Antigravity 不是 2.12.2 | 不要绕过；恢复到已验证版本，或完成新版本适配与验收 |
| `isolation_scope_unverified` | 实例未能证明自己使用独立令牌存储，已被终止 | 重试一次；持续失败时确认 Antigravity 版本并运行 `doctor` |
| `isolation_scope_unowned` | 已存在未登记的作用域 | 只有确认从未登录过时才用 `new <名字> --fresh` 或 `iso-prepare <name-or-id> --fresh` |
| `isolation_not_prepared` | Profile 没有隔离映射 | 运行 `fix <name-or-id>` |
| GUI 显示 `Needs fix` | 作用域未就绪 | 运行 `fix <name-or-id>` |
| `launch-all` 被禁用或失败 | 至少一个 Profile 的映射或作用域未就绪 | 顶部提示条会给出数量；逐个 `fix <name-or-id>` |
| `isolation_not_ready` | `open` 前置补齐后仍未就绪 | 运行 `fix <name-or-id>` 查看卡在哪一步 |
| `profile_name_ambiguous` | 名称或前缀匹配到多个 Profile | 用更长的前缀或 UUID（`ls` 可查看） |
| 提示另一个 Antigravity root 正在运行 | 正在使用 Direct / serial 路径 | 停止另一个 Direct 实例，或为所有并发 Profile 准备隔离盒 |
| `graceful_close_pending` | 普通关闭超时 | 检查未保存工作，再使用 `close <name-or-id> --force` |
| `profile_not_stopped` | 运行中的 Profile 不能注销 | 先运行 `close <name-or-id>` 并确认状态 |
| `cli_not_found` | 便携包目录被拆散 | 恢复完整 `hub`、`cli`、`scripts` 相对结构 |
| 找不到 `scripts\...ps1` | 当前 PowerShell 不在包根目录 | 先 `Set-Location` 到安装或便携根目录（仅高级/恢复路径需要） |
| SmartScreen 显示未知发布者 | 当前 EXE 未签名 | 先核对 Release 来源和 SHA-256，只运行信任的构建 |
| Workspace 没有自动打开 | 当前只保存元数据 | 启动后在官方 Antigravity 中手动打开项目 |
| Doctor 报 retained global launch barrier | 上一次启动的进程状态不确定 | 不要手删状态文件；关闭残留实例、再次运行 `status` / `doctor`，仍失败时保留日志排查 |

排查时建议依次运行：

```powershell
& $Hub doctor
& $Hub ls
& $Hub status
& $Hub fix "<名字>"
& $Hub open-logs
```

报告问题时只提供错误码、非敏感日志和复现步骤。不要提交密码、授权码、token、Cookie、Credential Manager 内容或账号隐私截图。

## 从源码构建和发布

### 开发环境

- Windows x64；
- PowerShell 7；
- .NET 10 SDK；
- 构建安装器时额外需要 Inno Setup 6；
- 运行真实集成测试时需要兼容版本的 Antigravity Desktop。

### 克隆与测试

```powershell
git clone https://github.com/Aliarrrrr/antigravity-profile-hub.git
Set-Location .\antigravity-profile-hub
pwsh -NoProfile -File .\scripts\Build.ps1 -Test
```

该命令执行 Release 构建和确定性测试，不要求真实账号登录。

从源码启动 GUI：

```powershell
dotnet run --project .\src\AGProfileHub.Gui\AGProfileHub.Gui.csproj -c Release
```

从源码运行 CLI：

```powershell
dotnet run --project .\src\AGProfileHub.Cli\AGProfileHub.Cli.csproj -c Release -- doctor
```

### 生成便携包

```powershell
pwsh -NoProfile -File .\scripts\Publish.ps1
```

输出位于：

```text
artifacts\package\<timestamp>\
  AGProfileHub\
  AGProfileHub-win-x64.zip
  AGProfileHub-win-x64.zip.sha256.json
```

`Publish.ps1` 默认生成 Windows x64 自包含包，并在打包前自动执行构建和测试。

### 生成安装器

安装 Inno Setup 6 后运行：

```powershell
pwsh -NoProfile -File .\scripts\Publish.ps1 -Installer
```

输出中会额外包含 `AGProfileHub-Setup-win-x64-<version>.exe` 和对应的 `.sha256.json`。

也可以对已有自包含包单独构建：

```powershell
pwsh -NoProfile -File .\scripts\Build-Installer.ps1 -PackageRoot ".\artifacts\package\<timestamp>\AGProfileHub"
```

### 验证发布包

只检查包结构、版本和脚本解析：

```powershell
pwsh -NoProfile -File .\scripts\Test-PackageStructure.ps1 -PackageRoot ".\artifacts\package\<timestamp>\AGProfileHub"
```

以下脚本会启动真实 Antigravity 进程，只能在已经保存工作，并确认不会干扰现有实例时运行：

```powershell
pwsh -NoProfile -File .\scripts\Test-Integration.ps1
pwsh -NoProfile -File .\scripts\Test-Package.ps1 -PackageRoot ".\artifacts\package\<timestamp>\AGProfileHub"
```

真实 Google 登录永不自动化。

### GitHub Actions 发布

- 推送到 `master` / `main` 或创建 Pull Request：运行 `scripts/Build.ps1 -Test`；
- 推送 `v*` 标签：构建便携 ZIP、安装器、SHA-256 文件并创建 GitHub Pre-release；
- 标签必须与 `Directory.Build.props` 中的版本完全一致，例如版本 `0.3.0` 对应标签 `v0.3.0`；
- Release workflow 会验证包结构和安装/卸载生命周期；
- 公开分发前仍建议为 EXE 添加可信代码签名。

典型维护流程：

```powershell
pwsh -NoProfile -File .\scripts\Build.ps1 -Test
git add .
git commit -m "Prepare vX.Y.Z"
git push origin master
git tag vX.Y.Z
git push origin vX.Y.Z
```

不要重复使用或移动已经发布的版本标签。

## 安全边界

- Hub 只管理本地 Profile、路径、进程和隔离作用域映射。
- Google 登录、退出和授权由官方 Antigravity 完成。
- Hub 不保存 Google 密码，不导出 token，不复制 Cookie，不代理认证。
- Hub 不修改服务端认证、配额或官方程序二进制。
- Profile 作用域用于解决同一 Windows 用户下的应用状态和认证冲突，不是 Windows 用户之间的安全边界。
- `remove` 只注销登记；不会清理 Profile 数据或 Workspace。
- 停止隔离 Profile 会终止其整个进程树，操作前必须保存工作。
- Antigravity 更新后，旧验收结论自动失效，必须重新验证。

进一步阅读：

- [SECURITY.md](SECURITY.md)：完整安全模型；
- [KEYRING_ISOLATION_REPORT.md](KEYRING_ISOLATION_REPORT.md)：根因、失败方案和通过证据；
- [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md)：人工验收步骤；
- [PROJECT_STATE.md](PROJECT_STATE.md)：当前实现状态与剩余门槛；
- [ARCHITECTURE.md](ARCHITECTURE.md)：架构说明；
- [CHANGELOG.md](CHANGELOG.md)：版本变化。
