# Antigravity Profile Hub 0.2.3 — 实验版

Antigravity Profile Hub 是一个 Windows 原生的 Antigravity Desktop 多 Profile 管理器。它为每个 Profile 分配独立的进程主目录、Electron user-data 和 Sandboxie 隔离盒，让多个官方 Antigravity Desktop 窗口可以在同一个 Windows 用户下使用不同的 Google 账号。

登录始终在官方 Antigravity / Google 页面内完成。Hub 不接收密码，不读取、复制或导出 OAuth token、Cookie 或 Credential Manager 中的凭据内容。

> [!WARNING]
> 当前版本只在 **Windows x64 + Antigravity Desktop 2.12.2 + Sandboxie Plus 1.18.2 / core 5.73.2** 上完成验收。Hub 会拒绝为未经验证的 Antigravity 版本猜测启动协议。升级 Antigravity 或 Sandboxie 后，请重新运行 `doctor` 并重新进行人工验收。

> [!IMPORTANT]
> 多账号并发必须为每个 Profile 准备独占的 Sandboxie 隔离盒。未准备隔离盒的 Profile 会走旧的“Direct / serial”路径，只能串行运行，并共享当前 Windows 用户的宿主凭据。

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
| GUI 和 CLI 管理 | 已实现 |
| 官方 Antigravity / Google 登录 | 支持，必须由用户在官方窗口手动完成 |
| 自动输入账号、密码或授权码 | 不支持，也不会实现 |
| Workspace 路径登记和直写检查 | 已实现 |
| 启动时自动让 Antigravity 打开 Workspace | 暂不支持 |
| Profile 导入、导出、彻底清理或 Sandboxie box 删除 | 暂不支持 |
| Tray、独立 headless 身份、Agent Safe Git worktree、配额管理 | 暂不支持 |
| 其他 Windows 用户、网络或系统级安全隔离 | 不提供 |

为什么不能只复制 `user-data` 目录：Antigravity Desktop 2.12.2 的语言服务使用固定的 Windows Credential Manager 目标 `gemini:antigravity`。仅改变 HOME、APPDATA 或 `--user-data-dir` 时，多个实例仍会读取同一个宿主凭据。当前方案先在每个全新的 Sandboxie box 内声明同名隔离项，再让官方应用完成正常登录，从而避免凭据回落到宿主环境。详细证据见 [KEYRING_ISOLATION_REPORT.md](KEYRING_ISOLATION_REPORT.md)。

## 下载安装

### 1. 前置条件

日常运行需要：

- Windows 10/11 x64；
- 官方 Antigravity Desktop **2.12.2**；
- Sandboxie Plus **1.18.2 / core 5.73.2**；
- 当前 Windows 用户可在需要时打开一次“以管理员身份运行”的 PowerShell。

安装包和默认便携包是自包含的，日常用户不需要另装 .NET。只有从源码构建时才需要 PowerShell 7 和 .NET 10 SDK。

Hub 不会捆绑、下载或静默安装 Antigravity Desktop 和 Sandboxie Plus。Sandboxie 需要安装在 `C:\Program Files\Sandboxie-Plus` 或 `C:\Program Files\Sandboxie`。

### 2. 选择安装包或便携包

从 [v0.2.3 Release](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/v0.2.3) 下载文件。

| 文件 | 适合场景 | 说明 |
|---|---|---|
| `AGProfileHub-Setup-win-x64-0.2.3.exe` | 推荐日常使用 | 按当前用户安装，开始菜单入口稳定，升级方便 |
| `AGProfileHub-win-x64.zip` | 免安装、U 盘或测试 | 解压后直接运行，移动目录后需重建快捷方式 |
| 对应的 `.sha256.json` | 完整性校验 | 保存 Release 构建时计算的 SHA-256 |

### 3. 校验下载文件

在下载目录打开 PowerShell。安装包示例：

