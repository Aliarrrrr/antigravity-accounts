# 共享工作空间开发版

共享组将工作内容与已有 Profile 的账号登记分开。原来的 Profile 启动、登录和多开入口保留。

## 当前可用范围

- WPF 的“共享工作空间”窗口与 CLI 共用 Core 的组管理、成员管理、选择来源、导入预览、应用和撤销。
- 导入只处理选中的项目文件、会话数据库、同会话 brain / annotations 及明确选中的知识目录；源 Profile 不修改。
- 会话文件的保存和复制不代表已确认能跨账号续聊。客户端账号切换、身份确认、原会话恢复尚未完成真实客户端检查，生产入口保持不可用。
- 不提供凭据存储、捕获或替换接口，不自动获取账号邮箱。账号标签手填，官方客户端自行管理登录状态。
- 切换协调逻辑通过模拟客户端检查等待任务、取消等待、位置恢复和失败处理。模拟结果不代表 Antigravity 已支持这些行为。

## CLI

使用本次构建的 `agprofile-next.exe`，并为试用指定独立数据根目录。以下命令均可加 `--json`：

```powershell
agprofile-next group create 工作组 --data-root D:\HubSharedTrial
agprofile-next group add-account 工作组 账号A --data-root D:\HubSharedTrial
agprofile-next group catalog 账号A --data-root D:\HubSharedTrial
agprofile-next group preview-import 工作组 账号A --project <项目ID> --conversation <会话ID> --data-root D:\HubSharedTrial
agprofile-next group apply-import 工作组 <预览ID> --data-root D:\HubSharedTrial
agprofile-next group restore-import 工作组 <导入ID> --data-root D:\HubSharedTrial
agprofile-next group status 工作组 --data-root D:\HubSharedTrial
agprofile-next group capabilities 工作组 --data-root D:\HubSharedTrial
```

来源账号必须是指定数据根目录中已经登记的 Profile。先运行 `group catalog` 列出可选标识；未明确选中的内容不会自动导入。`--knowledge` 单独选择知识目录与 `config/memory.txtpb` 记忆文件；它们分别判断冲突，不合并或覆盖不同内容。

项目只支持已观察的 `config/projects/<id>.json` 文件；未确认的项目目录在读取目录内容前标记 `Blocked`，避免递归带入账号文件或未选内容。目标端同 ID 目录视为冲突，不读取或覆盖。项目引用的其他环境、权限配置以及未选择的会话不会自动纳入。项目登记和已确认的摘要索引用于检查会话关系：缺少所需会话、同 ID 会话内容冲突、关联字段拼写或格式未知、引用未确认的环境时，项目在预览中标记 `Blocked` 并说明原因。会话先导入，项目后导入。

本机 App 2.24.0 的单会话数据库已在隔离副本中确认 `user_version=1` 及七张表结构；预览检查格式、内部 `cascade_id` 与文件 ID、SQLite 完整性。摘要索引已确认 `user_version=3`，其中还含其他会话及全文搜索正文，因此**不整库复制、不合并索引**。annotations 可选，存在时与同 ID 数据库、brain 一起判断冲突。

当前导入提供所选文件的保留与撤销，不提供官方客户端的恢复或跨账号续聊。预览和结果固定返回 `ClientRestoreValidated=false`，并列出索引尚未导入等限制。`Copy` / `Applied` 只描述文件处理结果。

`group switch <组> <账号>` 与 `group recover <组>` 在客户端适配未确认时返回 `shared_client_unsupported` 及非零退出码，不启动、注销或更改官方客户端。

## 数据和恢复

组位于数据根目录的 `shared-groups/<组ID>`，共享内容位于其 `content/.gemini`。普通组登记仅保存 Profile ID；没有 Hub 凭据目录。安装身份、整个 `.gemini`、Chromium 用户目录和账号缓存均不纳入导入。

预览与应用分开。来源或目标在预览后变化时，必须重新预览。相同文件内容跳过，不合并不同内容的会话数据库；会话数据库、brain 和 annotations 作为同一单位处理。带非空 WAL、SHM 或 journal 的来源要求关闭源客户端后重新预览，不能直接复制活动 SQLite 文件。

撤销只移除本次新建且此后没有变化的文件。已经被修改的文件保留并报告；原 Profile 和外部项目目录保留。组已连接客户端、正在切换或状态无法确认时不应用导入。

## 真实客户端检查前

先使用合成数据及隔离工作副本。真实账号及模型请求仍须另行授权：列出实际服务地址、官方客户端使用已有登录状态的方式、测试内容、认证请求与模型请求各自的预计数量及预算。授权不会把读取账号秘密加入 Hub 的职责。当前未使用真实账号或发送模型请求。

先检查原服务选择既有身份；没有可用入口时，再研究保留官方窗口、用目标 Profile 环境重启语言服务。只有认证路径和工作路径能分开，并且官方侧能确认实际目标身份时，才实施运行适配。单纯更改 HOME 不能证明 Windows keyring 已随 Profile 切换，详见 [客户端检查](SHARED_CLIENT_COMPATIBILITY.md)。

完成前必须检查 A→B→A 的实际身份、同一窗口和项目 / 会话 ID、历史、草稿与位置、任务等待及失败恢复。不得用浏览器确认、新会话摘要或隐藏多个实例替代。失效登录、模型权限不足和未知状态均停止切换，不自动登录、换模型或重发消息。

协调器在身份变更后失败时不再次调用旧账号切换。它只尝试重连仍可用的原官方服务环境，再确认原身份和上下文；无法确认时清空活动账号并保持 `RecoveryRequired` / 暂停发送。模拟客户端检查此逻辑，生产适配尚未启用。

只对检查过的版本实施适配；与现有汉化共同生成结果，不分别覆盖安装包。官方更新后先停用未确认适配，并保留原版恢复入口。本版本尚未修改官方安装包或默认多开入口。
