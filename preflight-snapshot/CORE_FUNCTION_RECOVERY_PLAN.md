# 核心功能恢复规划与执行记录

> 历史阶段记录：本文记录的是发现 keyring 读回退之前的 P0–P2。最终根因、同名盒内条目方案与通过验收见 `KEYRING_ISOLATION_REPORT.md`；当前产品状态见 `PROJECT_STATE.md`。

日期：2026-09-08。

执行状态：P0 已完成；P1 的随机自造凭据矩阵已在 Sandboxie Plus 1.18.2 / core 5.73.2 上通过。P2 的标准 Sandboxie 配置已失败：用户观察到并发运行时 B 显示 A 的既有登录态，虽然 A/B 进程归属正确。五秒只读 B 资源追踪记录了 43,407 个 session、18,157 个 B 条目、2,872 个 unique 资源、337 个 unmapped discarded、144 个 truncated；B 未显示直接读取宿主用户既有 Google/Antigravity 配置、cookie store、Local State 或 credential/vault/DPAPI data，但观察到加载 Windows System32 `DPAPI.DLL`，且 B 自身 user-data 下的 `Cookies-wal` 不属于宿主既有认证存储；另有 `protected_storage`、`SBIE_ProtectedStorage_Section`、`SBIE_BOXED_ServiceCrypto_Mutex1` 等共享认证相关对象。A/B 各有独立 `SbiePst.dat`（只检查路径/元数据）。没有安全的 `ClosedFilePath`/`ClosedIpcPath` 规则可从追踪中推出。A/B 沙箱数据均保留，Sandboxie 未接入产品。一次隐私模式 C 盒仅运行约五分钟后因无 supporter certificate 终止，不能作为依赖。没有枚举或读取真实认证条目，也没有修改旧 A/B 数据。工作区已有 scripts/Inspect-InstalledApp.py 修改，继续原样保留。

P1 证据位于被 Git 忽略的 `artifacts/sandboxie-credential-probe/<nonce>/report.json`。静态源码中的宿主 `CredReadW` 回退仍是真实风险，但本机动态矩阵显示 Sandboxie 的整体隔离使空白箱无法取得宿主测试值。该结论只绑定 1.18.2/5.73.2 和当前配置，尚不等价于 Antigravity 认证通过。

## 1. 恢复原始目标

原始 Master Prompt 要求：同一 Windows 用户、同一个官方安装、N 个 Profile、不同 Google 账号可并发工作且重启后保持登录；不使用虚拟机、额外 Windows 用户、认证代理或凭据搬运。

现有实现已经有 Core、CLI、WPF、配置锁、进程归属验证、快捷方式和打包。真正缺失的是账号隔离。继续完善界面、增加环境变量、删除并发门禁，都不能解决这个问题。

README 和 PROJECT_STATE 已诚实降级为串行管理器，但这不是原始核心功能的完成态。串行重启曾成功，也不能证明 B 登录后 A 仍能恢复自己的账号。现有数据不应迁移或清理。

## 2. 已有事实与证据强度

- 仓库记录：Desktop 2.12.2 的目录和进程隔离曾被观察到；B 首启显示 A 会话，A/B 并发均显示后登录账号。
- ROOT_CAUSE_AND_SOLUTION.md 记录后端包含 wincred/go-keyring 符号，以及系统存在 gemini:antigravity 条目。这支持共享 Windows 凭据存储的诊断，尚不是动态确认的厂商契约。
- 原文将随机端口、独立目录等表述为彻底“排除”其他原因，证据不足以如此绝对。它们降低相应假说的可能性，不能证明所有 IPC、认证读取或回调路径均隔离。
- 当前 AntigravityDriver.BuildLaunchPlan 仅隔离 user-data 和子进程环境。
- ProcessSupervisor.LaunchCoreAsync 看到另一个根进程即拒绝启动；HubService.LaunchAllAsync 和 GUI 也单独禁止多 Profile 批量启动。
- WindowsProcessLauncher 返回直接创建的进程身份。引入外部沙箱启动器后，这个身份可能只是启动器，必须重新发现真实应用进程。

## 3. 方案取舍

| 路线 | 判断 |
|---|---|
| 继续调整 HOME、APPDATA、user-data | 不能独立解决系统级认证存储共享，不作为主线 |
| 仅串行登录或改 OAuth 登录入口 | 不解决登录后的共享存储；回调是另一道独立关卡 |
| 每 Profile 独立 Windows 用户 | 与原始需求冲突，当前不采用；旧文档对计划任务交互窗口及凭据保存的描述也不能直接作为实现契约 |
| 修改 app.asar、LS 二进制、自研认证 Hook 或复制 Token | 不纳入当前方案 |
| 官方提供 Profile 认证命名空间 | 理想路径，但本轮没有取得这样的 Desktop 官方契约 |
| 每 Profile 一个 Sandboxie 沙箱 | 已做有限验证但失败；进程和自造凭据隔离通过，真实 Antigravity 会话仍跨盒继承 |