```powershell
$File = ".\AGProfileHub-Setup-win-x64-0.2.3.exe"
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

1. 双击 `AGProfileHub-Setup-win-x64-0.2.3.exe`。
2. 确认来源和 SHA-256 后完成安装。当前构建尚未进行可信代码签名，Windows SmartScreen 可能显示“未知发布者”。
3. 可选勾选桌面快捷方式。
4. 安装完成后，从开始菜单打开 **Antigravity Profile Hub**。

默认安装目录：

```text
%LOCALAPPDATA%\Programs\AntigravityProfileHub
```

安装器仅为当前用户安装，不修改 PATH，也不会安装外部依赖。升级或卸载不会删除 Hub Profile 数据或 Sandboxie box。

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

不要只复制 `hub\AGProfileHub.Gui.exe`。GUI 创建快捷方式和隔离 keyring 声明时还需要同一包内的 `cli` 与 `scripts`。

## 首次运行检查

安装或解压完成后先运行：

```powershell
& $Hub doctor
```

重点检查：

- `Antigravity executable` 指向真实的 `Antigravity.exe`；
- `Version` 为 `2.12.2`；
- `Experimental compatible` 为 `True`；
- `executableDiscovery` 为 `Verified`。

尚未创建 Profile 时，隔离盒或直写相关提醒可以暂时存在。

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

### 1. 创建两个 Profile

```powershell
$ProfileA = & $Hub --json create "Work A" | ConvertFrom-Json
$ProfileB = & $Hub --json create "Work B" | ConvertFrom-Json

$ProfileAId = $ProfileA.id
$ProfileBId = $ProfileB.id

& $Hub list
```

后续 Profile 参数必须使用 UUID，例如 `$ProfileAId`，不能使用显示名称。

### 2. 请求准备隔离盒

```powershell
& $Hub iso-prepare $ProfileAId
& $Hub iso-prepare $ProfileBId
```

普通用户首次创建 box 时通常会收到需要管理员权限的提示。复制提示中的 box 名称，然后按下一节执行管理员脚本和 `--fresh` 确认。

### 3. 配置直写并检查

```powershell
& $Hub iso-writethrough $ProfileAId
& $Hub iso-writethrough $ProfileBId
```

保存工作、停止所有 Sandboxie box，再从管理员 PowerShell 执行命令输出的 `Set-IsolationWriteThrough.ps1` 命令。

最后检查：

```powershell
& $Hub --json iso-status
& $Hub --json iso-writethrough $ProfileAId
& $Hub --json iso-writethrough $ProfileBId
```

每个 box 至少应满足 `boxExists=true`、`keyringClaimed=true`，每个 `iso-writethrough` 结果应为 `ready=true`。

### 4. 启动并登录

```powershell
& $Hub launch $ProfileAId
& $Hub launch $ProfileBId
```

在各自弹出的官方 Antigravity 窗口内完成 Google 登录。以后可以使用：

```powershell
& $Hub launch-all
```

## 完整隔离配置教程

这是多账号功能最关键的流程。每个 Profile 只需完整配置一次，但在更换 Workspace、Antigravity 版本或 Sandboxie 版本后需要重新检查。

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

### 第二步：请求准备隔离盒

```powershell
& $Hub iso-prepare $ProfileId
```

Hub 默认根据 UUID 生成唯一 box 名，例如：

```text
AGPH_123456781234123412341234567890ab
```

如果当前权限不能写入 `C:\Windows\Sandboxie.ini`，命令会失败，但会在错误提示中给出需要创建的 box 名和管理员脚本。此时不要手动启动 Antigravity 登录该 box。

### 第三步：以管理员身份创建 box

1. 在开始菜单搜索 PowerShell，并选择“以管理员身份运行”。
2. 切换到安装根目录或便携包根目录。
3. 把 `<box-name>` 替换成上一步输出的真实名称。

安装版：

```powershell
Set-Location "$env:LOCALAPPDATA\Programs\AntigravityProfileHub"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-IsolationBox.ps1 -BoxName <box-name>
```

便携版：

```powershell
Set-Location "D:\Apps\AGProfileHub"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\New-IsolationBox.ps1 -BoxName <box-name>
```

脚本输出 `OK <box-name>` 表示创建成功。它会关闭 box 的自动恢复和自动删除，并阻止直接开放宿主凭据。

### 第四步：确认这是从未登录过的新 box

回到普通 PowerShell：

```powershell
& $Hub iso-prepare $ProfileId --fresh
```

> [!CAUTION]
> `--fresh` 是明确的安全确认：该 Sandboxie box 必须从未启动过 Antigravity 登录，也从未存放过需要保留的登录状态。不要对已经登录过的 box 使用 `--fresh`。

`--fresh` 会让 Hub 在确认当前进程处于 Sandboxie 隔离环境后，写入一个刻意无效的同名占位项，以阻止读取回落到宿主 `gemini:antigravity`。Core 会拒绝在宿主环境执行这项写入。

成功输出类似：

```text
isolation: box=AGPH_... boxCreated=False mappingCreated=True keyringClaimed=True shimExit=11
```

再次运行时 `shimExit` 可能为 `0`；只要 `keyringClaimed=True` 且后续状态正常即可。

高级选项 `--box <existing-box-name>` 只适用于来源明确、未被其他 Profile 使用且从未登录过的既有 box。普通用户应使用自动生成的 box 名。

### 第五步：登记 Workspace

如果创建时没有设置：

```powershell
& $Hub workspace $ProfileId "D:\Projects\ClientA"
```

清除登记：

```powershell
& $Hub workspace $ProfileId none
```

先登记 Workspace，再生成直写配置，这样工作区路径会被纳入检查。

### 第六步：生成直写配置

```powershell
& $Hub iso-writethrough $ProfileId
```

命令会检查 Profile 的 `home`、`roaming`、`local`、`temp`、`user-data` 以及已登记的 Workspace。`OK` 表示已有规则，`MISSING` 表示仍需配置。命令末尾会打印一条包含真实 box 名和完整路径的管理员命令。

### 第七步：停止所有 Sandboxie box 并应用规则

1. 在所有 Antigravity 窗口中保存工作。
2. 使用 Hub 停止全部 Profile：

```powershell
& $Hub stop-all
```

3. 打开 Sandboxie Plus，确认**所有 box**都没有运行进程。
4. 以管理员身份打开 PowerShell 并切换到安装根目录。
5. 原样执行 `iso-writethrough` 输出的命令，其中必须包含 `-ConfirmAllBoxesStopped`。

命令形态如下；实际使用时请复制 Hub 输出，不要手工猜路径：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Set-IsolationWriteThrough.ps1 -BoxName <box-name> -Path "<path-1>","<path-2>" -ConfirmAllBoxesStopped
```

