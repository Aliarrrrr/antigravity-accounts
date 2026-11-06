# Accounts 实现审计与原生体验检查

日期：2026-11-07。审计对象：`D:\mutiagy\.worktrees\shared-workspaces`，基于提交 `bc0165e`（Accounts `0.4.0-preview.10`），宿主适配包含 Antigravity App `2.29.0` 和 `2.28.2`。

本文保留 preview.11 最终视觉调整之前的审计与开发包检查记录。文中的工作树状态、检查数量和“源码一致”均指该阶段；preview.11 的后续界面、安装及发布检查见 [当前检查记录](ACCOUNTS_VALIDATION.md)。

本轮修复了切换恢复、账号存储写入边界和界面键盘操作中的具体问题。**修改尚未提交或发布；本地开发包已按用户授权安装到 2.29.0，并重新应用中文界面。** 随后在新建空项目中完成同一会话的 A → B → A、三条合成文字请求。具体结果及仍未确认的行为见下文；不把这次短会话推广成所有内容与工具场景均已确认。

## 已修复的问题

| 问题与触发条件 | 原有影响 | 本轮修改与位置 |
| --- | --- | --- |
| 切换已提交、身份与模型已确认；清理捕获记录时，一个原生窗口成功，另一个窗口失败 | 重试又执行身份确认；已清理窗口返回 `native_view_capture_missing`，发送一直暂停 | [switch-coordinator.cjs](../packages/hostauth-adapter/src/switch-coordinator.cjs) 的 `confirm()` 在所有身份、项目、缓存、令牌代次和模型检查成功后记录 `identityConfirmed`。重试继续幂等清理；清理已完成但释放失败时，只重试释放 |
| 取消切换的清理首次失败后，两个窗口同时点击重试 | 两次恢复可能同时取消同一 ticket，使第二次取消报错，并互相覆盖恢复状态 | 同文件 `recover()` 在首次异步调用之前将状态改为 `confirming`，第二次恢复得到 `switch_in_progress` |
| 已保存 100 个账号，再授权一个不同的 Google subject | 旧写入路径可保存第 101 个账号，但读取校验拒绝超过 100 个账号，导致后续列表无法读取 | [AccountService.cs](../src/Antigravity.Accounts/AccountService.cs) 的 `RunLoginAsync()` 在添加前检查 `SavedState.MaximumAccounts`；同 subject 的重新授权仍可更新已有记录。上限定义在 [Models.cs](../src/Antigravity.Accounts/Models.cs) |
| 更新后的加密账号文件超过 4 MiB，或更新生成无效账号状态 | 写入接受、后续读取拒绝同一状态 | [EncryptedAccountStore.cs](../src/Antigravity.Accounts/EncryptedAccountStore.cs) 的 `UpdateAsync()` 在替换文件前调用 `ValidateState()`，并检查加密结果长度；拒绝时保留原文件 |
| 在管理界面修改标签后按 Enter 保存，再收到新的账号状态 | Enter 与按钮保存路径不同，已成功保存的输入仍被视为未保存，后续状态无法更新输入框 | [account-management.mjs](../packages/hostauth-adapter/src/account-management.mjs) 的 `accountRow()` 让两种操作共用 `saveLabel()`，成功状态匹配后清除 dirty；失败仍保留输入 |
| 删除的账号行正持有键盘焦点 | 行移除后焦点落到页面，继续按 Tab 的位置不明确 | 同文件 `render()` 将焦点移到相邻账号行；没有相邻行时移到可用的添加或关闭按钮 |
| 等待切换时，状态轮询反复刷新菜单，焦点位于取消切换按钮 | 重建按钮使键盘焦点丢失 | [model-menu-entry.mjs](../packages/hostauth-adapter/src/model-menu-entry.mjs) 的 `renderEntry()` 保留已有取消按钮；取消动作不可用后将焦点移到管理入口 |

切换恢复的新增测试覆盖部分清理失败、释放失败、模型仍需显式选择，以及两个窗口同时恢复。只有身份、项目、全部要求的缓存和已观察到的账号代次都匹配后，才记录确认完成。身份不符或 `native_model_selection_required` 不会进入清理恢复路径，也不会释放发送。

本轮另追加了管理界面的“取消授权”操作。调用路径为 `account-management.mjs` → `model-menu-bridge.cjs` → `runtime.cjs` → 组件 stdio 的 `cancelLogin` → `AccountService.CancelLoginAsync()`。取消针对当前登录尝试，不更换已保存账号；取消与保存共享选择锁，已经保存完成的登录不能再被报告为取消。取消后可以立即重新授权，旧尝试的迟到成功或失败不会覆盖新尝试。使用真实本地回调监听与合成 HTTP handler 的检查确认取消后监听关闭，无须等待三分钟，也没有请求 Google 端点。

