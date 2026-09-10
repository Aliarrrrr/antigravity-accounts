# 根因证据与后续方向 — 供后续维护

> 历史记录（2026-09-08）：本文在固定 Credential Manager 目标被动态确认前写成。其“只能串行”的结论已由 `KEYRING_ISOLATION_REPORT.md` 和 ADR-012 取代；保留本文用于理解当时证据和继续取证的理由。

日期：2026-09-08。本文档自包含：所有结论附证据与复现命令，不需要重做考古。
当前权威状态见 `PROJECT_STATE.md` / `MANUAL_TEST_PLAN.md` / `CAPABILITY_REPORT.md`。

---

## TL;DR

当前最强诊断是：**Antigravity Desktop 2.12.2 的 Go 后端（language_server.exe）很可能通过 Windows 凭据管理器保存 Google OAuth 状态，且观察到的条目名为 `gemini:antigravity`**。凭据管理器按 Windows 用户划分，与 `--user-data-dir`、`USERPROFILE/HOME/APPDATA` 等目录覆盖不是同一边界。这能解释「B 首启显示 A 的会话」「并发时两窗口都显示后登录账号」「目录隔离看起来全部生效」。由于没有读取凭据内容，也没有动态跟踪具体 Get/Set 调用，本结论是高可信诊断，不是已验证的厂商存储契约。

在同一 Windows 用户且不修改厂商文件的前提下，现有证据没有显示可用环境变量能划分这个共享边界。当前交付选择 D5「串行管理器 + 单实例门禁」。D1–D4 都是独立后续项目，不能据此改变本版 `Incompatible` 结论。

---

## 1. 证据链（全部为已观察事实）

### 1.1 手工验收观察（MANUAL_TEST_PLAN.md）
| # | 观察 |
|---|---|
| A1 | A 登录 → A 经 Hub 停止/重启后会话保持（串行持久性通过） |
| A2 | **B 首次启动即显示 A 的会话**（B 用的是全新隔离目录） |
| A3 | B 登出后登录 B → B 重启后保持 B |
| A4 | A/B 并发运行 → **两窗口都显示后登录的账号（B）** |

推论：A2 说明登录态不在被隔离的两棵目录树内；A2+A3+A4 共同指向「按 Windows 用户共享、持久、last-write-wins 的单一账号存储」。

### 1.2 静态证据（本轮新增，已验证）
- **凭据管理器存在条目**：`CredEnumerateW` 枚举（仅名称，未读内容）得到 `type=1 target='gemini:antigravity'`。
- **二进制符号**（`resources/bin/language_server.exe`，155MB Go，google3 构建）：
  - `google3/third_party/golang/github_com/danieljoos/wincred`（Windows 凭据管理器客户端）
  - `github_com/zalando/go_keyring`（21 处；`keyring.Set`/`keyring.Get` 各 1 处；`GetGenericCredential` 1 处）
  - `windowsKeychain.Set` / `windowsKeychain.Delete` / `NewGenericCredential` / `GenericCredential.Write`
- 字面量 `gemini:` 不在二进制中（0 处）→ 条目名是运行时 `service + ":" + username` 拼接（go-keyring Windows 实现），service=`gemini`、username=`antigravity`。
- 无 `KEYRING` 类环境变量可命名空间化（0 处）。

### 1.3 Electron 侧证据（app.asar `dist/`，已提取精读）
- `constants.js`：`DYNAMIC_PORT = 0` → LS 端口由 OS 分配 → **排除端口冲突/串台假说**（探针也观察到两个独立 language_server 子进程）。
- `languageServer.js:224`：`const env = { ...process.env, ...shellEnvSync() }`；而 `node_modules/shell-env` 在 win32 直接 `return process.env` → 合并是空操作 → **排除「shell 环境覆盖合成环境」假说**，LS 确实继承 Hub 的合成环境。
- `languageServer.js:191-212`：LS 启动参数 `--standalone --override_ide_name antigravity --subclient_type hub --https_server_port 0 --csrf_token … --app_data_dir antigravity --api_server_url https://generativelanguage.googleapis.com --cloud_code_endpoint … --enable_sidecars`；`--app_data_dir` 的值 = `getAppDataDirName()` = **常量** `'antigravity'`（由 `app.getName()` 派生，Hub 无法通过命令行改变，因为它是 Electron main 硬编码传给 LS 的）。
- `paths.js`：Electron 侧路径全部基于 `os.homedir()`（Node 读 `USERPROFILE`，已被 Hub 覆盖）或 `app.getPath('userData')`（已被 `--user-data-dir` 隔离）→ Electron 侧目录隔离**真实生效**。
- `main.js:94-132`：`antigravity://` 协议注册无 Profile 标识；`handleDeepLink` 把回调发给本进程 `wins[0]`；OS 通过注册表命令（`Antigravity.exe "%1"`，**无 --user-data-dir**）拉起的新进程使用默认 userData。

### 1.4 目录元数据证据（仅文件名）
- 真实 `~/.gemini/antigravity/`（状态目录）与两个 Hub Profile 的合成 `home/.gemini/antigravity/` 均只观察到状态文件名（antigravity_state.pbtxt、conversations、knowledge…），未发现明显令牌文件。此结果支持系统凭据设施假说，但单凭文件名不能证明令牌不在文件系统。