脚本会验证全部输入，只替换目标 box 的 `OpenFilePath` 规则，为 `Sandboxie.ini` 创建相邻备份，验证保存结果并重启 `SbieSvc`。如果验证或服务重启失败，它会恢复备份。

脚本会重启共享的 Sandboxie 服务，因此必须先停止所有 box。

### 第八步：验证状态

```powershell
& $Hub --json iso-status
& $Hub --json iso-writethrough $ProfileId
& $Hub status $ProfileId
```

预期结果：

- `backendAvailable=true`；
- `boxExists=true`；
- `keyringClaimed=true`；
- `ready=true`；
- `missingPaths=[]`；
- GUI 的 Isolation 列显示 `Ready`。

`iso-status` 适合检查 box、keyring 和托管 Profile 目录；`iso-writethrough <id>` 是检查当前 Profile 及其已登记 Workspace 完整路径集合的权威命令。

### 第九步：启动并在官方窗口登录

```powershell
& $Hub launch $ProfileId
```

只在弹出的官方 Antigravity 窗口中登录。不要把密码、OAuth code、Cookie、token 或包含这些信息的截图交给 Hub、脚本、Issue 或日志。

为其他 Profile 重复以上步骤。全部 Profile 准备完成后再使用 `launch-all`。

### 第十步：做一次真实直写验收

`ready=true` 只代表规则存在。首次部署后还应验证行为：

1. 在已登记 Workspace 内准备一个可丢弃的测试目录。
2. 从该 Profile 的 Antigravity 窗口创建或修改一个无害标记文件。
3. 用宿主文件资源管理器或普通编辑器确认变化立即可见。
4. 重启该 Profile，确认文件仍然存在。
5. 不要让两个 Profile 同时编辑同一个仓库；独立 Git worktree 仍是后续能力。

更完整的人工验收清单见 [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md)。

## GUI 使用教程

从开始菜单、桌面快捷方式或 `Start Hub.cmd` 打开 GUI。

### 顶部操作

| 按钮 | 作用 |
|---|---|
| **Create profile** | 创建 Profile；名称为 1–80 个可打印字符 |
| **Launch all** | 启动全部 Profile；多 Profile 时要求每个 Profile 都有可用的隔离盒 |
| **Stop all** | 停止全部 Profile |
| **Force stop all** | 强制停止全部 Profile；使用前先保存工作 |
| **Refresh** | 重新读取 Profile、进程和隔离状态 |
| **Doctor** | 检查程序路径、兼容版本、隔离能力和风险警告 |
| **Settings** | 手动选择 `Antigravity.exe` |
| **Open data** | 打开 Hub 数据根目录 |
| **Open logs** | 打开 Hub 结构化日志目录 |