## 账号与认证处理边界

本轮读取了账号服务、Google OAuth、DPAPI 存储、stdio 传输、原生任务状态、切换协调器、模型菜单桥接、管理界面和模型视图更新路径，并检查相关合成测试。下列结论限于这些实现及本地检查。

- [GoogleOAuth.cs](../src/Antigravity.Accounts/GoogleOAuth.cs) 的 `OAuthProof.Create()` 为每次登录生成随机 state 和 PKCE verifier，使用 SHA-256 challenge；`ValidateCallback()` 校验 state，再接受授权码。回调监听绑定 `127.0.0.1`，只接受 `GET /oauth/callback`。授权等待为 3 分钟，HTTP 请求超时为 30 秒，授权码上限 8192 字符，OAuth JSON 响应上限 65536 字节，HTTP 客户端禁止自动重定向。这些检查没有使用真实 Google 授权。
- 账号组件通过选择锁、ticket 和 generation 管理准备、提交与取消。当前账号或主账号在运行时的删除限制分别由 [runtime.cjs](../packages/hostauth-adapter/src/runtime.cjs) 和 `AccountService.RemoveAccountAsync()` 检查；有待提交切换时，删除和重新授权不能绕过组件的事务检查。
- [EncryptedAccountStore.cs](../src/Antigravity.Accounts/EncryptedAccountStore.cs) 保存的是用户主动添加的独立账号授权，使用 Windows DPAPI CurrentUser。当前路径没有导入官方登录数据库、Cookie 或 Windows Credential Manager 的接口。这里不能把“独立授权加密保存”描述为“不保存任何凭据”。
- 面向渲染界面的 [model-menu-bridge.cjs](../packages/hostauth-adapter/src/model-menu-bridge.cjs) 的 `ACTIONS` 和 `publicState()` 只开放允许的方法与展示字段；没有公开 `getToken`。来源必须是原生窗口的主 frame 和当前本地端点。组件内部 stdio 的 `getToken` **会**向 HostAuth 返回独立账号令牌，不能把整个 stdio 协议说成“不含 token”。
- [component-client.cjs](../packages/hostauth-adapter/src/component-client.cjs) 对组件响应行限制为 128 KiB，只转交受限格式的错误代码，并读取后丢弃组件 stderr；不把组件错误消息或 stderr 转入宿主日志。[Program.cs](../src/Antigravity.Accounts/Program.cs) 对接受的请求检查方法、字段、重复参数及长度。以上检查不证明任意外部组件或宿主未来日志行为都不会泄露数据。

## 安装、恢复与更新

本轮还查看了 [manage-install.mjs](../packages/account-plugin/manage-install.mjs)、[installer-lock.mjs](../packages/account-plugin/installer-lock.mjs)、[update-recovery.mjs](../packages/account-plugin/update-recovery.mjs) 及 [package-copy.mjs](../packages/hostauth-adapter/src/package-copy.mjs)。安装状态目录通过公开安装记录与实际路径定位，不枚举账号文件；安装写入使用同一程序目录的 Windows mutex。程序适配只接受已检查版本和内容，恢复前检查当前文件，拒绝覆盖更新器已经替换的程序。自动修复保留“程序仍在运行”“更新尚未结束”“未知程序内容”的不同结果。

本轮 Node 检查包含独立程序目录中的应用/恢复、更新内容拒绝覆盖、共享状态目录、安装锁与外部更新等待。这些结果不表示执行过真实 NSIS 更新，也不证明未来宿主版本受支持。开发包经现有构建脚本生成并检查其组件、源码和 ZIP 内容；之后的实际安装保持原账号资料目录，正常退出后恢复中文层、恢复旧 Accounts、安装开发包，再重新应用中文层。安装过程没有打开账号文件。

## 同会话上下文在不同账号之间的使用

当前切换路径为 `SwitchCoordinator.selectAccount()` → 暂停新的前端请求 → `readActivity()` → 取得令牌回调屏障并再次读取任务状态 → 捕获原生模型选择 → `beginSwitch` / `commitSwitch` → `confirmIdentity()` → 清理捕获记录并释放前端请求。

相关位置：

