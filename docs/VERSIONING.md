# 版本、构建与回滚

## 单一版本与依赖

产品版本只在根目录 `Directory.Build.props` 的 `Version` 中维护。项目程序集、`hub.ps1 version`、发布目录、压缩包及 manifest 都从它派生；不要在项目文件或脚本中另写产品版本。CHANGELOG 中的版本是历史记录，NuGet 包和 SDK 版本不是产品版本。

采用 SemVer：修复用 PATCH，向后兼容功能用 MINOR，不兼容变更用 MAJOR；`0.x` 阶段仍需在变更记录中明确不兼容点，预发布可用 `-rc.1` 等后缀。数据格式兼容性需单独声明，不能仅从产品版本推断。

- `global.json` 以稳定版 .NET SDK 10.0.400 为基线，`latestFeature` 允许同一 10.0.4xx feature band 内更高的补丁；不接受预览 SDK，也不滚到 .NET 11。CI 通过 `setup-dotnet` 的 `10.0.x` 取当期补丁。
- 默认目标框架、Nullable、ImplicitUsings 与依赖锁定开关集中在 `Directory.Build.props`；WPF GUI 显式保留 `net10.0-windows`。
- `System.Management 10.0.0` 是必要的 WMI 依赖，不应为使还原通过而删除。
- `NuGet.Config` 清除隐式继承的源，仅使用 nuget.org；不要把源凭据或 Token 写进配置和发布包。
- 四个项目各自的 `packages.lock.json` 必须审阅并纳入版本控制。首次建立或有意更新依赖时，先运行非锁定 `build`，检查锁文件变化，再运行 `build -LockedRestore -Test`。不要忽略锁文件，也不要在 CI 中临时生成锁文件来冒充锁定还原。
- `win-x64` 运行标识在 `scripts/Publish.ps1` 中声明并用于构建与发布；锁文件缺失或过期时，锁定构建和 CI（同样使用 `-LockedRestore`）失败，不能视为发布就绪。

## 分支与日常工作

保留现有 `master` 作为集成分支；如果仓库以后采用 `main`，相同流程与 CI 同时适用，不要求先改名。功能使用短生命周期的 `feature/...`，修复使用 `fix/...`，通过 PR 审阅后合并，避免长期分叉。构建发布脚本不执行 commit、push 或 tag，也不操作相邻项目。

在 PowerShell 5.1 或 7 中，从项目根目录调用：

```powershell
.\hub.ps1 help
.\hub.ps1 version
.\hub.ps1 check
.\hub.ps1 build
.\hub.ps1 build -LockedRestore -Test
.\hub.ps1 test -LockedRestore
.\hub.ps1 test -LockedRestore -IncludeSystemTests
```

`test` 及 `build -Test` 运行控制台测试器，即 `dotnet run --project tests/AGProfileHub.Tests -c Release --no-build --no-restore`，不是 `dotnet test`。系统测试必须显式传入 `-IncludeSystemTests`，脚本将其转为测试器的 `--include-system`，该开关同时隐含 `-Test`。默认用例只使用自建项目内测试目录和模拟进程；凭据写入与 COM 快捷方式测试默认显示 SKIP，仅显式启用。任何失败或非零退出码都向上传递，并阻止后续发布。

只有显式使用下列入口才会启动真实程序，构建和环境检查不启动 GUI/CLI 应用：

```powershell
.\hub.ps1 run -DataRoot 'D:\HubData'
.\hub.ps1 cli -DataRoot 'D:\HubData' -CliArgs @('list', '--json')
```

`run` 启动 GUI，`cli` 将 `CliArgs` 数组逐项传入 CLI，不拼接可执行命令字符串。两者先构建；相对 `DataRoot` 按调用者当前目录解析，随后才切换到项目根目录。未指定 DataRoot 时，由应用选择默认数据目录。

脚本不需要管理员权限，不设置执行策略，不使用 `ExecutionPolicy Bypass`。如本机策略禁止未签名脚本，应按所在环境的签名/审批流程处理，不绕过组织策略。

## SDK 发现和临时环境

依次查找 `AGPH_DOTNET`、项目 `.tools/dotnet/dotnet.exe`、`DOTNET_ROOT`、PATH 上的 `dotnet`、`ProgramFiles/dotnet/dotnet.exe`。`AGPH_DOTNET` 可指定可执行文件或安装目录；显式指定却无效时直接报错，避免悄悄选错 SDK。其他候选不存在或只有 runtime 时继续查找。

候选必须在 `--list-sdks` 中拥有稳定版 .NET 10 SDK，并且在项目根目录执行 `--version` 时通过 `global.json` 选中稳定版 10.0 SDK。已有 SDK 直接复用，脚本不会安装 SDK。

执行期间临时使用项目根目录作为工作目录，CLI home 为 `.dotnet-home`，NuGet 包与 HTTP 缓存位于 `.nuget`；临时设置 SDK 根目录与 PATH。无论成功或异常，均恢复调用者工作目录及修改过的进程级环境变量；不更改系统/用户级环境。缓存、bin、obj 和 artifacts 留在本地且不纳入版本控制。