Sandboxie 文档说明 OpenCredentials=y 会开放真实凭据存储；受保护存储的开放配置也可能使隔离失效。上游 cred.c 实现了 CredRead/Write/Delete 等 API 的包装，但 Cred_CredReadW 在沙箱数据缺失时会调用系统原始 CredReadW。因此默认配置不能被当成“空白且与宿主完全隔离”的保证。

来源：
- https://sandboxie-plus.github.io/sandboxie-docs/Content/OpenCredentials/
- https://raw.githubusercontent.com/sandboxie-plus/Sandboxie/601ef451e671af5285d46b96d2153b0fbf55c06d/Sandboxie/core/dll/cred.c

这是本方案的阻塞项。固定的 v1.18.2 发行版源码中的 `CredReadW` 回退与无 Profile 的 OAuth 协议共同使标准 Sandboxie 不能作为认证隔离后端。

## 4. 先做验证，再改产品

以下步骤保留为边界和历史验收记录；本轮 P0/P1/P2 已执行，用户登录始终在官方界面手工完成。

### P0：有限静态核查

最多两轮定向调查，输出一个结论表：

1. 所选稳定版 Sandboxie 是否有受支持的配置，阻止空白沙箱回退宿主凭据；核对配置继承、模板和 protected storage 例外。
2. 是否能让登录浏览器和 antigravity:// 接收进程留在同一个沙箱，不修改宿主协议注册，不让 Hub 接触授权 URL 内容或授权码。
3. 明确需要的版本、Windows 条件、管理员安装步骤、功能许可条件；先作为外部依赖，不打包或自行编译沙箱驱动。

若找不到满足边界的配置，应记录为阻塞，不反复逆向 LS、不扩大为自研隔离引擎，也不开始 GUI 集成。源码中的宿主回退必须正面处理，不能用 OpenCredentials=n 一项宣称解决。

### P1：只用自造测试数据验证隔离机制

使用随机且专属于测试的 AGProfileHub.Probe.<nonce> 凭据目标，创建公开的无价值标记。允许测试程序读写自己生成的标记以做相等性断言；不得枚举或访问任何真实认证条目。

验证矩阵：

| 场景 | 必须观察到的结果 |
|---|---|
| 宿主写 H，空白 A/B 读取同一测试目标 | A/B 看不到 H；若能读取，当前配置不满足空白隔离要求 |
| A 写 A，B 写 B，交错读取 | A 只得到 A，B 只得到 B，宿主仍为 H |
| A 删除自己的测试条目 | B 和宿主标记保持；A 不能回退得到 H |
| 重启沙箱应用及系统后复查 | 各自标记持久且互不影响 |
| 后端或浏览器产生子进程 | 每个进程属于预期沙箱，不能只检查 Electron 父进程 |

测试辅助程序不进入产品认证流程。只清理本次测试创建的精确目标和空白测试沙箱，绝不清理真实 Profile。

P1 失败时停在验证层。不能通过修改真实 Google 凭据、手工塞入空凭据、复制宿主状态等办法制造通过结果。

### P2：真实应用验证，用户完成登录

P1 通过才创建两套全新测试 Profile，原 A/B 保持原样。

1. A 首启处于未登录状态，用户在 A 沙箱中的浏览器完成官方登录。
2. 核查回调接收实例和 LS 的沙箱归属；确认没有宿主未托管实例。仅记录归属、时间和成功状态，不记录回调 URL。
3. B 首启仍未登录；登录 B 后，A 仍是 A。首次登录可串行，但这只减少路由竞争，不作为路由正确的证明。
4. A/B 同时执行一个小型真实请求；确认两窗口分别使用预期账号且返回结果。
5. 交错重启、系统重启；退出 A 账号后 B 仍可工作，再登录 A 不改变 B。
6. 实际测试工作区写入、终端、Git 和应用内浏览器。若沙箱只让文件写入虚拟副本，不能声称用户项目已被修改。

浏览器如果跳到宿主，或者 Google 登录被阻止，应判为失败并记录具体边界；不改造 OAuth 协议，不采用认证代理。本轮 P2 已有用户观察并判定失败，不能自动填通过或重新开启该路线。

## 5. 若 P2 成功的历史条件方案（当前不执行）

拟议结构：现有 CLI/WPF → Core → DirectSerial 或 Sandboxie 运行后端 → 官方应用。暂不做通用插件框架。

