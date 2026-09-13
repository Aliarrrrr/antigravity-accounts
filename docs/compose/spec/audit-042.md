---
feature: audit-042
status: delivered
updated: 2026-09-13
branch: fix/0.4.3-p1
commits: cc56c08..HEAD  # audit report on master; remediation on fix/0.4.3-p1
---

# 0.4.2 第一性原理全量审计

## Report

**What was built** — 对 `7f518d8`（0.4.2）完成只读全量审计；随后在 `fix/0.4.3-p1` 完成 P1 安全/文档/UX 修复（见 CHANGELOG 0.4.3 Unreleased）。

**Verification** — 审计：源码交叉核对 PASS；`hub.ps1 test` 曾因 NuGet path1 失败。修复后：`dotnet build --no-restore` + 58/58 tests PASS。无 Antigravity 运行时验收。

**Journey log** — 1) worktree 被拦 → master 只读审计；2) 三路深读+亲核；3) 修复分支逐 commit 可回退；4) 热路径 attestation / pin / RouterCommand 重建落地；5) 待 E2E 并发登录与发布。

## [S1] Problem

0.4.1 外部审计的修复已在 0.4.2 落地，但缺少一次不依赖历史叙事、只依据当前源码/文档/脚本真实状态的全面重审。需要按用户指定维度给出客观发现、风险等级与优先级，避免理想化描述。

## [S2] Design

审计对象：`D:\mutiagy\antigravity-profile-hub` @ `7f518d8`（0.4.2），只读分析。

交付物：本文件 `Report` 区 + 同级 `audit-042-report.md`（完整矩阵）。

维度（必须全覆盖）：

1. 安全性 — 潜在漏洞与风险点（含进程归属、回调、可执行文件钉扎、路径、日志、凭据边界）
2. 可用性 — CLI/GUI 交互与 UX（错误信息、冷启动、并发登录、发现/自愈、无障碍）
3. 美观性 — GUI 视觉与信息层次（仅基于可审阅的 XAML/代码，不虚构运行截图）
4. 实现简洁性 — 复杂度、冗余、死代码、过度设计、测试是否真验行为
5. 环境依赖性 — .NET/WPF/WMI/注册表/外部 Desktop 版本/工具链/CI
6. 可迁移性 — 跨平台与部署成本（x64-only、自包含包、无签名、路径假设）
7. 其他 — 文档真实性、发布闸门、能力表与代码一致性、维护成本

每条发现格式：`ID | 维度 | 位置(file:line) | 现象 | 为何是问题 | 风险(高/中/低) | 优先级(P0/P1/P2/P3) | 建议`

风险等级定义：

- 高：可导致凭据/隔离失效、误杀进程、错误声明能力、发布被污染
- 中：显著 UX/维护/安全边界弱点，或验证缺口
- 低：风格、文案、局部冗余、可延后改进

优先级：P0 立即修；P1 本迭代；P2 有余力；P3 backlog。

约束：

- 每条可核验结论必须能指向当前源码/文档行号；无法核验的写为「未验证」而非当事实。
- 不把 SECURITY.md/README 的自述当作已实现证据；文档与代码不一致本身是发现。
- 不把 0.4.1 历史当作默认可信；只引用当前 HEAD 可见内容。

## [S3] Out of Scope

- 不修改业务代码、不修 finding、不打 tag、不发布。
- 不做真实 Antigravity 运行时验收（无 Desktop 环境时标注为代码级结论）。
- 不生成 UI 截图对比；美观性仅基于 XAML/资源与代码中的视觉决策。

## Tasks

- [x] T1: 写 Spec 与报告骨架 — acceptance: 本文件完整，任务可勾选 (covers: S2)
- [x] T2: 安全性深读 — acceptance: 每条 finding 有 file:line 与风险/优先级 (covers: S2)
- [x] T3: 可用性/美观性深读 — acceptance: CLI/GUI 发现可定位到代码 (covers: S2)
- [x] T4: 简洁性/依赖/可迁移/文档一致性深读 — acceptance: 覆盖 S2 维度 4–7 (covers: S2)
- [x] T5: 合成完整报告 — acceptance: audit-042-report.md 含全维度矩阵与总优先级 (covers: S2)
- [x] T6: 独立 Review — acceptance: 抽查至少 10 条 finding 的行号与表述，纠错并反馈 (covers: S2)
- [x] T7: Finalize 勾选任务并回填 Report — acceptance: status=delivered (covers: S2)
