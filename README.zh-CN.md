# Antigravity Accounts

[English](README.md) · [简体中文](README.zh-CN.md)

**在 Antigravity 原生模型列表中使用已授权的 Google 账号。**

![Preview 0.4.0-preview.8](https://img.shields.io/badge/preview-0.4.0--preview.8-8b5cf6)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
[![MIT License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Antigravity Accounts 在现有模型菜单中加入账号分组，保留官方模型选项、主题和 View Usage。配置一个主账号后，可在同一窗口选择其他已授权账号。每个账号分别使用自己的额度，并受各自的服务资格限制。

这是通过 ZIP 分发的 **Windows 非官方宿主适配预览版**，尚未在 Antigravity 插件市场收录。单独安装 `plugin.json` 不会启用账号切换。

[下载 preview.8](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.8) · [安装说明](docs/ACCOUNTS_PLUGIN.md) · [更新日志](CHANGELOG.md) · [检查记录](docs/ACCOUNTS_VALIDATION.md)

## 安装

| 项目 | 支持范围 |
| --- | --- |
| 操作系统 | Windows x64 |
| Antigravity App | 已检查的 2.29.0、2.28.2 程序组合 |
| 额外运行时 | 分发包无需另装运行时；.NET 组件自包含，安装辅助程序使用已安装的 Electron 运行时 |

1. 下载并完整解压 [AntigravityAccounts-0.4.0-preview.8-win-x64.zip](https://github.com/Aliarrrrr/antigravity-accounts/releases/download/accounts-v0.4.0-preview.8/AntigravityAccounts-0.4.0-preview.8-win-x64.zip)。
2. 完全退出 Antigravity。若应用常驻系统托盘，请从托盘菜单选择“退出”。
3. 在解压后的 `AntigravityAccounts` 目录运行 `Install-Preview.cmd`，然后正常启动 Antigravity。

安装器会检查已安装的程序、保存原始文件，再应用本机宿主适配，不修改 `Antigravity.exe`。请保留完整分发目录及校验文件，以便恢复。

已应用中文适配时，操作顺序为：**恢复中文层 → 安装 Accounts → 重新应用中文层**。恢复 Accounts 时也采用相同顺序。详见[安装说明](docs/ACCOUNTS_PLUGIN.md#安装)。

自定义安装目录时，在运行安装器前将 `ANTIGRAVITY_INSTALL_ROOT` 设为包含 `Antigravity.exe` 的目录。仓库当前为私有仓库，下载与提交问题需要相应访问权限。

## 功能

- **原生模型菜单：** 账号分组与官方模型选项显示在同一列表，沿用宿主样式和键盘导航。
- **同一窗口切换账号：** 后续切换无需重启 App 或语言服务；提交切换前，先等待生成、工具和后台任务结束。
- **保护模型选择：** 原有具体模型可用时保留；不可用时，等待用户在原生列表中明确选择可用模型后再允许发送。
- **列表内管理账号：** 通过 Google 官方授权页面添加账号；删除非当前附属账号前，在同组核对邮箱并确认。
- **更新后恢复：** 缓存适配文件，通过已支持的启动入口在兼容程序替换后重新应用适配。

## 使用

1. 打开模型列表，点击“添加账号”，在 Google 官方页面完成授权。
2. 设定唯一“主账号”。首次配置后，完全退出并重启一次 Antigravity，启用独立账号路径。
3. 点击模型列表中的另一个账号。插件等待原生工作空闲、提交所选账号并确认账号元数据。
4. 继续使用官方模型选项。原模型在新账号中不可用时，请先选择可用模型再发送。

确认完成后，当前组显示“正在使用”。取消等待不会取消原任务；确认失败时暂停新的发送，并提供“重新确认此账号”操作。

删除非当前附属账号时，点击其右侧“删除”，核对邮箱后点击“确认删除”。当前账号需先切换到其他账号；主账号不能在运行中删除。此操作仅删除插件中的本机账号记录，不删除 Google 账号。

Google 的安全、资格和数据使用提示需要由账号所有者在官方界面亲自处理。

## 兼容与更新

兼容检查依据已检查的宿主脚本、RPC 依赖和原生程序组合。桌面版本号变化后，这些已检查程序内容仍匹配时可继续使用；未知的原生程序或相关源码变化会被拒绝。

兼容程序被替换后，先运行 `Start-With-Accounts.cmd` 再启动。已安装中文启动保护时，其启动器和后台监听先恢复 Accounts，再应用中文。应用内更新接力会准备外部辅助进程，等待 App 与安装器退出后修复并重新启动。

未知更新后直接打开 EXE 可能绕过这些启动检查。请保留原始文件，按[恢复说明](docs/ACCOUNTS_PLUGIN.md#更新与恢复)操作，不要用旧程序备份覆盖新版官方安装。

### 已检查的范围

已安装的 Antigravity 2.29.0 完成了仅元数据的 **账号 A → 账号 B → 账号 A** 往返。App 与语言服务进程、窗口、工作区和可用的具体模型均保留，代理模型请求为 **0**。

真实生成、额度扣减、跨账号继续生成和真实 NSIS 升级尚未验证。不同版本的具体结果见[检查记录](docs/ACCOUNTS_VALIDATION.md)。

## 卸载与恢复

完全退出 Antigravity 后运行：

```powershell
.\Restore.cmd
```

该操作恢复保存的官方程序并移除适配运行文件，保留账号记录供以后重新安装使用。需要移除某个账号记录时，请先使用模型菜单中的删除功能。请保留已登记 Accounts 状态目录中的 `restore-*` 文件夹。

## 数据与授权

- 账号通过 Google 官方 OAuth 添加，凭据以 **Windows DPAPI CurrentUser** 加密保存。
- 安装器登记账号目录的实际位置，组件、启用标记和更新恢复使用同一路径，避免不同启动进程的 LocalAppData 映射指向不同目录。
- 原生语言服务直接向 Google 发送模型请求。插件不代理推理请求，也不导入官方登录数据库、Cookie 或 Windows Credential Manager 内容。
- 分发 ZIP 包含独立组件和适配源码，不包含个人账号资料或官方 Antigravity 程序二进制。

授权端点、作用域和凭据流向见[授权与数据](docs/ACCOUNTS_PLUGIN.md#授权与本机数据)。敏感问题报告方式见 [SECURITY.md](SECURITY.md)。

## 开发

构建需要 **PowerShell 7**、**.NET SDK 10.0.400** 和 **Node.js 24**。`global.json` 固定 SDK，构建脚本不会安装工具链。

```powershell
.\scripts\Build.ps1 -LockedRestore -Test
.\scripts\Publish-Accounts.ps1 -DotNet (Get-Command dotnet).Source
```

JavaScript 检查：

```powershell
npm ci --prefix packages/hostauth-adapter --ignore-scripts --no-audit --no-fund
node --test packages/account-plugin/test/*.test.cjs packages/account-plugin/test/*.test.mjs packages/hostauth-adapter/test/*.test.mjs
```

打包命令生成 ZIP、JSON 校验文件和 `SHA256SUMS.txt`。正式发布时增加 `-Release`，要求工作目录没有未提交内容，且当前提交带有 `accounts-v<version>` 标签。`scripts/Test-AccountsPackage.ps1 -PackageRoot <解压后的包目录>` 用于检查包内文件清单及内容。

原生程序相关检查需要通过 `ANTIGRAVITY_PROGRAM_ASAR` 和 `ANTIGRAVITY_LANGUAGE_SERVER` 提供未修改的程序副本，安装包相关检查还使用 `ACCOUNTS_TEST_INSTALL`。缺少输入时可能跳过相关项目，请查看最终测试汇总。不要提交或分发这些官方程序副本和本机账号文件。

| 路径 | 内容 |
| --- | --- |
| `src/Antigravity.Accounts` | OAuth、加密账号存储和 stdio 组件 |
| `packages/account-plugin` | 安装、恢复和插件元数据 |
| `packages/hostauth-adapter` | 原生菜单、HostAuth 接入和切换协调 |
| `tests/Antigravity.Accounts.Tests` | 使用合成账号的组件检查 |
| `scripts` | 构建、检查和打包命令 |
| `docs` | 安装与检查记录 |

早期 Profile Hub 应用保留在 Git 历史中，当前项目与分发包为 Antigravity Accounts。参见[项目历史](docs/HISTORY.md)。

## 支持与贡献

安装或兼容性问题通过 [Issues](https://github.com/Aliarrrrr/antigravity-accounts/issues)反馈，代码改动通过 Pull Request 提交。请提供 Antigravity 版本、插件版本、复现步骤及限定格式的错误代码，不附带账号文件、回调链接、凭据或会话内容。

代码改动请运行相关组件或 JavaScript 检查，并说明实际检查过的客户端行为。维护者：[Aliarrrrr](https://github.com/Aliarrrrr)。

## 许可证

[MIT](LICENSE)。
