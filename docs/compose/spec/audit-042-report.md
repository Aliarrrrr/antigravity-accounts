# Antigravity Profile Hub 0.4.2 全量审计报告

- 对象：`antigravity-profile-hub` @ `7f518d8`（标签语义 0.4.2）
- 方法：只读源码/文档/脚本交叉验证；不把 SECURITY.md / README 自述当已实现证据
- 范围：安全 / 可用 / 美观 / 简洁 / 环境依赖 / 可迁移 / 文档真实性
- 约束：本机未完成 Antigravity Desktop 运行时验收；标注 `needs-runtime` 的结论仅代码级

## 0. 总判断（第一性原理）

产品核心契约是：**在同一 Windows 用户下，让多个官方 Antigravity 窗口各自持有不同 Google 登录态**。  
0.4.2 相对 0.4.1 确实修掉了一批真实安全缺陷（进程枚举 fail-closed、argv 精确归属、回调过期、可执行文件钉扎、发布 manifest 真话），整体工程纪律好于多数实验项目。

但审计仍发现：**安全模型的“自证 fail-closed”在热路径、direct 路径和日志可伪造性上并未闭合**；**文档仍高于实现**；**产品形态仍是调试工具密度**；**硬依赖 Antigravity 2.12.2 + win-x64 + 无签名**决定了可迁移与分发成本。

优先级建议（若只做 5 件事）：

1. **P1 安全闭合**：热路径也强制 token-store 自证；约束/重算 `RouterCommand`；无 pin 时拒绝 launch/handoff（SEC-01/02/03/05）
2. **P1 文档对齐**：Doctor / SECURITY / MANUAL_TEST_PLAN / ARCHITECTURE 与真实能力一致（DOC-05、UX-14、DOC-01）
3. **P1 产品可用性**：冷启动可见反馈、破坏性操作确认、中英一致（UX-01/02/10）
4. **P1 并发登录契约**：pending 单槽 last-writer + E2E 未验收（SEC-07 + README ⚠️）
5. **P2 简洁**：Sandboxie 枚举/双栈清理、测试真验行为、CI 包结构闸门前移（SIMP-01/03/05、DEP-05）

---

## 1. 安全性

威胁模型前提：SECURITY.md 已声明「Profile 不是对同一 Windows 用户的安全边界」。下列问题针对**代码对外呈现为闸门、实际未闭合**的部分。

| ID | 位置 | 现象 | 风险 | 优先级 | 建议 |
|---|---|---|---|---|---|
| SEC-01 | `DeepLinkRouter.cs:237-246`；`IsolatedProfileLauncher.cs:184,207` | `EnsureRegistered` 把注册文件里的 `RouterCommand` **原样**写入 `HKCU\...\antigravity\shell\open\command`，无“必须是本 Hub CLI”约束 | 高 | P1 | 启动时按当前 CLI 路径与 data root **重算**命令，或校验前缀/哈希后再写；写失败删除 ownership 记录（已有）保留 |
| SEC-02 | `IsolatedProfileLauncher.cs:164-168` | 已有进程在 scope 内时直接返回 Running，**跳过** `RequireFileTokenStoreAsync` | 高 | P1 | 热路径也要证明本 scope 存在有效 token-store 证据（进程启动时间晚于日志证据，或强制重新自证） |
| SEC-03 | `IsolatedProfileLauncher.cs:243-281`；`262-276` | 闸门是用户可写日志里的子串 `"Using file-based token storage"`；`LogLength` 失败或文件被截断时 floor=0，历史标记可过检 | 高 | P1 | 将证据绑定启动时间/进程创建时间；或校验日志文件归属 + floor 严格化；文档不得写成“绝对 fail-closed” |
| SEC-04 | `HubService.cs:50-54`；`ProcessSupervisor.cs:46-49` | 无 isolation mapping → legacy direct：无 `WSL_DISTRO_NAME`、无 token-store 闸门、共享主机凭据 | 中 | P1 | 文档/UI 必须写清 direct=串行+共享凭据；默认路径禁止静默回落 |
| SEC-05 | `ExecutableAttestation.cs:35` | `pinnedSha256` 为空直接 return（legacy 兼容） | 中 | P1 | 新装/首次 doctor 强制 pin；未 pin 时 launch/handoff 拒绝或明确 Experimental 标记 |
| SEC-06 | `ExecutableAttestation.cs:23` vs 后续 `Process.Start` | 校验与启动之间 TOCTOU（`FileShare.Delete`） | 中 | P2 | 对齐 AgentProbe：hash 时仅 `FileShare.Read`，或启动句柄方案 |
| SEC-07 | `DeepLinkRouter.cs:55-56,91-94,111-117` | 全局单文件 pending，last-writer wins，无锁、不绑定 live launch | 中 | P1 | pending 改为按 profile 多槽或队列；Route 时校验 profile 仍 launching |
| SEC-08 | `DeepLinkRouter.cs:146-157` | handoff 直接用 `pending.UserDataPath`，不与 `HubPaths.UserData(profileId)` 对账 | 中 | P2 | 用 ProfileId 解析权威路径，pending 只作选择器 |
| SEC-09 | `ProcessLocalBackend.cs:264-265,154-172` | WMI 只枚举 `Antigravity.exe`/`language_server.exe`；根退出后孤儿 helper 可能不可见 → Stop 误报成功 | 中 | P2 | 以 job object / 进程树快照补充枚举；Stop 失败保留 unknown |
| SEC-10 | `ProcessLocalBackend.cs:197-216` | `launch.cmd` 写后无校验即交给 `explorer.exe` | 低 | P3 | 写后重读校验内容/哈希再启动 |
| SEC-11 | `PathSafety.cs:30-37` | 字符串前缀包含 + 检查/使用竞态 | 低 | P3 | 句柄级 final path 校验（可接受延后） |

