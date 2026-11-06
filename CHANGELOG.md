# Changelog

## Accounts 0.4.0-preview.8 — 2026-11-04

- 2026-11-06 发布整理：仓库更名为 `antigravity-accounts`，从当前目录移出旧 Profile Hub / Next 实现；改为独立 Accounts 构建、依赖锁定、包内容检查与 GitHub 发布。
- 按插件安装页结构重写中英文 README，补充安装、使用、恢复、兼容范围、授权数据和贡献说明。
- 增加 2.29.0 支持并保留 2.28.2；按受影响脚本及 RPC 依赖判断兼容性，版本号变化但接口内容相同时可继续安装。
- 从原生前端接口识别编译后的变量；用户状态改用原生 protobuf 等值比较，兼容 2.29 的状态去重。
- 加入本地更新修复和安装互斥；与现有中文启动保护按 Accounts → 中文的顺序协作，不覆盖官方更新为旧程序。
- 账号菜单保留操作焦点、滚动位置和中文输入法确认；长账号列表可滚动，继承原生主题与模型选项。
- 增加账号分组内的删除确认，允许运行中删除非当前附属账号；当前账号与主账号保留删除限制。
- 固定组件、启用标记及更新修复使用的实际账号目录，修复 Windows 目录映射导致普通启动显示空账号的问题。
- 真实生成、额度扣减和跨账号继续生成仍未验证；本版检查范围见 `docs/ACCOUNTS_VALIDATION.md`。

## Accounts 0.4.0-preview.7 — 2026-10-31

独立账号插件的 Windows x64 预发布，针对 Antigravity App 2.28.2 的已检查程序内容。

- 在原生模型列表中按账号分组，保留官方模型选项和 View Usage，继承宿主主题；不创建独立账号页面。
- 通过 Google 网页主动添加独立账号，以 Windows DPAPI 加密保存；使用 HostAuth 和版本限定的本机宿主适配，不导入原官方登录数据。
- 使用压缩自包含组件，减少对话文本更新引起的全页面菜单扫描；未知程序内容拒绝安装，保留原程序用于恢复。
- 已在2.28.2完成安装及3个账号分组的界面检查；本轮没有真实账号切换或模型请求。旧版本的元数据切换结果不作为本版通过标记。
- 当前仍依赖私有宿主接口，尚未在 Marketplace 收录，未来版本需另行检查。真实生成、额度扣减和到期续期尚未验证。

## Earlier Profile Hub releases

The retired Profile Hub / Next implementation and its release notes remain in [Git history](https://github.com/Aliarrrrr/antigravity-accounts/blob/00ddfb120ef1f4d032e4b81a450c62ff24d9e9c2/CHANGELOG.md). Existing historical tags and release assets are retained.
