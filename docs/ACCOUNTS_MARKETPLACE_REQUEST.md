# Antigravity Accounts 市场申请与接口请求

2026-10-29已通过[Antigravity Plugin Interest Form](https://forms.gle/2EX5RFYPoJe1UgxR9)提交，Google页面显示“您的回复已记录”。这确认申请已提交，不代表审核通过、已上架或所需标准接口已获支持。

发布者为Aliarrrrr (Independent developer)，官网与联系人使用用户确认的GitHub资料。联系邮箱及公开支持邮箱已按用户明确指定填写，不写入本仓库。

[公开技术说明](https://gist.github.com/Aliarrrrr/a2070ce7fb58c2f05c308ec64096990e)已发布在Aliarrrrr账号下，正文与用户批准的`docs/ACCOUNTS_PUBLIC_OVERVIEW.md`完全相同。没有向表单上传程序、源码或本地账号检查记录。

## 已提交的插件说明

Antigravity Accounts is an independent Windows x64 preview that groups separately authorized Google accounts inside the native model selector, using host styling and preserving native model choices and View Usage. There is no separate account window; the official language server sends model requests directly to Google. Preview 0.4.0-preview.5 was locally checked on Antigravity 2.28.1 with two metadata-only account changes that restored the original account, project and model without restarting the App, language server or account component. The agent sent zero model requests. Generation, quota charging, expiry renewal and cross-account generation have not been verified. The current preview changes app.asar, uses private React/Redux interfaces, replaces the installed language-server program for a limited diagnostic route and runs a second copy with a project-cache change. It uses the matching desktop program's public OAuth client configuration; please confirm whether a separately registered client is required for third-party distribution. It is not yet a standard plugin-only or Marketplace-listed implementation. We request guidance on supported APIs for native account-group registration, native model/quota identity selection, safe idle switching and identity refresh, and on a standard Marketplace installation path for independent developers.

## 本地核对材料

- `artifacts/accounts/0.4.0-preview.5/installation-2.28.1.json`：本次安装、两次真实元数据切换及申请状态，不含邮箱、账号凭据或对话内容。
- `artifacts/accounts/0.4.0-preview.5/delivery/AntigravityAccounts/package-manifest.json`：分发内容和当前检查范围。
- `docs/ACCOUNTS_PLUGIN.md`：当前安装方式、独立授权、恢复及未确认项。

尚待Google确认：个人开发者的收录方式、原生模型菜单账号分组与额度身份接口、桌面OAuth客户端配置的分发要求，以及标准插件安装和更新兼容方式。