### 1.5 排除清单
| 假说 | 结论 | 依据 |
|---|---|---|
| 端口冲突/窗口连到对方 LS | 排除 | `DYNAMIC_PORT=0`，OS 分配端口 |
| shell-env 覆盖合成环境变量 | 排除 | shell-env win32 返回 process.env |
| Electron session/cookie 串号 | 排除 | userData 每 Profile 独立 |
| LS 状态文件共享 | 排除 | 探针+目录证据：合成 home 内 .gemini 各自独立 |
| **认证状态使用按 Windows 用户共享的凭据设施** | **高可信诊断，未动态确认** | 1.2 的条目名与二进制符号能解释 A1–A4；未读取内容或跟踪调用 |

---

## 2. 当前诊断

> 静态证据表明 LS 后端包含 go-keyring/wincred 调用，并且当前 Windows 用户存在 `gemini:antigravity` 条目。结合 A1–A4，最可能的机制是两个 LS 实例访问同一用户范围认证状态。这个解释尚未通过不读取秘密内容的动态调用跟踪确认。

注意未证实的次级细节：username 段 `antigravity` 是常量还是来自 `--app_data_dir`，静态字符串无法定论（见 D1 实验）。但即使可参数化，该值由 Electron main 硬编码传递，Hub 在不修改厂商文件的前提下无法改变它（红线见 §6）。

---

## 3. 解题方向（按优先级）

### D1 — 确认 keyring 键来源（后续静态研究）
- 动作：只读分析 `keyring.Set` 调用点的 service/username 来源（constant vs flag/env），或等待厂商给出正式说明。本项目不修改 app.asar、后端二进制或协议注册表进行实验。
- 判定：若条目名随输入变化 → 存在参数化路径，重新评估；若恒为 `gemini:antigravity` → D1 关闭，转 D2。
- 预期：大概率关闭（无 KEYRING env、值硬编码）。

### D2 — 每 Profile 独立 Windows 身份（需单独立项）
- 机制：为每个 Profile 建立独立本地 Windows 账户，经 `CreateProcessWithLogonW`（或计划任务触发器）启动 Antigravity。凭据管理器、DPAPI、HKCU 注册表、`%LOCALAPPDATA%` 全部按账户隔离——**按构造正确，不依赖厂商配合**。
- 设计要点：
  1. 账户生命周期：创建/命名（如 `agprof-<uuid8>`）/禁用/清理；需要管理员权限（提权 UI 已有 `elevate.exe` 类似生态位）。
  2. 密码问题（红线：不得明文存密码）：推荐「计划任务」模式——首次以交互方式设置一次性凭据注册任务，之后 Hub 触发任务启动，无需重复输入、无需存储密码。
  3. 文件 ACL：Profile 数据目录 `icacls` 授权子账户访问；Hub 自身数据（runtime/logs/config）留在主用户。
  4. driver 改造点：`src/AGProfileHub.Core/Processes/ProcessAdapters.cs` 增加 `CreateProcessWithLogonW` launcher；`AntigravityDriver.BuildLaunchPlan` 增加身份上下文；环境覆盖策略在跨用户场景下需重审（子用户自己的 USERPROFILE 天然隔离，可保留合成 home 以对齐 Git/SSH 行为）。
  5. doctor 能力项新增 `windowsIdentityIsolation`，与 `ProbeStrategyRevision`（HubSettings 已有字段）联动。
- 成本：高（账户管理 UX、提权、ACL、测试矩阵翻倍）。收益：并发多账号真正成立。
- 复用：`PROJECT_STATE.md` release gate 已预留 "separate Windows identity boundary" 措辞。

### D3 — 登录流程与 OAuth 回调路由（无论 D1/D2 都需要设计）
- 问题 1：协议处理器无 Profile 标识，回调落到 OS 拉起的进程（默认 userData）的 `wins[0]`。
- 问题 2：并发恢复后，回调可能落到错误实例；OS 拉起的进程还会以默认 userData 产生一个**未托管实例**（Hub 的单根门禁只拦受管启动，不拦这个）。
- 候选方案：
  a. **登录串行门禁**：Profile 处于「待登录」状态时，Hub 临时恢复单根互斥（复用现有 `LockLaunchAsync` 门禁），登录完成即解除。
  b. **headless 粘贴码登录研究**：这会让 Hub 接触授权码并扩大安全边界，因此不属于当前版本；只有在单独安全设计和用户明确授权后才能评估。
  c. 检测并提示关闭未托管的协议回调实例（进程扫描已具备能力）。

### D4 — 上游请求（生态位，正解在厂商）
- 向 Google 提交证据包：凭据条目名、wincred/go-keyring 符号、`antigravity://` 无 Profile 标识、`--app_data_dir` 硬编码。请求：① 按 `--app_data_dir` 命名空间化凭据键；② 协议回调携带 Profile 标识；③ 官方多实例/Profile 契约。

