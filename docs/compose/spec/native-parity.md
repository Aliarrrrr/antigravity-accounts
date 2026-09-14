---
feature: native-parity
status: delivered
updated: 2026-09-14
branch: feature/native-parity
commits: 8f177fe..08b4573
---

# Native Parity — 多账号体验对齐原生 Antigravity

## Report

**What was built** — 本分支在 0.4.4 之上交付多账号「像原生」体验切片：(1) Workspace 自动打开经只读探测确认 **无** 2.12.2 契约，改为 `open-workspace` 资源管理器打开 + 明确 Unsupported；(2) `iso-bridge` 把宿主 `.ssh`/`.gitconfig` junction/hardlink 进 Profile home，仅允许 `.agph-bridge.json` 标记的 reparse；(3) `conversations import|plan|ls` copy-only 导入/列出对话库；(4) deep-link pending 升为多槽 map（最多 4，无法归属 fail-closed）；(5) doctor/README/SECURITY/CLI 对齐。Project 实体仍属阶段二。

**Verification** — `dotnet build AGProfileHub.slnx -c Release --no-restore`：0 warning / 0 error。`dotnet run --project tests/AGProfileHub.Tests -c Release --no-build --no-restore`：**65/65 PASS**。评审发现 critical（未标记 reparse 被误采纳为 bridge）已修复并复审 PASS。`hub.ps1 restore` 在本机存在 PRE-EXISTING NuGet `path1` null（master 同样），构建用 `--no-restore` + 既有 assets。桌面 E2E（真 bridge/导入）仍需人工验收。

**Journey log** — 1) asar/argv 探测无开目录契约，不硬猜；2) PathSafety 白名单只认 marker；3) 测试夹具默认关 bridge，避免递归清理跟 junction 删宿主 `~/.ssh`；4) force 导入失败回滚 backup；5) worktree `git worktree add` 被沙箱拦，用户手动创建后继续。

## [S1] Problem

Hub 0.4.4 已能在同一 Windows 用户下并发多 Google 账号，但每个 Profile 的日常体验仍明显弱于原生 Antigravity：

1. **项目打不开**：`WorkspacePath` 只是元数据；`LaunchWithWorkspaceAsync` 直接 `UnsupportedCapabilityException`。原生会进入上次/绑定的项目，Hub 用户要手动 File → Open Folder。
2. **工具链丢失**：启动覆盖 `USERPROFILE/HOME` 到 Profile 合成 home，Agent 看不到宿主 `~/.ssh`、`~/.gitconfig`，Git/SSH 与原生不一致。
3. **旧对话带不进来**：原版 `~/.gemini/antigravity/conversations/*.db` + `brain/<id>/` 无法进入新 Profile；ADR-007 明确不导入身份，但对话内容本是用户数据。
4. **并发登录单槽**：`deeplink-pending.json` 单 last-writer；A/B 同时等待 OAuth 时后写覆盖先写，回调可能进错窗。
5. **对话不可见**：无法列出某 Profile 内已有会话，多账号同项目时缺少对照。

核心目的（用户确认）：**并发多账号使用体验与原生反重力一致**；同项目 Project 实体化属第二阶段，本轮不做。

## [S2] Design

五个可独立验收的子能力，共享同一安全红线（SECURITY.md）：不读/不写 token、Cookie、Credential Manager 内容；不伪造未验证的厂商启动参数；失败关闭。

### S2.1 Workspace 打开

**阶段 A — 只读探测（必须先做）**

对已钉扎的 `Antigravity.exe` 2.12.2 与 `resources/app.asar`、`language_server.exe` 做只读检索，寻找**可验证**的“启动时打开目录”契约（例如文档化 flag、asar 内 argv 解析）。禁止用未验证的位置参数试启动生产 Profile。

**阶段 B — 按探测结果二选一**

| 结果 | 行为 |
|---|---|
| 找到契约且手工验收通过 | `HubService.LaunchWithWorkspaceAsync` / `launch`/`open` 在 `WorkspacePath` 非空时把该参数写入 `LaunchPlan.Arguments`；仍禁止任意透传用户 argv（ADR-008） |
| 无契约 | 保持“不假装支持”（ADR-006）：`LaunchWithWorkspaceAsync` 继续明确 Unsupported；新增 `open-workspace <profile>`（CLI）与 GUI 行为：`explorer.exe "<WorkspacePath>"`；`status`/`doctor` 在已设置 workspace 时提示“不会自动打开，可用 open-workspace” |

错误码：契约路径沿用现有 launch 码；无契约时 `workspace_forwarding_unsupported`（与今日 UnsupportedCapability 文案对齐，CLI `--json` 稳定）。

### S2.2 工具链桥接（Junction → 宿主）

在 process-local Profile 的 `home` 下，为白名单条目创建指向**当前 Windows 用户真实 home** 的链接，使 `~/.ssh`、`~/.gitconfig` 等与原生同一来源。