| 改动位置 | 实施内容 |
|---|---|
| Domain/Models.cs、Storage/ConfigRepository.cs | 增加运行后端和稳定 BoxId；schema 迁移将旧 Profile 保持为 DirectSerial，不迁移认证数据。运行记录和屏障也记录后端、BoxId |
| Driver/AntigravityDriver.cs | 保留官方应用参数构建，区分安装路径、应用逻辑数据路径和沙箱物理存储路径；环境必须显式传入实际应用，不能假设服务启动会继承 Hub 环境 |
| 新增小型 Sandboxie 适配层、Processes/ProcessAdapters.cs | 使用上游启动/列 PID 接口；启动器退出码只代表请求结果，真实运行成功必须由沙箱归属、PID、创建时间、路径联合验证 |
| Processes/ProcessSupervisor.cs、Storage/HubPaths.cs | 持久屏障按隔离域划分；DirectSerial 仍共用原来的每用户门禁；已验证沙箱之间允许并发，同一 BoxId 跨 Hub 数据目录仍互斥。未知归属和未验证的混用组合默认阻止 |
| Services/HubService.cs、Gui/MainWindow.xaml.cs | Launch all 根据每个 Profile 的后端能力和冲突检查决定，去掉当前单纯按数量拒绝的判断；批量预检后逐个启动，明确报告部分失败 |
| Doctor、能力记录 | 分开报告 credentialIsolation、hostCredentialFallbackBlocked、oauthCallbackRouting、authenticatedConcurrentUse、workspaceWriteThrough；能力绑定应用版本、沙箱版本、配置摘要和策略版本 |
| Data 按钮、快捷方式、状态恢复 | Data 打开真实有效数据位置；快捷方式继续调用 UUID/CLI。Hub 重启从沙箱列表和进程事实恢复，不能把 Start.exe PID 当应用 PID |

BoxId 必须跨 Hub 数据根保持唯一，不能从显示名生成；检查其他数据根对同一沙箱的占用。停止时先关闭实际应用窗口，强制停止只针对验证属于该 Profile 的沙箱和进程，不使用全局 terminate_all。

上游公开启动接口支持 /box:<name> 和 /listpids，可作为适配入口：
https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine/

工作区需要用户选定目录的精确直写规则，不能开放整个用户主目录、AppData、认证存储或任意系统资源。OpenFilePath 的适用行为取决于可执行文件位于沙箱内还是外；必须按实际安装和更新后的路径验收：
https://sandboxie-plus.github.io/sandboxie-docs/Content/OpenFilePath.html

移除 Profile 仍只注销，不删除沙箱内容；应用更新可能在沙箱产生独立覆盖副本，需要检测实际运行版本。隔离配置、应用或沙箱版本变化后，相关验收失效，恢复待验证状态。不要把这种兼容隔离宣传为同一 Windows 用户之间的安全保密边界。

## 6. 历史测试与交付顺序（当前停在 P2 失败）

以下是进入产品改造前设定的条件顺序。本轮 P2 已失败，因此第 3–5 步的 Sandboxie 产品接入未执行；现有项目验证与结论文档按失败结果收尾。

1. P0/P1 先证明运行边界；失败就交付阻塞证据，不投入产品改造。
2. P2 证明实际双账号闭环。
3. 增加后端、schema 和配置校验；检查旧配置无损兼容。
4. 集成启动、归属、停止、恢复和按隔离域互斥。重点测试启动器提前退出、延迟发现、PID 复用、错误沙箱、写屏障失败、跨数据根同箱竞争、异箱并发和依赖缺失。
5. 接入 CLI 和 GUI，验证真实工作区变更及快捷方式。
6. 跑现有适用测试及新行为测试，再做真实进程验收和打包。旧 DirectSerial 单根测试继续保留；不要把断言全部改成允许双开。
7. 按精确验证结果更新 PROJECT_STATE、CAPABILITY_REPORT、MANUAL_TEST_PLAN、IMPLEMENTATION_PLAN、ASTRA_HANDOFF 和 README。

## 7. 当前交接边界

标准 Sandboxie 路线已结束，当前禁止接入 Sandboxie 或据此改造 GUI、后端和发布包。只有取得官方 Profile 认证契约，或用户明确批准不同的 Windows 隔离边界后，才重新规划后续验证和实现。

最终判断：标准 Sandboxie 路线已因真实会话跨盒继承、`cred.c` 的宿主 `CredReadW` 回退、共享认证对象和缺少安全 closed-resource 规则而结束，不接入产品。当前产品保持串行、Google-account-incompatible；等待官方 Profile 认证能力或由用户重新选择约束。
