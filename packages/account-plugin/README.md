# Accounts（本机宿主适配）

将独立授权的 Google 账号分组放在 Antigravity 原生模型列表中，使用官方模型选项及主题样式。没有独立账号页面。

当前预览需要完整 Windows x64 分发包及匹配 Antigravity 2.28.2 的 HostAuth 宿主适配。仅通过标准插件入口安装本目录，不会加载账号分组或切换功能；它只提供插件元数据。完整包的安装、独立 Google 网页授权和恢复方式见包根目录 `README.md`。

当前未在 Marketplace 上架，也不能保证未来宿主版本自动兼容。[公开技术说明及接口请求](https://gist.github.com/Aliarrrrr/a2070ce7fb58c2f05c308ec64096990e)记录了 preview.5 的实现及检查范围。preview.6调整了菜单监听；preview.7适配2.28.2，当前检查范围见包根目录 `VALIDATION.md`。
