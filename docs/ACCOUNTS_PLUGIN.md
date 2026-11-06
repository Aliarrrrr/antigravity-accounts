# Antigravity Accounts · 0.4.0-preview.10

Windows x64 账号插件：将独立授权的 Google 账号分组放入 Antigravity 原生模型列表，保留官方模型选项、主题和 View Usage。各账号分别使用自己的额度；插件不合并或转移额度。模型请求仍由原生语言服务直接发往 Google。

当前支持已检查的 Antigravity 2.29.0 和 2.28.2 程序。需要完整分发包与本机宿主适配；仅安装 `plugin.json` 不会启用切换。当前未在 Marketplace 上架，也不保证未知版本兼容。运行检查范围见包根目录 `VALIDATION.md`。

## 安装

1. 解压完整 ZIP，保留其中目录结构。
2. 正常退出 Antigravity。启用“常驻系统托盘”时，请从托盘选择“退出”；仅关闭窗口不会退出进程。
3. 已安装旧版 Accounts 时，先运行旧分发包的 `Restore.cmd`；账号授权会保留。若同时有中文适配，先恢复中文层，再恢复旧 Accounts。
4. 运行新版 `Install-Preview.cmd`。安装器检查当前程序，保留原始 `app.asar` 和 `language_server.exe` 后写入适配。桌面、语言服务、更新器或无法识别的相关进程仍在运行时，安装会停止。
5. 如需中文界面，重新应用中文层。运行 `Start-With-Accounts.cmd`，启动并打开原生模型菜单。

无需另装 .NET、Node、Python 或 PowerShell 7。安装辅助脚本使用本机官方 Electron 的 Node 模式。自定义安装位置时，将 `ANTIGRAVITY_INSTALL_ROOT` 设置为包含 `Antigravity.exe` 的目录。`Prepare.cmd` 只生成本机程序副本，不应用到安装目录。

## 添加和切换账号

在原生模型菜单中打开“账号设置与管理…”，输入可选标签并点击“添加账号”，亲自在 Google 网页完成授权。在同一面板设定唯一主账号，其余账号作为附属账号。添加、删除、标签编辑、重新授权、主账号配置和数据使用设置都位于该面板；模型列表保留切换和管理入口。未配置独立账号时，应用继续使用原官方登录。

首次启用独立主账号和所选账号后，需要完全退出并重新启动 Antigravity。之后点击其他账号分组即可请求切换，无需重启 App 或语言服务。切换会暂停新的提交，等待原生任务结束，再确认目标身份、项目、模型和额度元数据。取消等待只取消切换，不取消原有任务。

切换前会保存原模型。新账号仍可用时保留该模型；不可用时，须在原生列表明确选择可用模型后才能发送。确认失败或任务状态无法读取时，新的发送保持暂停。查明原因后可在目标分组选择“重新确认此账号”。托盘“额度账号…”打开同一模型菜单。

在管理面板中可修改标签、重新授权及设置数据使用授权。默认对账号切换后出现的 Security Notice & Data Use 关闭收集选项，待原生保存成功后确认；可改为勾选并确认，或每次手动处理。首次使用、条款类型变化、无法识别及读取或保存失败时保留原生弹窗。preview.10 修复了设置已保存却因读取 React 旧状态而未确认的问题。出现 `Verify your account to continue.` 时，请在 Google 官方流程处理账号验证，再重新确认；重新授权不能替代这项验证。参见 [Google 账号验证说明](https://support.google.com/accounts/answer/15437069)。

## 删除账号

在“账号设置与管理”中点击附属账号的“删除账号”，核对确认行中的邮箱，再点击“确认删除”。这会移除本机插件保存的授权，再次使用需重新添加；不删除 Google 账号。正在使用的账号须先切换到其他账号，主账号不能在运行中删除。授权或切换尚未结束时，删除入口不可用。删除其他附属账号不改变当前选择及其余账号。

## 授权与本机数据

- 账号通过 `accounts.google.com` 授权，`oauth2.googleapis.com` 交换或续期，`openidconnect.googleapis.com/v1/userinfo` 查询独立账号身份。模型及项目/额度元数据由原生服务向 Google 请求。
- OAuth 桌面客户端配置取自匹配的本机语言服务公开常量，不读取官方保存的登录。当前授权范围为 `openid`，以及 `https://www.googleapis.com/auth/` 下的 `cloud-platform`、`userinfo.email`、`userinfo.profile`、`cclog`、`experimentsandconfigs`。实际许可由 Google 决定。
- 账号默认存于 `%LOCALAPPDATA%/AntigravityAccounts`，使用 Windows DPAPI CurrentUser 加密。安装器登记实际目录，组件、启用标记和修复均使用该位置，避免不同启动环境读取空目录。
- 组件和宿主通过子进程 stdio 通信；界面只接收账号摘要和状态，不接收 token。独立账号凭据交给原生语言服务使用，原官方账号的 Authorization 不经过插件。
- 不导入 Cookie、原生认证数据库或 Windows 凭据管理器数据，不复制项目和会话。启用独立路径后，组件故障不会悄悄退回原官方身份。

本项目不代表 Google。采用本机官方桌面客户端公开 OAuth 配置的第三方分发方式仍待 Google 确认；相关申请不等于批准。

## 更新与恢复

使用 `Start-With-Accounts.cmd` 可在启动前检查并修复已支持的更新。安装会把独立修复文件缓存到账号状态目录的 `recovery-package-*`，因此原解压目录移除后仍可修复。版本号变化而相关完整脚本、RPC 依赖和原生程序仍兼容时可以继续使用；未知接口或原生程序变化会停止修改，等待新版插件。

应用内“重启安装更新”会先准备外部等待进程，待旧 App、安装器退出且文件停止变化后，调用登记的修复与启动入口。真实 NSIS 升级尚未验证。直接双击更新后的官方 EXE 会绕过启动前检查。

恢复时正常退出 Antigravity，再运行 `Restore.cmd`。安装收据为账号状态目录中的 `adapter-installation.json`，原程序及恢复清单位于同目录的 `restore-*`。恢复会移除插件运行文件并保留主动添加的账号资料。当前程序与收据不匹配时会拒绝覆盖；不要删除恢复文件后尝试用旧程序覆盖新版本。

若叠加外部中文适配，手动升级或恢复 Accounts 前须先恢复中文层，完成 Accounts 操作后再应用中文层。支持的中文启动入口可协调启动修复；中文工具本身不在本包中。

## 源码、支持和许可

[项目与问题反馈](https://github.com/Aliarrrrr/antigravity-accounts) · [当前版本](https://github.com/Aliarrrrr/antigravity-accounts/releases/tag/accounts-v0.4.0-preview.10) · [源码构建说明](https://github.com/Aliarrrrr/antigravity-accounts#development)

MIT 许可，详见包内 `LICENSE`。preview.10 已在安装的 2.29.0 上观察到切回原账号时自动取消勾选并关闭数据使用弹窗，再次普通重启后正常加载；代理模型请求为 0。2.28.2 的真实账号切换，以及真实生成、实际扣额、到期续期和跨账号继续生成仍未验证。反馈时请提供版本、操作步骤和错误代码，勿附凭据、账号数据文件或未经处理的个人日志。