**已核对为健康（勿再当漏洞）**

- 直接 stop 的 PID+启动时间+主模块路径三重校验（`ProcessSupervisor` / `ProcessAdapters`）
- 回调 URI：scheme/长度/控制字符 + `ArgumentList`/`UseShellExecute=false`
- `AtomicJsonStore` 临时文件 + `.bak` + schemaVersion
- Hub 日志 allowlist，不落 CSRF/完整 argv
- 安装器 `PrivilegesRequired=lowest`，不改 PATH

**SECURITY.md vs 代码**

| 文档声称 | 实际 |
|---|---|
| 无法证明 token 切换则终止 | 冷启动路径大致如此；热路径不跑证明（SEC-02） |
| 证明 fail-closed | 证明是可预写/可残留的日志子串（SEC-03） |
| 钉扎不匹配则拒绝 | **仅有 pin 时**拒绝（SEC-05） |
| 回调只属于发起 profile | 单槽 last-writer（SEC-07） |

---

## 2. 可用性（交互 / UX）

| ID | 位置 | 现象 | 风险 | 优先级 | 建议 |
|---|---|---|---|---|---|
| UX-01 | `MainWindow.xaml.cs` PrimaryAction/RunAsync | 冷启动 1–2 分钟仅 wait cursor，无 “Launching…/Cancel” | 高 | P1 | 状态栏/按钮进入 launching 态；禁用重复点击并显示已等待秒数 |
| UX-02 | CLI `remove`/`stop --force`/`stop-all`；GUI Force stop | 无确认（GUI Remove 有，Force/Stop all 无） | 高 | P1 | 破坏性操作统一二次确认或 `--yes` |
| UX-10 | README 中文 vs GUI/CLI 英文硬编码 | 文档受众与界面语言断裂 | 高 | P1 | 至少中文资源字符串，或 README 英文主文档对齐 |
| UX-14 | `MANUAL_TEST_PLAN.md` 全文 | 仍要求 Sandboxie / `iso-writethrough` / `keyringClaimed`，0.4.2 CLI 无对应动词 | 高 | P1 | **重写** 0.4.2 验收计划（process-local + 并发登录 E2E） |
| UX-03 | Doctor 对话框 / CLI doctor | 原始 capability 名倾倒，无下一步 | 中 | P2 | 按严重度分组 + 指向 fix/configure-deeplink |
| UX-04 | 错误展示 | snake_case code 无释义 | 中 | P2 | code→一句话中文/英文 glossary |
| UX-05/06 | open/launch、close/stop 双词汇 | 心智模型混乱 | 中 | P2 | 日常只暴露一套；高级动词折叠 |
| UX-08 | Settings | 只弹 exe 选择器，不显示当前 pin/backend | 中 | P2 | Settings 面板展示配置摘要 |
| UX-09 | 工具栏 9 个平铺按钮 | Doctor/Callbacks 不可发现 | 中 | P2 | 分组：日常 / 诊断 / 数据 |
| UX-11 | 几乎无 AutomationProperties/KeyBinding | 无障碍与键盘路径弱 | 中 | P2 | 补 Name、F5、Esc |
| UX-12 | iso-* vs GUI 菜单文案 | 高级/危险语义不一致 | 中 | P2 | 统一文案与确认 |
| UX-07/13 | 输入框不预填；刷新失败弱提示 | 小摩擦 | 低 | P3 | 预填当前值；错误对话框 |
| AES-01 | Isolation 列 | 关键状态无颜色/badge | 中 | P1 | Ready/Needs fix/Direct 语义色 |