**默认白名单（settings 可关/可缩，不可任意扩到系统目录）**

| 条目 | 类型 | 宿主源 |
|---|---|---|
| `.ssh` | directory junction | `%USERPROFILE%\.ssh` |
| `.gitconfig` | file hardlink（同卷）；跨卷则复制并标记 `copied` | `%USERPROFILE%\.gitconfig` |

**不变量**

- 仅创建**我们管理的** reparse：旁路标记文件 `profiles/<uuid>/home/.agph-bridge.json`（schemaVersion、每项 target 绝对路径、kind=junction|hardlink|copy、createdUtc）。无标记的 reparse 仍按现网策略拒绝（`reparse_point_rejected`）。
- `PathSafety.EnsureSafeDirectory` / 启动布局：跳过对**已标记桥接路径本身**的拒绝；祖先路径上的未知 reparse 仍拒绝。
- **删除只删链接/副本，永不删宿主目标内容**（junction 删除与目录递归删除必须区分）。
- 不桥接：token 路径、Credential Manager、浏览器 profile、`.gemini` 整树（避免对话/状态被两实例写穿）。
- Profile 运行中禁止 prepare/remove bridge（与 stop 状态机一致）。

**CLI / 面**

- `iso-bridge prepare <profile>`：创建缺失项；幂等。
- `iso-bridge status <profile>`：每项 ready / host_missing / broken_reparse / kind。
- `iso-bridge remove <profile>`：只拆链接。
- `fix` / `iso-prepare` 在 settings.`toolchainBridge.enabled`（默认 true）时自动 prepare。
- `doctor` 聚合 bridge 状态；GUI Isolation 列或详情展示 “Toolchain Ready/N/A”。

设置：`HubSettings.ToolchainBridge { Enabled, Entries[] }`，缺省 = 上表两项。

**威胁模型说明（写入 SECURITY 增补）**：同 Windows 用户本就能读宿主 `.ssh`；桥接提升的是**可发现性**（与原生一致），不是新的跨用户边界。风险是 Profile 内进程经 junction **写入**宿主密钥——与原生 Antigravity 写 `~/.ssh` 同级；不在本轮加 ACL 只读（会连带锁死原生）。若后续要只读，单开 ADR。

### S2.3 原版对话导入

**范围**：只导入**对话内容**，不导入登录态。

```text
conversations import <profile> [--from <dir>] [--dry-run] [--force] [--json]
```

| 项 | 规则 |
|---|---|
| 默认源 | `%USERPROFILE%\.gemini\antigravity`（可用 `--from` 覆盖，须为已有目录） |
| 拷贝集 | `conversations/*.db` + 对每个 `*.db` 同名的 `brain/<id>/`（若存在） |
| 不拷贝 | 任何 token/Cookie/凭据；`installation_id`；默认不拷 `antigravity_state.pbtxt`（避免搬 onboarding/迁移状态）；`knowledge/` 本轮不拷 |
| Profile 状态 | 必须已停止，否则 `profile_not_stopped` |
| 目标非空 | 目标 `home/.gemini/antigravity/conversations` 已有 `*.db` 时：无 `--force` 则 `import_destination_not_empty`；有 `--force` 则先把既有 `*.db` 与对应 `brain/<id>` **改名**到 `home/.gemini/antigravity/conversations-backup-<yyyyMMddHHmmss>/`，不删除 |
| 校验 | 每个 db 文件头必须为 `SQLite format 3\0`，长度 > 0；失败则中止且不留半套（先拷到 `import-staging-<uuid>` 再原子改名进 conversations） |
| dry-run | 列出将拷文件数、总字节、目标冲突项；不写盘 |
| 日志 | 只记路径与计数，不记库内容 |

安全边界：SECURITY 增补“conversation DBs are user content, copy-only, never parsed into Hub logs”。

### S2.4 并发登录多槽 pending

**存储**：`runtime/deeplink-pending.json` 从单条 `PendingCallback` 升级为映射：

```json
{
  "schemaVersion": 1,
  "entries": {
    "<profileId>": { "userDataPath": "...", "createdUtc": "..." }
  }
}
```

兼容：读到旧单条形状（根上直接有 `profileId`）时迁成单 entry map 再写回（一次迁移，可测试）。

**写入**：launch 登录等待路径改为 `UpsertPending(profileId)`，不再覆盖其他 Profile。上限 `MaxPendingEntries = 4`；超出拒绝并 `deeplink_pending_full`。

**清理**：仍只清本 Profile（stop / 失败 launch）；读取时丢弃 `> MaxPendingAge`（30 分钟）的 entry。

**路由 `callback`**

| 活 entry 数 | 行为 |
|---|---|
| 0 | 返回 0（现行为） |
| 1 | handoff 到该 Profile（现行为，user-data 仍由 ProfileId 推导） |
| ≥2 | 解析 URI query/fragment 中的 `state`（大小写不敏感）；若某 entry 记录了 `expectedState` 且相等 → 该 Profile；否则 **fail-closed** `deeplink_pending_ambiguous`，不猜测、不 last-writer |

