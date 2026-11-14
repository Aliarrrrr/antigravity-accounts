# 第三方来源与许可

本工具新增的构建、部署、原生补丁与运行时实现采用根目录 [LICENSE](LICENSE) 的 MIT 许可。该许可不替代复用内容、npm 依赖或 Antigravity 官方文件各自的许可。

## 汉化字典与参考替换

- 来源：[liominsb/Antigravity-Chinese-Localization](https://github.com/liominsb/Antigravity-Chinese-Localization/tree/b541ec6654a068beb476fef07ed437786908fdb2)。
- 固定提交：`b541ec6654a068beb476fef07ed437786908fdb2`。
- 复用范围：`localize.js` 内的汉化字典，以及经审查后重新实现的界面文字替换。
- 上游版权：`Copyright (c) 2026 aabbWei`；许可为 MIT，完整原文保存在 [LICENSE.upstream](LICENSE.upstream)，再分发复用内容时须保留。
- 字典还合入旧插件 `old294` 提供的缺失条目与后续观察所得的界面条目，未用旧条目覆盖固定上游的已有条目。实际条目数量记录于构建产物 `manifest.json`。

这里只隔离读取了字典对象，未执行上游顶层安装器、服务端、下载、启动或进程终止逻辑。本工具的 MIT 声明不表示重新许可第三方原始内容。

## npm 依赖

直接运行依赖为 `@electron/asar` `4.3.1`（MIT；`Copyright (c) 2014 GitHub Inc.`），完整许可见 [随包 LICENSE.md](node_modules/@electron/asar/LICENSE.md)。开发依赖为 `jsdom` `26.1.0`（MIT；`Copyright (c) 2010 Elijah Insua`），完整许可见 [随包 LICENSE.txt](node_modules/jsdom/LICENSE.txt)。jsdom 只用于测试，运行时翻译载荷不引入这些 npm 依赖。

以下版本及许可标识来自当前 `package-lock.json`，许可正文与版权声明以随包文件为准。分发 `node_modules` 时保留这些原始许可和声明；本表不替代它们。`npm ci` 按锁文件安装，不应改用浮动版本替换交付依赖。

| 包 | 锁定版本 | 许可 | 随包许可文件（相对工具根） |
| --- | --- | --- | --- |
| @asamuzakjp/css-color | 3.2.0 | MIT | `node_modules/@asamuzakjp/css-color/LICENSE` |
| @csstools/color-helpers | 5.1.0 | MIT-0 | `node_modules/@csstools/color-helpers/LICENSE.md` |
| @csstools/css-calc | 2.1.4 | MIT | `node_modules/@csstools/css-calc/LICENSE.md` |
| @csstools/css-color-parser | 3.1.0 | MIT | `node_modules/@csstools/css-color-parser/LICENSE.md` |
| @csstools/css-parser-algorithms | 3.0.5 | MIT | `node_modules/@csstools/css-parser-algorithms/LICENSE.md` |
| @csstools/css-tokenizer | 3.0.4 | MIT | `node_modules/@csstools/css-tokenizer/LICENSE.md` |
| @electron/asar | 4.3.1 | MIT | `node_modules/@electron/asar/LICENSE.md` |
| acorn | 8.15.0 | MIT | `node_modules/acorn/LICENSE` |
| agent-base | 7.1.4 | MIT | `node_modules/agent-base/LICENSE` |
| balanced-match | 4.0.4 | MIT | `node_modules/balanced-match/LICENSE.md` |
| brace-expansion | 5.0.12 | MIT | `node_modules/brace-expansion/LICENSE` |
| cssstyle | 4.6.0 | MIT | `node_modules/cssstyle/LICENSE` |
| data-urls | 5.0.0 | MIT | `node_modules/data-urls/LICENSE.txt` |
| debug | 4.4.3 | MIT | `node_modules/debug/LICENSE` |
| decimal.js | 10.6.0 | MIT | `node_modules/decimal.js/LICENCE.md` |
| entities | 6.0.1 | BSD-2-Clause | `node_modules/entities/LICENSE` |
| glob | 13.0.6 | BlueOak-1.0.0 | `node_modules/glob/LICENSE.md` |
| html-encoding-sniffer | 4.0.0 | MIT | `node_modules/html-encoding-sniffer/LICENSE.txt` |
| http-proxy-agent | 7.0.2 | MIT | `node_modules/http-proxy-agent/LICENSE` |
| https-proxy-agent | 7.0.6 | MIT | `node_modules/https-proxy-agent/LICENSE` |
| iconv-lite | 0.6.3 | MIT | `node_modules/iconv-lite/LICENSE` |
| is-potential-custom-element-name | 1.0.1 | MIT | `node_modules/is-potential-custom-element-name/LICENSE-MIT.txt` |
| jsdom | 26.1.0 | MIT | `node_modules/jsdom/LICENSE.txt` |
| lru-cache | 10.4.3 | ISC | `node_modules/lru-cache/LICENSE` |
| minimatch | 10.2.6 | BlueOak-1.0.0 | `node_modules/minimatch/LICENSE.md` |
| minipass | 7.1.3 | BlueOak-1.0.0 | `node_modules/minipass/LICENSE.md` |
| ms | 2.1.3 | MIT | `node_modules/ms/license.md` |
| nwsapi | 2.2.28 | MIT | `node_modules/nwsapi/LICENSE` |
| parse5 | 7.3.0 | MIT | `node_modules/parse5/LICENSE` |
| path-scurry | 2.0.2 | BlueOak-1.0.0 | `node_modules/path-scurry/LICENSE.md` |
| path-scurry 的 lru-cache | 11.5.3 | BlueOak-1.0.0 | `node_modules/path-scurry/node_modules/lru-cache/LICENSE.md` |
| punycode | 2.3.1 | MIT | `node_modules/punycode/LICENSE-MIT.txt` |
| rrweb-cssom | 0.8.0 | MIT | `node_modules/rrweb-cssom/LICENSE.txt` |
| safer-buffer | 2.1.2 | MIT | `node_modules/safer-buffer/LICENSE` |
| saxes | 6.0.0 | ISC | npm 包的 `package.json` 声明；上游原文见下方链接 |
| symbol-tree | 3.2.4 | MIT | `node_modules/symbol-tree/LICENSE` |
| tldts | 6.1.86 | MIT | `node_modules/tldts/LICENSE` |
| tldts-core | 6.1.86 | MIT | `node_modules/tldts-core/LICENSE` |
| tough-cookie | 5.1.2 | BSD-3-Clause | `node_modules/tough-cookie/LICENSE` |
| tr46 | 5.1.1 | MIT | `node_modules/tr46/LICENSE.md` |
| w3c-xmlserializer | 5.0.0 | MIT | `node_modules/w3c-xmlserializer/LICENSE.md` |
| webidl-conversions | 7.0.0 | BSD-2-Clause | `node_modules/webidl-conversions/LICENSE.md` |
| whatwg-encoding | 3.1.1 | MIT | `node_modules/whatwg-encoding/LICENSE.txt` |
| whatwg-mimetype | 4.0.0 | MIT | `node_modules/whatwg-mimetype/LICENSE.txt` |
| whatwg-url | 14.2.0 | MIT | `node_modules/whatwg-url/LICENSE.txt` |
| ws | 8.22.0 | MIT | `node_modules/ws/LICENSE` |
| xml-name-validator | 5.0.0 | Apache-2.0 | `node_modules/xml-name-validator/LICENSE.txt` |
| xmlchars | 2.2.0 | MIT | `node_modules/xmlchars/LICENSE` |

当前 saxes `6.0.0` npm 包未带独立 LICENSE 文件；其固定版本上游保留了贡献者及继承自 sax 的 ISC 声明，完整文本见 [saxes v6.0.0 LICENSE](https://raw.githubusercontent.com/lddubeau/saxes/v6.0.0/LICENSE)。

## 官方应用与本地验收材料

Antigravity 的名称、官方 EXE、原始 ASAR、unpacked 文件及从中提取的文件仍归其原权利人及各自第三方所有，不由本工具 MIT 许可授权。`verification/baseline`、`backups` 和 `artifacts` 为本机构建、恢复与验收材料；本工具不是官方应用安装器。对外分发工具时，应避免连同官方原包、用户数据或本机验收材料一起打包。
