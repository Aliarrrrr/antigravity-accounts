# Antigravity Accounts

[English](README.md) · [简体中文](README.zh-CN.md)

**0.4.0-preview.18** 增加[汉化与 Accounts 成套更新](docs/COMPATIBILITY_UPDATES.md)。已安装两者时可使用[成套兼容包](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.18)：从固定仓库校验并准备新引擎，正常退出后应用；未知原生程序不会被强行修改。独立 Accounts ZIP 继续提供。

本机 2.32.1 已完成两个已保存账号 A → B → A 切换，项目和模型选择保留。本轮未发送模型请求，不代表已验证实际扣额或跨账号生成；详见[检查记录](docs/ACCOUNTS_VALIDATION.md)。

包含 Antigravity **2.32.1** 兼容修复和 BaseWindow/WebContentsView 账号切换修正。

**在 Antigravity 原生模型列表中使用已授权的 Google 账号。**

![Preview 0.4.0-preview.18](https://img.shields.io/badge/preview-0.4.0--preview.17-8b5cf6)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
[![MIT License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Antigravity Accounts 在现有模型菜单中加入账号分组，保留官方模型选项、主题和 View Usage。配置一个主账号后，可在同一窗口选择其他已授权账号。每个账号分别使用自己的额度，并受各自的服务资格限制。

这是通过 ZIP 分发的 **Windows 非官方宿主适配预览版**，尚未在 Antigravity 插件市场收录。单独安装 `plugin.json` 不会启用账号切换。

[下载 preview.17](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.18) · [安装说明](docs/ACCOUNTS_PLUGIN.md) · [更新日志](CHANGELOG.md) · [检查记录](docs/ACCOUNTS_VALIDATION.md)

## 安装

| 项目 | 支持范围 |
| --- | --- |
| 操作系统 | Windows x64 |
| Antigravity App | 已检查的 2.32.1、2.31.2、2.31.0、2.29.0、2.28.2 程序组合 |
| 额外运行时 | 分发包无需另装运行时；.NET 组件自包含，安装辅助程序使用已安装的 Electron 运行时 |

1. 下载并完整解压 [AntigravityAccounts-0.4.0-preview.18-win-x64.zip](https://github.com/Aliarrrrr/antigravity-accounts/releases/download/accounts-v0.4.0-preview.18/AntigravityAccounts-0.4.0-preview.18-win-x64.zip)。
2. 完全退出 Antigravity。若应用常驻系统托盘，请从托盘菜单选择“退出”。
3. 在解压后的 `AntigravityAccounts` 目录运行 `Install-Preview.cmd`，然后正常启动 Antigravity。

安装器会检查已安装的程序、保存原始文件，再应用本机宿主适配，不修改 `Antigravity.exe`。请保留完整分发目录及校验文件，以便恢复。

仅使用独立 Accounts ZIP、宿主版本未变时，先退出 Antigravity，运行**新解压 preview.18 包**中的 `Restore.cmd`，再运行同包的 `Install-Preview.cmd`。宿主已被官方更新替换为上表支持的新原程序时，直接运行新版 `Install-Preview.cmd`，安装器会保存旧记录并对当前程序重新准备适配。已保存的账号授权会保留。已安装汉化与 Accounts 的用户可改用[成套兼容包](docs/COMPATIBILITY_UPDATES.md)，统一准备两层并保留原状态目录。旧入口单独操作 Accounts 时仍需先恢复中文层、完成后再应用中文层。详见[安装说明](docs/ACCOUNTS_PLUGIN.md#安装)。

自定义安装目录时，在运行安装器前将 `ANTIGRAVITY_INSTALL_ROOT` 设为包含 `Antigravity.exe` 的目录。

## 功能

- **原生模型菜单：** 紧凑账号行将名称与状态分栏显示，使用独立勾选、等待、展开与设置图标，保留官方模型选项和键盘导航。
- **同一窗口切换账号：** 后续切换无需重启 App 或语言服务；提交切换前，先等待生成、工具和后台任务结束。
- **保护模型选择：** 原有具体模型可用时保留；不可用时，等待用户在原生列表中明确选择可用模型后再允许发送。
- **统一账号管理：** 同窗口面板显示账号状态标签，减少重复邮箱，统一控件与窄屏布局，提供添加、删除、标签编辑、重新授权、主账号配置和数据使用授权设置。
- **更新后恢复：** 缓存适配文件，通过已支持的启动入口在兼容程序替换后重新应用适配。

## 使用

管理操作集中在“账号设置…”面板；模型列表保留账号切换和这一个管理入口。

1. 打开模型列表，点击“账号设置…”，在面板中添加账号并在 Google 官方页面完成授权。
2. 在管理面板中设定唯一“主账号”。首次配置后，完全退出并重启一次 Antigravity，启用独立账号路径。
3. 点击模型列表中的另一个账号。插件等待原生工作空闲、提交所选账号并确认账号元数据。
4. 继续使用官方模型选项。原模型在新账号中不可用时，请先选择可用模型再发送。

确认完成后，当前组显示“正在使用”。取消等待不会取消原任务；确认失败时暂停新的发送，并提供“重新确认此账号”操作。

删除非当前附属账号时，在“账号设置”中点击“删除账号”，核对邮箱后点击“确认删除”。当前账号需先切换到其他账号；主账号不能在运行中删除。此操作仅删除插件中的本机账号记录，不删除 Google 账号。

面板还可修改标签、重新授权已有账号；等待 Google 授权时可在此取消本次尝试，已保存账号保持不变。“数据使用授权”提供三种选择：

| 选择 | 账号切换后的行为 |
| --- | --- |
| 自动确认，不勾选（默认） | 将数据收集保存为不勾选，原生保存成功后确认弹窗 |
| 自动确认，并勾选 | 将数据收集保存为勾选，原生保存成功后确认弹窗 |
| 每次手动处理 | 保留原生弹窗，由用户选择 |

首次条款、条款类型变化、无法识别、读取或保存失败时保留原生弹窗。Google 身份验证和服务资格提示仍需账号所有者亲自处理。preview.10 修复了已成功保存不勾选、却因读取 React 旧状态而没有确认的问题。

## 兼容与更新

preview.16 包含未单独发布的 preview.15 的 Antigravity 2.32.1 兼容修复，并修正账号切换的原生窗口查找。保留 2.31.2、2.31.0、2.29.0、2.28.2 的已有支持，以及 preview.14 的未知官方更新原生登录确认入口。preview.12、preview.13 和 preview.15 均为未单独发布的本地版本。SSH 远程会话不在独立账号支持范围内。各次检查范围见检查文档。

兼容检查依据已检查的宿主脚本、RPC 依赖和原生程序组合，未知程序不会写入补丁。完整官方更新暂时无法适配时，启动器检查当前文件后提供原生登录选项，并提醒其身份可能与之前的额度账号不同。原账号授权保留，后续兼容包可继续恢复账号功能。部分替换、文件损坏、记录冲突及安装器仍在运行时继续阻止启动。

兼容程序被替换后，先运行 `Start-With-Accounts.cmd` 再启动。已安装中文启动保护时，其启动器和后台监听先恢复 Accounts，再应用中文。应用内更新接力会准备外部辅助进程，等待 App 与安装器退出后修复并重新启动。

更新交接准备失败时，官方安装仍会继续，安装完成后保持关闭。从原有中文快捷方式打开，再确认是否使用原生登录。

未知更新后直接打开 EXE 可能绕过这些启动检查。请保留原始文件，按[恢复说明](docs/ACCOUNTS_PLUGIN.md#更新与恢复)操作，不要用旧程序备份覆盖新版官方安装。

### 已检查的范围

本地 preview.15 已在 Antigravity 2.32.1 启动，中文、原生账号分组及管理面板可见，但该次检查没有切换已保存账号。后续切换失败暴露出适配器只查 BrowserWindow，而宿主使用 BaseWindow 与 WebContentsView。preview.16 修正这处窗口访问；最终测试与已安装客户端的切换结果记录在[检查文档](docs/ACCOUNTS_VALIDATION.md)。旧版生成结果不代表已确认 2.32.1 的生成或扣额行为。

2026 年 11 月 12 日，本机 preview.14 已安装到 Antigravity 2.31.2。已观察登记的中文启动器启动、原生账号分组和管理面板，两个已保存账号均在，默认数据使用选项为不勾选。这轮检查的账号切换和模型发送均为 0。中文界面由单独维护的外部汉化工具提供，Accounts 发行包不包含该工具。中文保护入口需要使用支持 preview.14 原生登录确认的新版启动器，旧版仍可能阻止未知更新启动。当前本机已更新并检查该启动器，本仓库未单独分发中文工具。

preview.11 界面版本已安装到 Antigravity 2.29.0 并保留中文；已观察原生菜单、管理面板、当前/主账号状态标签、邮箱去重、Escape 关闭及焦点环回到模型按钮。这轮界面检查新增模型发送、账号切换和编辑均为 **0**。深浅主题、390 × 620 窗口、长标签、Enter 保存、取消授权和 Tab 环绕另用合成账号的浏览器页面检查。

preview.8 在已安装的 Antigravity 2.29.0 上完成了仅元数据的 **账号 A → 账号 B → 账号 A** 往返，保留 App 与语言服务进程、窗口、工作区和可用的具体模型。

安装 preview.10 后，已在 2.29.0 上观察到切回原账号时数据使用选项自动取消勾选、弹窗随后关闭；原账号和模型保留，再次普通重启后正常加载。代理模型请求为 **0**。这不代表已验证 2.28.2 的真实账号切换。

11 月 7 日，在 preview.11 视觉调整之前，经授权使用开发包在同一会话完成了账号 A → B → A 的三次纯文本发送；窗口、项目、会话 ID、草稿、模型与进程身份保持，后两次回复正确回忆了只在首次发送时提供的标记。这只确认该短文本上下文场景，没有重做最终视觉版本的同类生成检查。实际扣额、附件、工具和子代理历史、长上下文、到期续期、切换时排队发送及真实 NSIS 升级仍未验证。详见[检查记录](docs/ACCOUNTS_VALIDATION.md)与[实现审计](docs/ACCOUNTS_AUDIT_2026-11-07.md)。

隔离原生服务已确认插件发现和 enabled 偏好保存可用；Accounts 的 HostAuth 生命周期尚未接入原生插件开关，见[接入可行性](docs/ACCOUNTS_EXTENSION_INTEGRATION.md)。

## 卸载与恢复

完全退出 Antigravity 后运行：

```powershell
.\Restore.cmd
```

该操作恢复保存的官方程序并移除适配运行文件，保留账号记录供以后重新安装使用。需要移除某个账号记录时，请先使用管理面板中的删除功能。请保留已登记 Accounts 状态目录中的 `restore-*` 文件夹。

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
