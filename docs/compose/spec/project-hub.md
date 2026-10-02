---
feature: project-hub
status: designed
updated: 2026-09-14
branch: feature/project-hub
commits: # not implemented
---

# Project Hub — 同项目多账号管理（阶段二）

## Report

设计定稿，**本轮不实现**。依赖：`feature/native-parity`（workspace 元数据、`open-workspace`、多 Profile 并发、工具链桥接）。

## [S1] Problem

多账号并发已可用，但「同一个本地项目由多个 Google 账号同时开发」仍靠约定：用户要记住哪几个 Profile 绑了同一路径，每次手点 N 次 Open。缺少 Hub 级的「项目 → 账号组」对象与整组打开入口。

## [S2] Design

### S2.1 数据模型

新增 `config/projects.json`（schemaVersion=1），与 `profiles.json` 平级：

```json
{
  "schemaVersion": 1,
  "projects": [
    {
      "id": "uuid",
      "name": "工作台A",
      "workspacePath": "D:\\work\\repo",
      "profileIds": ["uuid-profile-1", "uuid-profile-2"],
      "createdUtc": "...",
      "updatedUtc": "..."
    }
  ]
}
```

| 规则 | 说明 |
|---|---|
| 不变式 | ProjectId 不可变；Name 可改且不得成为路径 |
| WorkspacePath | 必须为已存在绝对目录（`PathSafety.NormalizeWorkspace`） |
| ProfileIds | 必须是已注册 Profile；去重；上限建议 4（与 pending 槽对齐） |
| 删除 Project | 只删项目登记，**不**删 Profile、不删对话、不删 workspace 文件 |
| Profile remove | 从所有 Project 的 profileIds 中摘除（或校验时跳过缺失 id 并警告） |
| 与 WorkspacePath 字段关系 | Project 的 path **不**静默改写各 Profile 的 `WorkspacePath`；`project bind` 时若 Profile.WorkspacePath 为空则写入同一 path；若不同且非空则拒绝除非 `--force`（仅改 Profile 元数据） |

存储：`ConfigRepository` 增加 `ReadProjectsAsync` / `WriteProjectsAsync`，原子写 + schema 校验 + 文件锁，与现有 profiles/settings 一致。`HubJsonContext` 注册新类型。

### S2.2 CLI / 面（MVP）

```text
project new <name> <workspace-path>
project bind <project> <profile> [--force]
project unbind <project> <profile>
project ls
project show <project>
project open <project>     # 整组打开
project remove <project>
project rename <project> <new-name>
```

- 名字解析：显示名 / 唯一前缀 / UUID（与 Profile 相同策略，多匹配报错）。
- 全部支持 `--json`。

**`project open` 行为（MVP）**

1. 解析 Project；校验 workspace 目录仍存在。
2. 对每个 profileIds：若未 ready → `EnsureIsolationReadyAsync`（含 native-parity 工具链桥接副作用）。
3. 逐个 `LaunchAsync`（已有 `launch-all` 的 all-or-nothing 预检可复用：任一 not ready 则 **不启动任何一个**）。
4. **一次** `explorer.exe <workspacePath>`（不要 N 个资源管理器窗）。
5. 返回结构：每 Profile 的 LaunchResult + openedFolder。
6. 明确不调用 `LaunchWithWorkspaceAsync`（2.12.2 无契约）；GUI/CLI 提示「在 Antigravity 内手动 Open Folder」。

### S2.3 GUI（可第二迭代）

- 侧栏或第二列表：Projects；行内 Open / 编辑绑定。
- MVP 可仅 CLI；GUI 作 P2。

### S2.4 Doctor / 能力

- `doctor` 增加 `projectGroupOpen`：`Observed`（实现后）/ `Unsupported`（未实现）。
- `project open` 失败码：`project_not_found`、`project_workspace_missing`、`project_no_profiles`、`profile_not_in_project`。

### S2.5 安全边界

- 不读 token；不自动登录；不复制对话到「项目」目录。
- 项目不是隔离边界：同一 Windows 用户可访问 path。
- 不在 workspace 写标记文件（本 MVP 明确不做，避免弄脏用户仓库）。

## [S3] Out of Scope

- 按项目切分对话库 / 项目级会话索引  
- 在 workspace 内写 `.agph-project.json`  
- 自动在 Antigravity 内 Open Folder / 多根 workspace 的 vendor 契约  
- Git 协作（分支锁、同时写冲突检测）  
- 云同步、权限、跨 Windows 用户  
- 删除 Profile 时级联删项目外的任何数据  

## Tasks

- [ ] T1: ProjectsDocument + ConfigRepository 读写与校验 — acceptance: 原子写；未来 schema 拒绝；损坏不覆盖 (covers: S2.1)
- [ ] T2: ProfileManager.Remove 联动摘除 projectIds — acceptance: remove 后 project show 不含该 id (covers: S2.1)
- [ ] T3: CLI project new/bind/unbind/ls/show/rename/remove — acceptance: 解析与错误码稳定；--json (covers: S2.1, S2.2)
- [ ] T4: project open 整组 launch + 单次 Explorer — acceptance: 预检失败不启动；成功返回 N 个 LaunchResult；不调用 workspace 转发 (covers: S2.2; depends: T1, T3)
- [ ] T5: 单测 + README/CHANGELOG — acceptance: 测试覆盖绑定校验与 open 预检；文档与实现一致 (covers: S2.1, S2.2, S2.4; depends: T4)