`expectedState`：若本轮无法从厂商 UI 捕获 state，则 ≥2 时稳定 fail-closed，并在错误信息中列出等待中的 Profile 显示名（不含 URI 秘密）；探测/验收时**只记录 URI 结构骨架**（是否含 `state` 键），不落盘完整回调。单槽完成一侧再另一侧的流程仍可用。

可选增强（仅当探测证实可得）：在用户于 Profile 内发起登录后、Hub 能观察到本地回调前的某种安全信号时写入 `expectedState`；无信号则不做。

### S2.5 对话清单只读

```text
conversations ls <profile> [--json]
```

- 只读打开 `profiles/<id>/home/.gemini/antigravity/conversations/*.db`（FileShare.ReadWrite，允许桌面在跑时列出；写路径仍要求停止）。
- 每项：`id`（文件名去扩展）、`sizeBytes`、`lastWriteUtc`、可选 `title`（尝试读 `trajectory_metadata_blob` / 表内可识别 TEXT；**失败则 title=null，不抛**）。
- 不调用 `agentapi`、不启动 LS（避免 CSRF/运行时耦合）。
- 本轮 CLI-only；GUI 列表不做。

### S2.6 横切

- 新错误码进 README 错误表与 `--json` 稳定字段。
- `doctor`：workspace 打开模式、bridge 状态、pending entry 数量。
- 自动化：Core 可测路径全覆盖；涉及真 Antigravity 的行为标 manual acceptance（沿用 MANUAL_TEST_PLAN 风格新增一节）。
- 版本仍钉 2.12.2；探测结论写进本文件 Report（交付时）。

## [S3] Out of Scope

- **Project 实体**（项目 → 多 Profile 绑定、一键整组打开）— 阶段二，另开 feature。
- 导入登录 token / Cookie / 凭据；同步或改写云端会话。
- 修改 vendor 二进制、asar、注册表 scheme 以外的机器状态（scheme 仍仅 deeplink register/restore）。
- 桥接的 ACL 只读收紧、跨 Windows 用户隔离、`.gemini` 整树桥接。
- 对话导入 `knowledge/`、默认导入 `antigravity_state.pbtxt`、导出/备份产品化。
- 并发 OAuth 在无 state 时的自动猜窗；pending >4。
- GUI 对话浏览器；Agent Safe worktree；tray。

## Tasks

- [x] T1: Workspace 契约只读探测并记录结论 — acceptance: 在本 spec Report 或探测笔记中写明“有/无契约 + 证据位置”；无契约时给出 fallback 设计已覆盖的结论 (covers: S2.1)
- [x] T2: Workspace 打开实现 — acceptance: 有契约则 `open`/`launch` 进入项目且测试覆盖 LaunchPlan；无契约则 `open-workspace` CLI + 现 Unsupported 保留 + doctor 提示 (covers: S2.1; depends: T1)
- [x] T3: PathSafety/布局允许受管桥接 reparse — acceptance: 有标记的 junction 不被 `reparse_point_rejected`；无标记仍拒绝；单测覆盖 (covers: S2.2)
- [x] T4: iso-bridge prepare/status/remove + settings 默认白名单 — acceptance: 幂等 prepare；status 三态；remove 不删宿主内容；fix/iso-prepare 在 enabled 时调用；单测 (covers: S2.2; depends: T3)
- [x] T5: conversations import — acceptance: dry-run 不写盘；force 先备份；staging 原子替换；非 SQLite 头拒绝；运行中 Profile 拒绝；单测用临时目录伪 db (covers: S2.3)
- [x] T6: 多槽 pending 存储与清理 — acceptance: upsert 不覆盖他槽；stop/失败只清己槽；过期丢弃；旧单条 JSON 可迁移；上限 4；单测 (covers: S2.4)
- [x] T7: callback 多槽路由 — acceptance: 0/1/≥2 行为与 S2.4 表一致；ambiguous 时 fail-closed；user-data 仍由 ProfileId 推导；单测 (covers: S2.4; depends: T6)
- [x] T8: conversations ls — acceptance: 列出 id/size/mtime；title 尽力解析；损坏库不拖垮整次 ls；`--json` 稳定；单测 (covers: S2.5)
- [x] T9: doctor/README/CLI help/SECURITY 增补对齐 — acceptance: 能力表、错误码、安全边界与实现一致 (covers: S2.1, S2.2, S2.3, S2.4, S2.5; depends: T2, T4, T5, T7, T8)
- [x] T10: 全量构建与测试 — acceptance: `hub.ps1 build -Test`（或等价 dotnet test）全绿；记录命令与计数 (covers: S2.1, S2.2, S2.3, S2.4, S2.5; depends: T9)
