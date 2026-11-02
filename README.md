# Antigravity Accounts _(antigravity-profile-hub)_

在 Antigravity 原生模型列表中，按账号分组选择已授权的 Google 账号。

账号通过官方 Google 网页登录，模型菜单沿用宿主的主题、字体和模型选项。项目、会话和工具仍由官方 App 管理。仓库沿用 `antigravity-profile-hub` 名称，旧 Profile Hub 的源码与文档作为兼容实现保留，当前发布的是 Accounts 插件。

当前预览版为 **0.4.0-preview.7**，适配 **Windows x64 / Antigravity App 2.28.2**。它需要本机宿主适配，尚未在插件市场收录；`plugin.json` 不能单独提供全部功能。其他版本须重新检查程序内容，不能保证未来更新自动兼容。市场申请情况见[申请记录](docs/ACCOUNTS_MARKETPLACE_REQUEST.md)。

## 目录

- [安装](#安装)
- [使用](#使用)
- [更新与恢复](#更新与恢复)
- [安全与数据](#安全与数据)
- [开发](#开发)
- [维护者](#维护者)
- [参与贡献](#参与贡献)
- [许可证](#许可证)

## 安装

### 依赖

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows x64 |
| 官方客户端 | 已安装 Antigravity App 2.28.2，程序内容须通过安装器检查 |
| 运行时 | 分发包自包含，无需另装 .NET、Node、Python 或 PowerShell 7 |

1. 从 [0.4.0-preview.7 发布页](https://github.com/Aliarrrrr/antigravity-profile-hub/releases/tag/accounts-v0.4.0-preview.7)下载 `AntigravityAccounts-0.4.0-preview.7-win-x64.zip`，完整解压。
2. 完全退出 Antigravity；启用托盘常驻时，从托盘菜单选择“退出”。若已应用中文适配，先使用中文工具恢复该层。
3. 在解压后的 `AntigravityAccounts` 目录运行：

```powershell
.\Install-Preview.cmd
```

安装器检查官方版本和程序内容，保存原始程序后生成本机适配。安装完成后，按需重新应用中文适配，再正常启动 Antigravity。详细步骤见[安装说明](docs/ACCOUNTS_PLUGIN.md#安装与使用)。

仓库目前为私有仓库，下载需要相应 GitHub 访问权限。自定义安装目录时，在运行安装命令前设置 `ANTIGRAVITY_INSTALL_ROOT`，指向包含 `Antigravity.exe` 的目录。

## 使用

1. 打开官方模型选择列表，点击“添加账号”，在官方 Google 网页完成授权。已添加账号可继续使用。
2. 首次配置独立账号时，设定唯一主账号；其余账号作为附属账号。完全退出并重新启动一次，以启用独立账号路径。
3. 在模型列表中展开目标账号分组，选择该账号。插件等待原生任务结束并确认身份，再恢复提交；取消等待会保留原任务。
4. 继续使用官方模型选项。原模型在新账号中不可用时，需在原生列表明确选择可用模型后才能发送。

当前账号组显示官方模型选项，其他账号显示为折叠分组，保留“View Usage”。后续附属账号切换的实现不要求重启 App 或语言服务；主账号不能在运行中替换或删除。各账号额度分别使用。

### 命令行入口

分发包中的以下命令打开同一原生模型菜单：

```powershell
.\Start-With-Accounts.cmd
```

2.28.2 已检查启动、三个原生账号分组及主题显示；本版没有执行真实账号切换或模型请求。2.28.1 上的两次账号元数据切换属于旧版检查结果。实际生成、额度扣减、到期续期和跨账号继续生成仍未验证，详见[检查记录](docs/ACCOUNTS_VALIDATION.md)。

## 更新与恢复

官方更新后，须使用适配该版本的插件；版本或程序内容不匹配时，安装器会拒绝修改。当前没有自动重新应用适配的服务。

需要恢复官方程序时，完全退出 Antigravity，在原分发包目录运行：

```powershell
.\Restore.cmd
```

恢复会移除插件运行文件，保留已添加的账号资料。原始程序及安装记录保存在 `%LOCALAPPDATA%\AntigravityAccounts`，请保留其中的 `restore-*` 目录。叠加中文适配时，操作顺序为：恢复中文层 → 安装或恢复 Accounts → 重新应用中文。更多情况见[恢复与更新](docs/ACCOUNTS_PLUGIN.md#恢复与更新)。

## 安全与数据

- 独立账号通过官方 Google 网页主动授权，凭据使用 Windows DPAPI CurrentUser 加密，存于 `%LOCALAPPDATA%\AntigravityAccounts`。
- 原生语言服务直接请求 Google；插件不转发模型请求，也不读取或复制官方登录数据库、Cookie、项目或会话。
- 安装包不包含个人账号资料或官方程序二进制；宿主适配在安装时于本机生成。
- 授权端点、作用域及凭据流向见[授权与数据](docs/ACCOUNTS_PLUGIN.md#授权与数据)，问题报告方式见[安全说明](SECURITY.md)。

## 开发

构建需要 PowerShell 7 和 **.NET SDK 10.0.400**，JavaScript 检查需要 **Node.js 22 或更新版本**。`global.json` 固定 SDK，发布脚本不会自动安装工具链。

在仓库根目录执行以下命令；`dotnet` 须指向已安装的指定 SDK：

```powershell
dotnet restore .\src\Antigravity.Accounts\Antigravity.Accounts.csproj -r win-x64 -p:SelfContained=true --locked-mode
.\scripts\Publish-Accounts.ps1 -DotNet (Get-Command dotnet).Source
```

发布目录位于 `artifacts/accounts/0.4.0-preview.7/`，包含完整分发目录、ZIP 和校验文件。安装操作独立于源码构建，不会在发布时修改官方 App。

组件与 JavaScript 检查入口：

```powershell
dotnet run --project .\tests\Antigravity.Accounts.Tests -c Release
node --test packages/account-plugin/test/*.test.cjs packages/account-plugin/test/*.test.mjs packages/hostauth-adapter/test/*.test.mjs
```

原生程序相关测试需要未修改的 2.28.2 程序副本，通过 `ANTIGRAVITY_PROGRAM_ASAR` 和 `ANTIGRAVITY_LANGUAGE_SERVER` 指定；缺失时会跳过相关项目。不要把本地组件或程序副本检查当作真实账号生成及扣额结果。宿主接入说明见 [hostauth-adapter](packages/hostauth-adapter/README.md)。

| 路径 | 内容 |
| --- | --- |
| `src/Antigravity.Accounts` | OAuth、加密账号存储和 stdio 组件 |
| `packages/account-plugin` | 插件清单、官方授权配置读取和安装入口 |
| `packages/hostauth-adapter` | 原生账号分组、宿主接入与恢复 |
| `tests/Antigravity.Accounts.Tests` | 不访问真实账号的组件测试 |
| `scripts/Publish-Accounts.ps1` | Windows 自包含分发包构建 |
| `docs` | 安装、检查记录和历史实现说明 |

版本变化见[更新日志](CHANGELOG.md)。旧实现见 [Profile Hub](docs/LEGACY_PROFILE_HUB.md) 与[共享工作空间](docs/SHARED_WORKSPACES.md)，二者不随 Accounts 分发包发布。

## 维护者

[Aliarrrrr](https://github.com/Aliarrrrr)。

## 参与贡献

欢迎通过 [Issues](https://github.com/Aliarrrrr/antigravity-profile-hub/issues)提问、报告问题，通过 Pull Request 提交改动；当前仓库需要访问权限。报告兼容性问题时，请提供 Antigravity 版本、插件版本和具体错误代码，勿附带凭据或个人会话内容。代码改动请运行涉及的组件或 JavaScript 检查，并注明真实客户端操作的范围。

README 结构参考 [Standard Readme](https://github.com/RichardLitt/standard-readme/blob/main/spec.md)。

## 许可证

[MIT](LICENSE) © 2026 Aliarrrrr and antigravity-profile-hub contributors。