- [switch-coordinator.cjs](../packages/hostauth-adapter/src/switch-coordinator.cjs)：`selectAccount()`、`acceptCommit()`、`confirm()` 和 `cancelSwitch()`。
- [native-task-adapter.cjs](../packages/hostauth-adapter/src/native-task-adapter.cjs)：`readActivity()` 检查轨迹、原生空闲结果、后台工作聚合、元数据请求、定时任务与远程控制；不能读取状态时拒绝提交切换。
- [native-agent-state.cjs](../packages/hostauth-adapter/src/native-agent-state.cjs)：短期订阅读取初始工作计数，关闭的是本次观察请求，不调用停止任务 RPC；不解码会话正文。
- [native-view.cjs](../packages/hostauth-adapter/src/native-view.cjs)：`updateNativeView()` 使用原生状态与模型 action 更新列表，并按窗口捕获当前 conversation 路由和模型选择。路由变化时返回 `native_view_context_changed`；原模型不可用时要求真实的原生 `modelPicked` 操作。

从源码可以确认，切换协调器没有复制、摘要、删除或新建会话，也没有在切换中重启语言服务。合成视图检查中，草稿和导航状态没有被视图更新函数修改。这些事实解释了插件如何保留官方会话容器，**不能证明 Google 后端已经接受另一个账号继续同一会话的完整上下文**。

本轮随后已经确认了短文字会话中的同一窗口、项目和 conversation ID 的 A → B → A、草稿与所选模型保留及继续生成，见下一节。仍未确认包含附件、工具与子代理结果、长历史的会话；没有测量精确额度归属，也没有主动制造过期令牌。历史元数据切换观察及本轮开发包结果分别记录在 [ACCOUNTS_VALIDATION.md](ACCOUNTS_VALIDATION.md)，不能互相替代。

### 经授权的三条真实生成

用户明确允许安装、使用现有账号授权、A → B → A，以及最多三条合成请求。测试新建空项目 `D:\mutiagy\outputs\accounts-context-20261107`，只输入合成代号与数字，要求不调用工具。没有将旧会话或项目文件用于测试。

| 阶段 | 发送内容要点 | 原生窗口观察到的回复 |
| --- | --- | --- |
| A | 记住代号 `AGY-7C91-M4Q2` 和数字 `583149`，只回复确认文字 | `ACK-A` |
| B | 不重复代号或数字，询问首条消息中的数字组合 | `583149` |
| 返回 A | 询问首条代号末四字符与数字倒序 | `M4Q2\|941385` |

三次均使用 Gemini 3.8 Flash High。两次切换先在当前账号输入下一条问题作为草稿，切换确认后原文仍在，之后才发送。会话 ID 始终为 `e3b2cfdf-f378-4694-9de1-be20981f052b`，项目 ID 始终为 `0f25a9d2-dfd8-45f9-8a29-196502cf18e4`。窗口 ID `11667792` 保持；应用 PID `42680`、语言服务 PID `15132`、组件 PID `27984` 及各自启动时间在开始、B、返回 A、结束四次读取中一致。模型菜单先后显示对应账号“正在使用”，最后回到原账号 A。

这确认了该短文字会话的原始内容能够在原生账号切换后继续用于生成；不是仅有模型菜单标签变化。没有观察到工具调用，空测试文件夹结束时仍为空。结束时保留测试会话可供查看，没有发送第四条请求。安装后归档中的本轮 runtime、切换协调器、组件通信、菜单桥、生成 preload，以及独立组件程序均与当前源码或开发包一致。

记录在 `output/playwright/audit-live-context.json`，对应进程读取文件为 `audit-live-processes-*.json`；安装记录为 `artifacts/accounts/audit-20261107/installation-result-1793984881981.json`。这些本地文件不进入发布内容，账号在记录中仅标为 A/B。三次是界面发送次数，原生标题、元数据及可能的重试请求总数未统计；未独立确认 Google 的具体扣额或计费账号，不宣称三次后端请求或零额外调用。

### 切换期间已经提交的请求

[submission-gate.cjs](../packages/hostauth-adapter/src/submission-gate.cjs) 的请求处理函数只读取 URL，并保存 Electron 的回调；`release()` 通过 `callback({ cancel: false })` 让请求继续。它不读取或重写上传正文，因而不会替已序列化的请求更换模型或重新整理会话输入。

具体待确认条件：用户在账号 A 的模型仍显示时发送消息，请求被暂停；账号 B 不支持该模型，用户随后改选模型。视图与后续发送可以使用新选择，但这不能证明此前已经序列化并排队的请求也使用新模型。需要客户端检查这类请求的实际结果、失败提示与草稿恢复。当前没有观察到真实错误发送，不能仅凭排队实现断言它已发生；同样不能宣称该场景已正确处理。

