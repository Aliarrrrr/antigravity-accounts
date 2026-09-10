# 回退指南 — 2026-09-10 重构

## 版本标识

| 检查点 | 标签 | Commit | 内容 |
|---|---|---|---|
| 基线 | `baseline-20260910-1245` | `7cb5f6d` | 工作树 = 旧 v0.2.3（40fea45），构建 0 警告 0 错误，测试 34/34 |
| CP1 | `cp1-sandboxie-ini` | `529576f` | Sandboxie.ini 单一实现（+1 测试） |
| CP2 | `cp2-elevation-bridge` | `576adb1` | 提权内建 + Start.exe 加固（+1 测试） |
| CP3 | `cp3-isolation-setup` | `a331d55` | 幂等隔离状态机 + 直写收敛（+1 测试） |
| CP4 | `cp4-cli-verbs` | `d474615` | 新动词 + 名字寻址（+1 测试） |
| CP5 | `cp5-gui-rework` | `af76c96` | GUI 设计系统与交互 |
| CP6 | `cp6-docs-sync` | `557b2ff` | README / CHANGELOG 同步 |

## 回退到任一状态

```powershell
cd D:\mutigravity

# 回到重构前（v0.2.3 等价）
git reset --hard baseline-20260910-1245

# 回到某个检查点（保留其之前的全部工作）
git reset --hard cp3-isolation-setup

# 只撤销某一个检查点，保留其余（生成反向提交，历史不改写）
git revert <commit-sha>

# 查看差异再决定
git log --oneline baseline-20260910-1245..HEAD
git diff baseline-20260910-1245..HEAD
```

回退后重新验证：

```powershell
pwsh -NoProfile -File .\scripts\Build.ps1 -Test
```

## 数据库 / 配置 / 依赖回滚

- **数据库**：无（本项目无数据库）。
- **依赖版本**：未变更（.NET 10、无新增包引用，回退后 `dotnet build` 无需 restore 调整）。
- **配置 schema**：未变更。`profiles.json`、`settings.json`、`isolation\state.json` 的版本字段保持原值（schema 2 / 1），回退代码后旧数据可直接读取，无需迁移。
- **唯一系统级副作用**：若在 CP2 之后运行过 `hub new` / `hub fix` / `hub open`，可能已把 `OpenFilePath=` 裸规则写入 `C:\Windows\Sandboxie.ini`（每个隔离盒一段）。回退代码**不会**自动清除这些规则。清除方法（任选其一）：
  - 恢复写入时自动生成的相邻备份：`C:\Windows\Sandboxie.ini.agph-<时间戳>-*.bak`（提权复制回 `Sandboxie.ini` 后重启 `SbieSvc`）；
  - 或以管理员编辑 `C:\Windows\Sandboxie.ini`，删除对应 `[AGPH_*]` 段的 `OpenFilePath=` 行，重启 `SbieSvc`。
  - 建盒副作用（`AGPH_*` 盒段）可在 Sandboxie Plus 中删除，或 `SbieIni.exe delete <box>`。
- **临时产物**：`%LOCALAPPDATA%\AntigravityProfileHub\runtime\elevations\` 下的 `elevate-*.json` 可直接删除。

## 本次基线的特殊说明（重要）

本次重构开始时发现 `D:\mutigravity\.git` 已损坏（`refs/` 与大量对象丢失，且 GitHub 远端在本环境不可达）。处理：

- 工作树源码零丢失，完整快照保留在 `D:\mutigravity_snapshot_20260910-1240`（1055 文件）。
- 损坏的 `.git` 备份在 `D:\mutigravity_gitbackup_20260910-1240\`。
- 基线 commit `7cb5f6d` 是把当前工作树嫁接到可恢复的最近本地历史 `7a9f3cc`（来自 `D:\mutiagy\agphub` 的对象）上形成的；旧历史 `117fd7b..40fea45`（8 个提交）被折叠进该基线，内容完整但提交粒度不可恢复。
- 网络恢复后如需完整历史：`git fetch origin --tags` 后对照远端，不影响当前代码。