### D5 — 维持现定位（兜底，已交付）
- 产品已是「串行多 Profile 管理器 + 单实例门禁」，诚实标注 Incompatible。若 D2 成本不可接受，保持现状即合理发布态。

---

## 4. 建议执行顺序（给后续 agent）

1. D1 判定实验（半天）→ 记录进 CAPABILITY_REPORT。
2. D3b headless 粘贴码登录可行性实验（1 天；不改厂商文件，用已有 headless 开关）。
3. 若 D3b 可行：实现登录流程 + doctor 能力项 + MANUAL_TEST_PLAN 更新。
4. D2 立项评估（若产品目标仍是并发多账号）。
5. 所有能力声明变化必须先通过干净的 A/B 手工验收（§5）。

## 5. 验收标准（重开 release gate 的条件）

- 每 Profile 独立登录后，凭据管理器出现**按 Profile 区分**的条目（或运行于独立 Windows 身份，按构造隔离）——枚举仅限条目名。
- 串行 + 并发 A/B：各窗口显示各自账号；互不覆盖；重启持久；登出不串扰。
- 现有 26/26 核心断言、12/12 CLI 元数据并发、12/12 实际进程集成和打包冒烟全部保持通过。
- doctor 的 `googleAccountIsolation` 仅在干净 A/B 通过后才允许升级出 `Incompatible`。

## 6. 安全红线（不得违反）

1. **不自动化 Google 登录**；一切认证操作由用户在官方界面完成。
2. **只枚举凭据条目名**；永不读取/移动/导出凭据内容、cookie、令牌。
3. **不修改厂商文件**（app.asar / language_server.exe / 协议注册表项）——破坏更新与本项目「不改 Vendor 配置」原则；一次性实验只允许在丢弃副本上做且不入库。
4. 两个 Profile 数据目录继续保留；`remove` 保持只注销。
5. 验收未通过前，不得在任何 UI/文档声称账号隔离。

---

## 附录 A — 本轮取证工具与命令（Windows，均只读）

```powershell
# 1) 提取 app.asar dist（Node 脚本见 %TEMP%\ag-inspect\extract.js，思路：解析 asar 头 JSON + base 偏移）
node extract.js "$env:LOCALAPPDATA\Programs\antigravity\resources\app.asar" out "dist" "package.json" "node_modules/shell-env"

# 2) 后端二进制定点字符串（快扫用 -F 固定串）
grep -a -c -F "danieljoos/wincred" language_server.exe   # vendor 路径
grep -a -o -E ".{0,60}wincred.{0,60}" language_server.exe | head   # 符号上下文

# 3) 凭据条目名枚举（只读名称！脚本全文如下）
python list_cred_names.py
```

```python
# list_cred_names.py — 仅打印凭据条目 Type 与 TargetName，绝不触碰 CredentialBlob
import ctypes
advapi32 = ctypes.windll.advapi32
Count = ctypes.c_ulong(); P = ctypes.POINTER(ctypes.c_void_p)()
class FILETIME(ctypes.Structure):
    _fields_ = [("dwLow", ctypes.c_ulong), ("dwHigh", ctypes.c_ulong)]
class CREDENTIAL(ctypes.Structure):
    _fields_ = [("Flags", ctypes.c_ulong), ("Type", ctypes.c_ulong),
        ("TargetName", ctypes.c_wchar_p), ("Comment", ctypes.c_wchar_p),
        ("LastWritten", FILETIME), ("CredentialBlobSize", ctypes.c_ulong),
        ("CredentialBlob", ctypes.c_void_p), ("Persist", ctypes.c_ulong),
        ("AttributeCount", ctypes.c_ulong), ("Attributes", ctypes.c_void_p),
        ("TargetAlias", ctypes.c_wchar_p), ("UserName", ctypes.c_wchar_p)]
if advapi32.CredEnumerateW(None, 0, ctypes.byref(Count), ctypes.byref(P)):
    arr = ctypes.cast(P, ctypes.POINTER(ctypes.POINTER(CREDENTIAL)))
    for i in range(Count.value):
        c = arr[i].contents
        print(f"type={c.Type} target={c.TargetName!r}")
    advapi32.CredFree(P)
else:
    print("CredEnumerateW failed, GetLastError =", ctypes.GetLastError())
```

## 附录 B — 代码落点速查

| 关注点 | 文件 |
|---|---|
| 启动参数/环境构建 | `src/AGProfileHub.Core/Driver/AntigravityDriver.cs` `BuildLaunchPlan` |
| CreateProcess 适配层（D2 需加 LogonW 版本） | `src/AGProfileHub.Core/Processes/ProcessAdapters.cs` |
| 单根门禁/登录串行复用点 | `src/AGProfileHub.Core/Processes/ProcessSupervisor.cs`（`LaunchCoreAsync`、`LockLaunchAsync`） |
| doctor 能力矩阵 | `src/AGProfileHub.Core/Services/HubService.cs` `DoctorAsync` |
| 策略版本 | `HubSettings.ProbeStrategyRevision` / `AuthIsolationAttested*`（Domain/Models.cs） |
| 验收协议 | `MANUAL_TEST_PLAN.md`（需增补凭据名枚举步骤，仅名称） |