### 等待切换期间的原生停止操作

`createNativeTaskAdapter()` 传给提交暂停器的允许路径只有状态/元数据读取接口，未包含原生停止接口。因此，用户在等待切换时点击停止，相关请求也会排队。现有操作办法是先取消切换，再使用原生停止按钮；取消切换本身不停止原任务。

本轮从未修改的 2.29.0 语言服务副本读取嵌入前端与调用实现，确认普通停止 hook 使用 `CancelCascadeInvocation`，停止部分子代理时可携带 `notifyParent: true`。调用关系如下：

```text
原生前端 stop hook → cancelCascadeInvocation({ cascadeId, ... })
Server.CancelCascadeInvocation            0x143055860
local.Backend.CancelCascadeInvocation     0x142bb9160
CascadeManager.Cancel                    0x142635c60
AgentExecutor.cancel                     0x14258c340
AgentExecutor.maybeNotifyParentOfCancel   0x14258c1a0
messages.Client.Send                     0x1410cbc20
```

被检查的原始程序 SHA-256 为 `c98e7c331da5bcc37002a550e7d625b4c636f775fa2804f73777c6a500e1c3d5`。停止子代理可能发送父代理消息，不能只按 URL 将此接口认定为“只减少工作、不会产生后续工作”。没有把它加入允许路径，也没有改写请求中的 cascadeId。直接放行需要进一步处理提交阶段与父代理通知的关系，否则在账号已提交后仍可能影响同一 cascade 的新状态。该检查只读取程序，没有运行停止操作或发送模型请求。

## 原生插件列表与开关

独立结果见 [ACCOUNTS_EXTENSION_INTEGRATION.md](ACCOUNTS_EXTENSION_INTEGRATION.md)。本地隔离的原生语言服务已通过 `GetAllPlugins` 发现合成 Accounts 条目，并使用 `JetboxWriteState` 将 `enabled` 从默认开启改为 `false`、再改为 `true`；重读列表和配置文件均反映变化。未在真实窗口中观察对应条目或开关。

Accounts 当前的 HostAuth、组件和 preload 生命周期仍由 App 启动接入管理，没有读取原生插件 enabled 选项。因此，现阶段只能报告“原生列表与持久化开关可行”，不能报告“Accounts 已可由该开关完整关闭”。完整接入需要在启动认证来源之前读取选项，并对运行中的更改显示重启生效，避免在任务执行中切断认证来源或无提示地更换账号。

## 本轮检查范围与交付状态

- C# 组件检查 **44/44 通过**，使用受控 OAuth 与临时数据目录；包括存储拒绝更新后原文件保留、取消授权、取消后重试、旧登录迟到及本地回调关闭。
- Node 现有套件 **225/225 通过，无跳过**；包含切换恢复、显式模型选择、界面、传输、安装与更新路径。程序相关检查使用独立的原始 2.29.0 ASAR/语言服务副本；Electron 辅助项使用现有程序的 Node 模式，没有启动正式 App 或访问账号。
- Chromium 隔离页面使用当前源码与宿主样式，鼠标操作确认“添加”进入合成 pending 后可取消、账号保留、取消按钮隐藏后焦点回到添加按钮；合成账号删除后焦点转到相邻标签。Enter 保存和再次打开可见保存后的名称。检查还包括深色与 390×620 浅色窗口；页面请求只允许本地测试服务。这不是已安装客户端窗口的观察。
- 本地开发包：`artifacts/accounts/audit-20261107/AntigravityAccounts-0.4.0-preview.10-win-x64.zip`。现有打包检查核对了 45 个内容文件以及 ZIP 内容。其 `provenance.dirty=true`、`release=false`，没有新版本发布。
- 本地测试日志和界面图片在 `output/playwright/audit-node-tests.log`、`audit-package-build.log`、`audit-dark.png` 和 `audit-narrow.png`，这些生成内容不随源码提交。
- `git diff --check` 检查了修改格式；它不检验客户端行为。
- 打包后的组件在全新资料目录中实际执行了状态读取、无 pending 时取消登录、非法取消参数拒绝及退出；没有配置 OAuth 或访问 Google。
- 用户授权后完成开发包安装和上述三条合成生成，保留中文界面与原账号资料目录。未发布新版本，未验证明确过期的令牌、附件/工具历史、切换中发送或精确额度扣减；进一步生成须遵守另行确认的范围，不能超过本次三条预算。

以上结论描述本次已查看与已执行的范围，不表示已覆盖未来宿主版本、所有工具调度组合、实际生成扣额或插件市场安装。
