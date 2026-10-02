# GitHub 多实例方案核查与核心重构决策

核查日期：2026-09-09。目标版本：Windows 11、Antigravity Desktop 2.12.2。

## 结论

没有找到同时满足以下条件的现成 GitHub 项目：同一 Windows 用户、同一个未修改的官方安装、不同 Google 账号、多个 Desktop 2.x 窗口并发、重启后保持身份，而且不使用认证代理或搬运 Token。

当前项目已经实现了 `ai-multi-instance` 一类工具的核心文件态技术：独立 Electron `--user-data-dir`、独立进程 HOME/APPDATA、独立根进程和安全生命周期管理。真实 A/B 验收仍显示两个并发窗口解析为后来登录的账号，因此缺口不是更多目录重定向，而是 Windows 凭据身份边界。

目前唯一方向正确的公开参考是 `Spielewoy/multi-cli` 的“每 Profile 一个本地 Windows 用户”。它在设计上隔离固定的 `gemini:antigravity` 凭据，但违反原始同用户约束，而且源码审计显示它尚未完成真实双账号验收，不能作为开箱即用依赖。

## Windows 边界

- [CredReadW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credreadw) 读取当前进程 token 的登录会话所关联的 credential set。
- [CREDENTIALW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw) 以 `Type + TargetName` 标识一项凭据。`CRED_PERSIST_LOCAL_MACHINE` 对同一用户之后的登录会话仍可见；另开同用户进程并不能形成稳定的持久凭据命名空间。
- [CreateProcessWithLogonW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithlogonw) 的 `LOGON_NETCREDENTIALS_ONLY` 保留同一个本地 token，只替换出站网络凭据，因此不能隔离本地 WinCred。
- [PasswordVault](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.passwordvault) 的 locker 限制面向 AppContainer；普通 Desktop 应用仍处于用户级边界。
- Electron 的 [`app.getPath('userData')`](https://www.electronjs.org/docs/latest/api/app#appgetpathname) 和 VS Code 的 [`--user-data-dir`](https://code.visualstudio.com/docs/configure/command-line) 定义的是文件态目录，不承诺 Credential Manager 命名空间。

因此，对一个未修改且使用固定持久 Generic Credential TargetName 的 Win32 应用，可支持的稳定隔离只有两类：应用自身使用不同 TargetName，或进程使用不同 Windows 用户身份。没有发现第三种受支持的 launcher 级重定向机制。

## 本机静态证据

只读取了明确指定的安装文件，没有启动 Antigravity、枚举 Credential Manager 或读取任何真实认证数据。可复现实证由 `scripts/Collect-AntigravityStaticEvidence.py` 生成；当前报告位于被 Git 忽略的 `artifacts/forensics-20260908/static-evidence.json`。

- `Antigravity.exe` 产品版本为 2.12.2；`language_server.exe` SHA-256 为 `424ED4EF6FC9CB470CE785D8F4B392DE3FA94FD16D030E6FF83E043440659F94`。
- 打包入口把语言服务器 `--app_data_dir` 的值取自 `getAppDataDirName()`；正式包的该值由 Electron 应用名计算为 `antigravity`。
- 打包入口同时把文件目录放在进程 HOME 下的 `.gemini/antigravity`，并把 Electron UI 状态放在 `app.getPath('userData')`。当前 Hub 已能改变这两类文件路径。
- 语言服务器二进制包含 `wincred`、`CredReadW`、`CredWriteW`、`CredDeleteW`、`keyring` 以及 `gemini/antigravity` 静态标记。
- 已核对的打包启动参数包括 `--app_data_dir`、端口、CSRF、host bridge、IDE 名称和端点覆盖；没有发现 Credential Manager TargetName、service 或 username 覆盖参数，也没有从环境或配置覆盖应用名的调用链。

“固定坐标”由本机二进制、多个账号管理器实现和运行失败共同支持，证据强度高。“不存在任何未公开覆盖入口”无法由闭源静态分析绝对证明，当前证据强度为中高；应用升级后必须重新核查。

## GitHub 候选矩阵

| 项目 | 已核对机制 | 是否解决 Desktop 2.x | 结论 |
|---|---|---|---|
| [Spielewoy/multi-cli](https://github.com/Spielewoy/multi-cli) | 每 Profile 建本地用户，以 `Start-Process -Credential -LoadUserProfile` 启动 | 原理上可以 | 最佳 PoC 参考，不是已验证成品 |
| [Zoltak-Dev/ai-multi-instance](https://github.com/Zoltak-Dev/ai-multi-instance) | Claude 使用 `--user-data-dir`；Codex 另设 `CODEX_HOME` 和 `CODEX_ELECTRON_USER_DATA_PATH` | 不可以 | 可借鉴 Profile 编排和应用特定契约 |
| [jlcodes99/cockpit-tools](https://github.com/jlcodes99/cockpit-tools) | 独立实例目录；Desktop 2.x 启动前写同一个 `gemini:antigravity` | 不可以 | UI/生命周期参考；认证仍是全局切换 |
| [lbjlaq/Antigravity-Manager](https://github.com/lbjlaq/Antigravity-Manager) | OAuth 账号池、本地代理、配额路由；官方客户端切换时写全局凭据 | 不可以 | 成熟代理路线，不是官方多窗口并发 |
| [Draculabo/AntigravityManager](https://github.com/Draculabo/AntigravityManager) | 账号池和 WinCred 切换，固定使用同一凭据坐标 | 不可以 | 串行切换，不是实例隔离 |
| [TaeyanG4/ai-switcher](https://github.com/TaeyanG4/ai-switcher) | USERPROFILE/APPDATA/`--user-data-dir` 和协议 broker | 未证明且机制不足 | 测试只覆盖目录/参数，没有真实双账号证据 |
| [ProTechPh/Mira-AI](https://github.com/ProTechPh/Mira-AI) | 独立目录并向旧 `state.vscdb` 注入 refresh token | 不适用于当前版本 | 旧 IDE 路线；另有 Token 落盘和许可证不一致风险 |
| `multigravity-cli` / `multigravity-pro` 系列 | USERPROFILE/APPDATA/`--user-data-dir`/`--extensions-dir` | 不可以 | 与当前已失败的 Direct 机制同类 |
| [ppanphper/ag-manager](https://github.com/ppanphper/ag-manager) | macOS 应用克隆和重签名 | 不适用 | 非 Windows 方案 |

精确拼写 `ai-muti-instance` 没有检出可信实现；最接近且有持续提交与 Release 的项目是 `Zoltak-Dev/ai-multi-instance`。它明确只支持 Claude 和 Codex，且其同用户 DPAPI/session 快照模型不能隔离 Antigravity 的固定 WinCred 项。

本轮还发现若干近期或单提交仓库，但未完成源码审计，不能作为结论依据：`krouter`、`antigravity-studio`、`antigravity-account-switcher`、`ag-multi-account-switchboard`、`antigravity-storage-manager`、`ai-tools-mng`、`Multigravity`、`MultiIDELauncher`。其中项目描述涉及切号、代理、Token 或目录隔离，不应仅凭 README 宣称支持真并发。

## `multi-cli` 审计

审计固定在 commit `6efb0d204d4e690c2e0f5e9c2ee900a3cead6afa`。完整克隆包含 39 个提交，MIT 许可，没有 tag 或 GitHub Release。

成立的部分：

- Antigravity adapter 明确声明固定凭据 `gemini:antigravity` 和 `singletonScope: osUser`。
- Windows 实现为每个 Profile 派生一个受管本地用户名，并以另一个用户的 token 和已加载用户 Profile 启动进程。
- 操作者侧密码存入 Credential Manager，而不是 Profile JSON。

不能直接采用的部分：

- `tests/e2e/windows/Invoke-DualAccount.ps1` 只检查前置条件，随后明确要求未来实现身份、进程重叠、配额和 logout 断言，并以退出码 4 结束。
- Antigravity 的 detached 分支只把 `Start-Process` 返回非空当作成功，没有验证目标 SID、交互 Session、可见窗口、持续存活或实际账号。
- adapter 默认解析当前操作者 `%LOCALAPPDATA%/Programs/Antigravity/Antigravity.exe`。本机目录和 EXE ACL 只授予 `SYSTEM`、Administrators 和当前用户；新建标准用户没有读取/执行权限，而实现没有修复或预检该 ACL。
- 创建用户时密码作为 `net user` 的 argv 传递。删除只执行 `net user /delete`，没有可靠清理或显式保留 `Win32_UserProfile`、用户目录和残留认证状态。
- 没有证明 OAuth 浏览器、`antigravity://` 回调、语言服务器和窗口都属于预期用户及交互桌面。

这些缺口意味着 `multi-cli` 只能提供隔离边界思路，不能作为运行时依赖或完成证明。若借鉴其 MIT 代码而非独立实现，必须保留相应版权和许可文本。

## 当前代码决策

现在保留：

- DirectSerial 的全局启动锁、持久 launch barrier、任意现存根进程阻断。
- `HubService.LaunchAllAsync` 和 GUI 的多 Profile 拒绝。
- `ProcessOwnershipResolver` 的行为保持型抽取。它集中 PID、创建时间、可执行路径、精确 user-data 参数及后代顺序规则，是未来增加 OwnerSid/SessionId 时唯一合理的安全切入点。

现在不做：

- 不增加通用 `IIsolationBackend`、Profile backend 枚举、schema v2、按域锁或 GUI backend 选择器。
- 不解除第二实例门禁，不把 Sandboxie 或任一 GitHub 工具接入发布包。
- 不创建本地用户、不改 ACL、不保存辅助用户密码，也不复制或注入 Google Token。

原因是外部 PoC 的结果会决定启动器、进程目录、控制器和 OAuth 路由中哪些必须一起后端化。先定义接口会把未知机制固化成错误架构。

## 条件式 OS-user PoC

只有在用户明确允许“每 Profile 一个辅助本地标准用户”后，才执行一次性、产品外 PoC；PoC 不迁移 schema，也不改 GUI。

1. 创建两个随机且带可核验所有权记录的临时本地用户。密码不得进入 argv、环境、JSON 或日志；应使用 Windows API 安全建号并存入受保护凭据存储。
2. 只给安装目录精确读取/执行权限，只给两个测试 Profile 和精确测试 workspace 所需的修改权限；记录原 ACL 并提供失败回滚。不得给整个 `Users`、用户主目录或 AppData 广泛权限。
3. 启动后联合验证 PID、创建时间、EXE、精确 user-data、OwnerSid、SessionId、窗口句柄和持续存活；两个窗口必须位于当前交互桌面。
4. Hub 进程重启后必须能重新发现、聚焦、优雅关闭并在必要时只终止自己拥有的进程。
5. 浏览器、OAuth 回调接收者、Electron 根和语言服务器必须保持在目标 SID；不得修改宿主协议注册或记录授权 URL/代码。
6. 用户在官方界面完成两个测试账号登录。必须证明身份不同、进程生命周期重叠、配额归属独立、交错重启保持、A logout 不影响 B、真实 workspace 写透。
7. 任一项失败即关闭路线。清理必须按 SID 和所有权双重确认；删除用户与删除 Windows user profile 分开、显式、可审计，默认保留诊断数据。

## PoC 通过后的最小重构

PoC 全部通过后才实施：

1. Profile 增加 `DirectSerial` 或 `WindowsUser` 的不可变隔离绑定；旧 schema 原子迁移后全部保持 `DirectSerial`，不迁移认证数据。
2. `ProcessIdentity` 和 `ProcessSnapshot` 增加 OwnerSid 与 SessionId；缺失或不匹配一律为 Unknown，禁止聚焦、关闭和终止。
3. 增加窄的显式 provision/unprovision 服务。创建普通 Hub Profile 不自动建用户，remove 仍只注销；破坏性清理必须是独立操作。
4. `AntigravityDriver` 继续只构建官方应用参数；经 PoC 定型的 Windows 身份执行层负责 token、用户 Profile 和环境。
5. 锁和 barrier 按不可变隔离域划分：DirectSerial 继续全局互斥；同 SID 跨数据根互斥；未知组合默认冲突。只有两个已验收且 SID 不同的 WindowsUser 域允许并发。
6. 最后才让 Core 能力驱动 CLI/GUI 的 Launch all。任何未验收版本、ACL 漂移、SID 重建或 OAuth 归属异常都恢复 fail-closed。

如果辅助本地用户仍不可接受，则当前串行实现已经是满足安全边界的最终形态；恢复核心并发只能等待 Google 提供 Profile-scoped TargetName 或其他官方认证契约。