当存在多个 Profile 且至少一个还没有隔离映射时，**Launch all** 会被禁用。即使按钮可用，也应先确保每个 Profile 的直写状态为 `Ready`。

### Profile 行操作

| 按钮 | 作用与注意事项 |
|---|---|
| **Launch** | 启动当前 Profile；有隔离映射时自动走专属 box |
| **Stop** | 停止当前 Profile；隔离 Profile 会终止专属 box，先保存工作 |
| **Force stop** | Direct Profile 的显式强制停止；隔离 Profile 同样按专属 box 停止 |
| **Restart** | 停止后重新启动 |
| **Force restart** | Direct Profile 在普通关闭失败时强制重启 |
| **Prepare isolation** | 创建或登记隔离盒；首次创建可能提示管理员脚本 |
| **Write-through** | 检查缺失的直写路径，并提示需要运行的 CLI 命令 |
| **Workspace** | 登记或清除 Workspace 元数据 |
| **Rename** | 修改显示名称，不改变 UUID |
| **Data** | 打开该 Profile 的托管数据目录 |
| **Shortcut** | 在桌面创建绑定 UUID 和当前数据根的启动快捷方式 |
| **Remove** | 只取消登记；Profile 数据和 Sandboxie box 会保留 |

Isolation 列含义：

- `Direct / serial`：未准备隔离盒，只能串行；
- `Write missing`：已有隔离映射，但缺少一个或多个直写规则；
- `Ready`：当前 Profile 和已登记 Workspace 的直写规则完整。

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

### Profile 管理

```powershell
& $Hub create "Pro A"
& $Hub create "Pro B" --workspace "D:\Projects\ProB"
& $Hub list
& $Hub show <profile-id>
& $Hub rename <profile-id> "新的显示名称"
& $Hub workspace <profile-id> "D:\Projects\Example"
& $Hub workspace <profile-id> none
& $Hub remove <profile-id>
```

注意：

- `<profile-id>` 必须替换成 `list` 输出的 UUID；
- Profile 名称不是命令身份，重命名不会影响 UUID；
- Workspace 目录必须存在；
- `remove` 前必须停止 Profile；
- `remove` 只移除登记，不删除磁盘数据或 Sandboxie box。

### 隔离管理

```powershell
& $Hub iso-prepare <profile-id>
& $Hub iso-prepare <profile-id> --fresh
& $Hub iso-status
& $Hub iso-writethrough <profile-id>
```

`iso-launch` 和 `iso-stop` 是要求 Profile 已经存在隔离映射的显式命令：

```powershell
& $Hub iso-launch <profile-id>
& $Hub iso-stop <profile-id>
```

日常无需特意选择它们；普通 `launch`、`stop` 和 `restart` 会根据 Profile 映射自动选择隔离或 Direct 路由。

### 启动、状态和停止

```powershell
& $Hub launch <profile-id>
& $Hub launch-all
& $Hub status
& $Hub status <profile-id>
& $Hub restart <profile-id>
& $Hub stop <profile-id>
& $Hub stop-all
```

Direct Profile 普通关闭失败时，先保存工作，再显式使用：

```powershell
& $Hub stop <profile-id> --force
& $Hub restart <profile-id> --force
& $Hub stop-all --force
```

对隔离 Profile，停止操作会终止该 Profile 的整个专属 box，不是文档保存协议。始终先在 Antigravity 中保存工作。

`launch-all` 会在启动任何 Profile 前先做整体预检：只要有一个 Profile 缺少映射、box 不存在、Sandboxie 不可用或 keyring 未声明，就不会启动任何一个。直写缺失目前不会阻止启动，但编辑可能只落在 sandbox 副本中，因此不建议继续工作。

### 文件夹与快捷方式

```powershell
& $Hub open-data
& $Hub open-data <profile-id>
& $Hub open-logs
& $Hub shortcut <profile-id>
& $Hub shortcut <profile-id> --directory "D:\Shortcuts"
```

自定义快捷方式目录必须是已经存在的绝对路径。Hub 不覆盖同名旧快捷方式；需要更新路径时，请删除旧快捷方式后重新创建。

### JSON 和独立数据根

`--json` 输出 camelCase JSON，适合 PowerShell 或其他脚本：