## 发布与 CI

```powershell
# 开发包可来自有改动的工作区，manifest 如实标记 dirty。
.\hub.ps1 publish
.\hub.ps1 publish -FrameworkDependent -LockedRestore

# 已完成下面的人工版本准备流程后，才执行正式发布。
.\hub.ps1 publish -Release
```

每次发布先构建和测试，再分别还原/发布 CLI 与 GUI。正式发布隐含锁定还原；开发发布可选择 `-LockedRestore`。默认是自包含 win-x64，`-FrameworkDependent` 则要求目标机预先具有 .NET 10 Desktop Runtime。SDK 与 runtime 不是一回事。

正式版本顺序：

1. 完成 PR 审阅、集中版本调整、依赖锁文件和 CHANGELOG 条目；核对自动化测试、系统测试与人工验收各自的证据，不混为一谈。
2. 在拟发布提交上运行锁定构建和测试。修复任何失败后重新执行，成功前不要打发布标签。
3. 由有权限的维护者在通过验证且工作区干净的提交上创建与版本精确对应的 `v<版本>` 标签，并按仓库流程推送。脚本本身不创建、移动或删除标签。
4. `publish -Release` 检查工作区（含未跟踪文件）严格干净、HEAD 上存在精确且区分大小写的 `v<版本>` 标签、CHANGELOG 有对应版本条目；发布前后均检查，构建期间 HEAD/版本变化则拒绝交付。
5. Windows CI 在 `master`/`main`、PR 与 `v*` 标签触发，使用 checkout 与 setup-dotnet 的稳定版 `10.0.x`，执行 `Build.ps1 -LockedRestore -Test`。标签工作流执行 `Publish.ps1 -Release`（隐含锁定还原与全部门禁），并在交付前断言 manifest 的 `release=true`、`dirty=false`、版本与标签一致。仅成功标签任务上传产物并创建 GitHub Pre-release（`contents: write`，`gh release create`，不标记 latest）；失败、缺锁、错误标签均不能交付。Actions 引用使用主版本 tag，dependabot 周期性提供更新 PR。

正式交付目录为 `artifacts/package/<版本>/<UTC时间戳>-<随机标识>/`，每次独立，不覆盖旧目录。中间产物只放在 `artifacts/.staging`，失败时不移入交付目录、不上传；排障后可由维护者清理失败的 staging。

包保持 `AGProfileHub/hub`、`AGProfileHub/cli`、`Start Hub.cmd` 与 `agprofile.cmd` 布局。manifest 的 `schemaVersion` 为 2，包含产品版本、完整 commit、dirty、runtime、UTC builtAt、SDK 版本及自包含/正式发布标志；这是**发布清单** schema，不等同于用户配置 schema。ZIP 旁生成标准 `.sha256` 与兼容的 `.sha256.json`。验证示例：

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '<实际发布压缩包路径>'
```

比较实际摘要和同目录校验文件后再使用。每个目录互相独立，重复运行不会改写旧包。可选 `Probe-Antigravity.ps1` 存在才复制，缺失不影响打包，发布不会执行它。只复制编译产物和明确列出的说明文件/可选脚本，绝不递归打包工作区、用户配置、数据根、缓存、Token 或凭据。

manifest 的 `authIsolation` 与 `externalDependency` 描述当前真实机制（每 Profile 独立 home + 文件令牌存储，唯一外部依赖为官方 Antigravity Desktop 2.12.2）；`Test-PackageStructure.ps1` 会断言这些字段不得再出现已删除的沙盒产品。自动化构建通过不代表完成真实双账号并发登录的人工验收，CHANGELOG 或 manifest 不能替代验收记录。

## 回滚代码、程序目录与数据

- 代码回滚使用 `git revert` 生成可审阅的逆向提交，再走构建测试与新的补丁版本流程；不要通过重写共享历史或移动已发布标签伪装回滚。
- 程序目录并行保留：新版本解压到新目录，不覆盖旧版。需要回退时先关闭新版本，再切换快捷方式/调用路径到旧目录。不要让两个不同版本同时写同一份数据。
- 用户配置 schema 当前为 1。将来若升级到 v2，必须在升级**之前**关闭所有会写数据的程序，为整个数据根制作独立、带日期、可恢复的预升级备份，放在发布目录之外并限制访问权限。备份可能含敏感数据，不能上传到仓库或放入 ZIP。
- `.bak` 仅表示最近一次写入前的状态，后续每次写入都可能替换它；它不是升级前的长期快照，不能当作完整回滚保证。
- 若未来出现 v2：老程序不得写入 v2 配置。若旧程序不兼容 v2，回滚必须切换到独立恢复的 v1 备份数据根，或使用经过明确验证的数据迁移工具；不能仅把 schema 数字改回 1，也不能直接让旧程序打开并写当前 v2 数据。
- 预升级备份恢复会舍弃该快照之后的数据变化，需由维护者单独确认处理；二进制目录回退不等于安全的数据回退。