**做得好**：空状态 CTA、InfoBar 情境化消失、launch-all 预检 tooltip、CLI 退出码与 `--json`。

---

## 3. 美观性（界面 / 视觉）

仅基于 XAML/Theme（无运行截图）。

| ID | 现象 | 风险 | 优先级 |
|---|---|---|---|
| AES-01 | 隔离状态纯文本，弱于 Status badge | 中 | P1 |
| AES-02 | 仅浅色；无 dark/SystemColors | 中 | P2 |
| AES-04 | 九等宽文字按钮，无图标/主次 | 中 | P2 |
| AES-05 | Danger 色板定义了，Force/Remove 未用 | 中 | P2 |
| AES-07 | Doctor=OS MessageBox；状态栏几乎空置 | 中 | P2 |
| AES-03/06/08 | 占位 “A” logo；行高偏高；InfoBar 全是警告样式 | 低 | P3 |

判断：目前是 **可用的工具壳**，不是产品级视觉；优先做信息层次（隔离列、破坏色、诊断入口），再谈 dark mode。

---

## 4. 实现简洁性

| ID | 现象 | 风险 | 优先级 |
|---|---|---|---|
| SIMP-01 | `IsolationBackendKind.Sandboxie` 仍存在；`IsolationBackends.Create` 忽略 kind；driver 仍分支 | 中 | P2 |
| SIMP-03 | HubService 双栈：有 mapping → Isolated，无 → ProcessSupervisor 全套 | 中 | P2 |
| SIMP-05 | 自研 console 测试跑 54 例；部分纯 helper 重述；CI 无真 WMI/注册表 | 中 | P2 |
| SIMP-02 | AgentRuntimeProbe 实验性 + 硬编码 LS hash | 低 | P3 |
| SIMP-04 | 多层自研文件锁 | 低 | P3（合理但重） |
| SIMP-06 | CLI/GUI 启动路径复制 | 低 | P3 |
| SIMP-07 | scripts/legacy 隔离有价值，易误导 | 低 | P3 |

补充（本轮亲核）：

- `Models.cs:25,57` C# 默认已是 `ProcessLocal`，与 CHANGELOG 0.4.0「旧文档 load as Sandboxie」叙述不一致 → **文档债**（并入 DOC）
- `Publish.ps1:81-84` 的 `Start Hub.cmd` / `agprofile.cmd` 用 ASCII 编码写入；含中文的安装路径一般靠 `%~dp0`，但与 launch.cmd 的 UTF-8 策略不一致

---

## 5. 环境依赖性

| ID | 依赖 | 现象 | 风险 | 优先级 |
|---|---|---|---|---|
| DEP-01 | Antigravity Desktop **2.12.2 硬钉** | 未知版本一律 `version_unverified`，功能变砖直到改代码+人工验收 | 高 | P1（有意 fail-closed，但维护成本极高） |
| DEP-02 | .NET 10 + WPF + WMI + HKCU + explorer.exe | Windows-only；WMI 故障已 0.4.2 修 fail-closed | 高 | P1 |
| DEP-03 | 自带 `.tools/dotnet` + locked restore | 无 SDK 则脚本不装；NuGet 失败无回退 | 中 | P2 |
| DEP-04 | PS 5.1 vs 7 分裂 | 本地 5.1 可 publish，装机脚本要 7 | 低 | P2 |
| DEP-05 | CI 只 build+console test | 打包/安装器结构要等 tag | 中 | P2 |
| DEP-env | 本机 `hub.ps1 test` | `NuGet.targets(782): Value cannot be null. (Parameter 'path1')`，四个项目 restore 全挂 | 中 | P2（环境脆弱性：空 PATH 段/NuGet 设置组合可炸） |