```powershell
$Profiles = & $Hub --json list | ConvertFrom-Json
$Status = & $Hub --json status <profile-id> | ConvertFrom-Json
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
| `3` | Profile 不存在 |
| `4` | 当前能力或版本不支持 |
| `5` | 无法安全确认进程所有权 |
| `130` | 用户取消 |

内部命令 `credential-shim` 不属于日常 CLI 接口，不要手动调用。

## 日常使用与 Workspace

推荐的日常流程：

1. 打开 Hub，点击 **Refresh**，确认目标 Profile 为 `Ready`。
2. 点击 **Launch**，或使用该 Profile 的桌面快捷方式。
3. 多个 Profile 都已准备完成时，可以使用 **Launch all**。
4. 在官方 Antigravity 窗口内工作和登录。
5. 停止或重启前保存全部文件。
6. 使用 Hub 的 **Stop** / **Restart**，不要按进程名批量结束 `Antigravity.exe`。

快捷方式绑定 Profile 的不可变 UUID、创建时使用的数据根以及当时包内 CLI 的绝对路径。安装版升级路径固定，旧快捷方式通常可继续使用；便携版移动目录后需要重建。

### Workspace 的真实含义

`workspace` 当前只完成两件事：

1. 在 Profile 中保存默认工作区元数据；
2. 将该目录加入 Sandboxie 直写检查。

它**不会**自动让 Antigravity Desktop 在启动时打开该项目。当前没有发现 Desktop 2.12.2 可验证的 workspace 转发参数，因此：

- 启动 Profile 后仍需在官方应用内手动打开项目；
- `launch <id> --workspace <path>` 会明确返回“不支持”，不会假装成功；
- 新增、修改或清除 Workspace 后，应重新运行 `iso-writethrough <id>`；
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
| `isolation\state.json` | Profile 与 box 的映射，不包含 token |
| `runtime\` | 进程状态、锁和运行期记录 |
| `logs\` | Hub 白名单结构化日志 |
| `global-launch-state\` | 所有 Hub 数据根共享的启动协调状态 |

### 备份

Hub 当前没有正式的 Profile 导出/导入功能。如果需要本机灾备：

1. 在所有 Antigravity 窗口保存工作。
2. 通过 Hub 停止全部 Profile。
3. 确认没有 Hub 写入操作。
4. 复制整个 `%LOCALAPPDATA%\AntigravityProfileHub` 到受保护的位置。

该目录可能包含官方应用生成的本地状态，应像其他个人应用数据一样保护，不要上传到公开仓库或 Issue。

Hub 数据备份不等于完整备份 Sandboxie：box 定义、box 容器和登录状态由 Sandboxie 管理。当前版本不提供受支持的跨机器迁移流程；换机时应预期重新创建隔离盒并通过官方页面重新登录，而不是复制或导出认证秘密。

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
6. 重新运行 `doctor`、`iso-status` 和每个 Profile 的 `iso-writethrough`。
7. 如果 Antigravity 或 Sandboxie 也升级过，重新完成多账号和 Workspace 人工验收。

### 便携版升级

1. 停止全部 Profile 并退出旧版 Hub。
2. 把新版 ZIP 解压到新的完整目录，不要只覆盖个别 EXE。
3. 运行新版 `Start Hub.cmd`。
4. 默认数据位于 `%LOCALAPPDATA%`，会被新版继续读取。
5. 如果目录位置变化，重新创建桌面快捷方式。

### 卸载

在 Windows“已安装的应用”中卸载 **Antigravity Profile Hub**。卸载会删除安装目录中的程序文件，但会保留：

- `%LOCALAPPDATA%\AntigravityProfileHub` 中的 Profile 数据；
- Sandboxie box 和其容器；
- 外部 Workspace；
- 用户自己创建的项目文件。

重新安装后通常可以继续看到原有 Profile。当前版本没有“彻底清理所有数据”按钮；如需彻底删除，请先备份并分别在 Hub 与 Sandboxie 层确认目标，避免误删项目或仍需保留的 box。

## 故障排查

| 现象或错误 | 原因 | 处理 |
|---|---|---|
| `executable_not_found` | 没找到官方 Antigravity | GUI 点击 **Settings**，或运行 `configure-executable <绝对路径>` |
| `version_unverified` | Antigravity 不是 2.12.2 | 不要绕过；恢复到已验证版本，或完成新版本适配与验收 |
| `sandbox_not_found` | Sandboxie 未安装在受支持目录 | 确认 `Start.exe` 和 `SbieIni.exe` 位于标准安装目录 |
| `sandbox_create_failed` / 拒绝访问 | 普通用户不能写系统级 Sandboxie 配置 | 从管理员 PowerShell 运行 `New-IsolationBox.ps1` |
| 提示既有 box 需要确认 | Hub 不会静默接管未登记 box | 只有确认从未登录过时才运行 `iso-prepare <id> --fresh` |
| `isolation_not_prepared` | Profile 没有隔离映射 | 完成 `iso-prepare` 流程 |
| GUI 显示 `Write missing` | 缺少 `OpenFilePath` | 运行 `iso-writethrough <id>`，停止所有 box，再执行输出的管理员命令 |
| 文件只存在于 sandbox 副本 | Workspace 或数据目录未直写 | 先保存并保留 box，不要清空；用 Sandboxie 检查盒内文件，再补齐直写并做行为验收 |
| `launch-all` 被禁用或失败 | 至少一个 Profile 的映射、box、Sandboxie 或 keyring 未就绪 | 运行 `iso-status`、`status` 和 `iso-writethrough` 逐个修复 |
| 提示另一个 Antigravity root 正在运行 | 正在使用 Direct / serial 路径 | 停止另一个 Direct 实例，或为所有并发 Profile 准备隔离盒 |
| `graceful_close_pending` | 普通关闭超时 | 检查未保存工作，再使用 `--force` |
| `profile_not_stopped` | 运行中的 Profile 不能注销 | 先运行 `stop <id>` 并确认状态 |
| `cli_not_found` | 便携包目录被拆散 | 恢复完整 `hub`、`cli`、`scripts` 相对结构 |
| 找不到 `scripts\...ps1` | 当前 PowerShell 不在包根目录 | 先 `Set-Location` 到安装或便携根目录 |
| SmartScreen 显示未知发布者 | 当前 EXE 未签名 | 先核对 Release 来源和 SHA-256，只运行信任的构建 |
| Workspace 没有自动打开 | 当前只保存元数据 | 启动后在官方 Antigravity 中手动打开项目 |
| Doctor 报 retained global launch barrier | 上一次启动的进程状态不确定 | 不要手删状态文件；关闭残留实例、再次运行 `status` / `doctor`，仍失败时保留日志排查 |

排查时建议依次运行：

```powershell
& $Hub doctor
& $Hub list
& $Hub status
& $Hub iso-status
& $Hub open-logs
```

报告问题时只提供错误码、非敏感日志和复现步骤。不要提交密码、授权码、token、Cookie、Credential Manager 内容或账号隐私截图。

## 从源码构建和发布

### 开发环境

- Windows x64；
- PowerShell 7；
- .NET 10 SDK；
- 构建安装器时额外需要 Inno Setup 6；
- 运行真实集成测试时需要兼容版本的 Antigravity Desktop 和 Sandboxie Plus。

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
- 标签必须与 `Directory.Build.props` 中的版本完全一致，例如版本 `0.2.3` 对应标签 `v0.2.3`；
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

- Hub 只管理本地 Profile、路径、进程和隔离盒映射。
- Google 登录、退出和授权由官方 Antigravity 完成。
- Hub 不保存 Google 密码，不导出 token，不复制 Cookie，不代理认证。
- Hub 不修改服务端认证、配额或官方程序二进制。
- Profile box 用于解决同一 Windows 用户下的应用状态和认证冲突，不是 Windows 用户之间的安全边界。
- `OpenFilePath` 会授予 box 对指定宿主目录的直接写权限，因此配置需要显式管理员操作。
- `remove` 只注销登记；不会清理 Profile、Workspace 或 sandbox。
- 停止隔离 Profile 会终止专属 box 中的进程，操作前必须保存工作。
- Antigravity 或 Sandboxie 更新后，旧验收结论自动失效，必须重新验证。

进一步阅读：

- [SECURITY.md](SECURITY.md)：完整安全模型；
- [KEYRING_ISOLATION_REPORT.md](KEYRING_ISOLATION_REPORT.md)：根因、失败方案和通过证据；
- [MANUAL_TEST_PLAN.md](MANUAL_TEST_PLAN.md)：人工验收步骤；
- [PROJECT_STATE.md](PROJECT_STATE.md)：当前实现状态与剩余门槛；
- [ARCHITECTURE.md](ARCHITECTURE.md)：架构说明；
- [CHANGELOG.md](CHANGELOG.md)：版本变化。