---

## 6. 可迁移性 / 部署成本

| ID | 现象 | 风险 | 优先级 |
|---|---|---|---|
| PORT-01 | win-x64 自包含双包；**无代码签名**，靠 SHA-256 | 高 | P1 |
| PORT-02 | 快捷方式 COM IShellLinkW，绝对 data root + 依赖包内 `cli\` | 中 | P2 |
| PORT-03 | LocalAppData + `global-launch-state` 跨机迁移需整树 | 中 | P2 |
| PORT-04 | 注册 `antigravity://` 是唯一机器级副作用（可还原） | 中 | P2 |
| PORT-05 | 非 Windows ≈ 重写（WPF/WMI/COM/registry） | 低 | P3（文档声明即可） |

---

## 7. 文档真实性 / 发布闸门

| ID | 现象 | 风险 | 优先级 |
|---|---|---|---|
| DOC-05 | Doctor 在 scopes ready 时把 `visibleConcurrentWindows`/`googleAccountIsolation` 标 Verified，但 README/SECURITY 写并发**登录** E2E 未完成 | 中 | P1 |
| UX-14 | MANUAL_TEST_PLAN 仍是 Sandboxie 时代 | 高 | P1 |
| DOC-01 | ARCHITECTURE 写 WScript.Shell，代码是 IShellLinkW | 中 | P2 |
| DOC-03 | PROJECT_STATE 混排历史 “Current phase” | 中 | P2 |
| DOC-04 | KEYRING_ISOLATION_REPORT 主体是已删 Sandboxie 时代 | 中 | P2 |
| DOC-06 | 0.4.2 changelog「54 checks」「Publish sandbox 修正」抽查属实 | 低 | — |

发布链（`release.yml` + `Publish.ps1 -Release` + `Test-PackageStructure`）：干净树、精确 tag、CHANGELOG 条目、manifest release/dirty 校验——**比多数项目扎实**；缺口是未签名与 PR CI 不测安装器。

---

## 8. 本机验证记录

| 命令 | 结果 |
|---|---|
| `git log` / tree @ `7f518d8` | PASS：与 README 0.4.2 一致 |
| 人工复核 SEC-02/03/01/05、DOC-05、UX-14、SIMP-01 | PASS：与源码行号一致 |
| `./hub.ps1 test` | **FAIL**（环境）：NuGet `_GetRestoreSettings` → `path1` null；未取得 54/54 复跑证据 |

---

## 9. 优先级路线图（建议）

**P0（本审计无单独 P0 硬伤；安全闭合组按 P1 连打）**

**P1 本迭代**

1. SEC-01/02/03/05 + 文档改口（SECURITY 不得夸大）
2. SEC-07 pending 绑定 + 完成并发登录 E2E（README 能力表 ⚠️ 消掉）
3. UX-14 重写 MANUAL_TEST_PLAN；DOC-05 Doctor 降级/加 pending 能力
4. UX-01/02/10；AES-01 隔离列语义化
5. PORT-01：至少 Authenticode 或明确“实验包仅哈希校验”强化

**P2**

- SEC-04/06/08/09；SIMP-01/03/05；DEP-03/04/05；DOC-01/03/04
- AES-02/04/05；UX-03..06/08/09/11/12

**P3**

- SEC-10/11；SIMP-02/04/06/07；AES-03/06/08；PORT-05

---

## 10. 结论

0.4.2 是**有真实修复、有发布纪律的实验版**，不是“包装文档的空壳”。  
短板不在“有没有写安全注释”，而在：**热路径与 direct 路径的隔离证明缺口、日志子串自证可伪造、注册表命令可被数据根投毒、并发回调单槽、文档/Doctor 仍超前于代码、验收计划停留在已删除的 Sandboxie 时代**。

在完成 P1 之前，应继续按 README 自己写的警告对待：**不要把 0.4.1 修复后的并发登录当作已验收能力**；隔离是同一 Windows 账号内的账号状态隔离，不是安全沙箱。
