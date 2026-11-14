import { createHash } from 'node:crypto';

export const WHITELIST = Object.freeze([
  'dist/preload.js', 'dist/menu.js', 'dist/tray.js', 'dist/ipcHandlers.js',
  'dist/updater.js', 'dist/main.js', 'dist/wsl.js', 'dist/provisionSplash.js',
  'dist/ideInstall/wizardPreload.js', 'dist/ideInstall/wizardHtml.js',
]);

const MARKER = 'ANTIGRAVITY_ZH_CN_NATIVE_V1';
const menuLabels = {
  File: '文件', Edit: '编辑', View: '视图', Window: '窗口', Help: '帮助',
  'New Window': '新建窗口', Docs: '使用文档', 'Toggle Developer Tools': '开发者工具',
  'Check for Updates': '检查更新', 'Checking for Updates...': '正在检查更新...',
  'Downloading Update...': '正在下载更新...', 'Restart to Update': '重启以应用更新',
  Undo: '撤销', Redo: '重做', Cut: '剪切', Copy: '复制', Paste: '粘贴', 'Select All': '全选',
  Minimize: '最小化', Close: '关闭', 'Quit Antigravity': '退出 Antigravity',
  'About Antigravity': '关于 Antigravity', Services: '服务', 'Hide Antigravity': '隐藏 Antigravity',
  'Hide Others': '隐藏其他', 'Show All': '显示全部', 'Force Reload': '强制重新加载', Reload: '重新加载',
  'Actual Size': '实际大小', 'Zoom In': '放大', 'Zoom Out': '缩小', 'Toggle Full Screen': '切换全屏',
  'Toggle Fullscreen': '切换全屏', 'Reset Zoom': '重置缩放', 'New Conversation': '新建对话',
  'Create Project': '创建项目', 'New Project': '新建项目', 'Create New Project': '创建新项目',
  'Open Project': '打开项目', 'Command Palette': '命令面板', Split: '分屏', 'Split Right': '向右分屏',
  'Split Down': '向下分屏', 'Replace With New': '替换为新建', 'Remove From Split': '从分屏中移除',
  'Split Terminal': '拆分终端', 'Split Conversation Vertically': '垂直分屏对话',
  'Split Conversation Horizontally': '水平分屏对话', 'Equalize Split Panes': '均分分屏窗格',
  Fork: '派生', 'Fork Conversation': '派生对话', 'Connect to WSL': '连接到 WSL', 'Reopen Locally': '本地重新打开',
};
const contextLabels = {
  Cut: '剪切', Copy: '复制', Paste: '粘贴', 'Select All': '全选', Undo: '撤销', Redo: '重做',
  Delete: '删除', 'New Conversation': '新建对话', 'Fork Conversation': '派生对话', Rename: '重命名',
  Pin: '置顶', Unpin: '取消置顶', Close: '关闭', 'Close Others': '关闭其他', 'Close All': '全部关闭',
  'Copy Path': '复制路径', 'Copy Relative Path': '复制相对路径',
  'Reveal in File Explorer': '在文件资源管理器中显示', 'Reveal in Finder': '在访达中显示', 'Open in Terminal': '在终端中打开',
};

const menuDefinitions = `
// ${MARKER}
const menuTranslationMap = Object.freeze(${JSON.stringify(menuLabels)});
function translateMenu(menuItem) {
    const distroSubmenu = menuItem.label === 'Connect to WSL' || menuItem.label === '连接到 WSL';
    if (Object.hasOwn(menuTranslationMap, menuItem.label)) menuItem.label = menuTranslationMap[menuItem.label];
    if (menuItem.submenu && !distroSubmenu) {
        const items = Array.isArray(menuItem.submenu) ? menuItem.submenu : menuItem.submenu.items;
        items?.forEach(translateMenu);
    }
}
`;
const contextDefinitions = `
// ${MARKER}
const contextMenuTranslationMap = Object.freeze(${JSON.stringify(contextLabels)});
function translateContextLabel(label) {
    return Object.hasOwn(contextMenuTranslationMap, label) ? contextMenuTranslationMap[label] : label;
}
`;

// Exact reviewed fragments. Source compatibility separately checks their containing
// functions, owning calls and dependencies; replacements never use fuzzy matching.
export function nativePatchOperations(payload) {
  const operations = [];
  const add = (entry, old, replacement, expected = 1) => operations.push({ entry, old, replacement, expected });
  const electronImport = 'const electron_1 = require("electron");';
  const preloadAnchor = "electron_1.contextBridge.exposeInMainWorld('wsl', wslAPI);\n";
  add('dist/preload.js', preloadAnchor, preloadAnchor + `
// ${MARKER}
(() => {
    try {
        if (window.top !== window) return;
        let started = false;
        const start = () => {
            if (started) return;
            started = true;
            try {
                void Promise.resolve(electron_1.webFrame.executeJavaScript(${JSON.stringify(payload)})).catch(() => {});
            } catch {}
        };
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
        else start();
    } catch {}
})();
`);
  add('dist/menu.js', electronImport, electronImport + menuDefinitions);
  add('dist/menu.js', 'const submenuItem = appMenu.items.find((item) => item.label === submenuLabel);',
    'const submenuItem = appMenu.items.find((item) => item.label === submenuLabel || item.label === menuTranslationMap[submenuLabel]);');
  add('dist/menu.js', 'electron_1.Menu.setApplicationMenu(menu);',
    'menu.items.forEach(translateMenu); electron_1.Menu.setApplicationMenu(menu);', 2);
  add('dist/menu.js', 'const action = updater_1.updateActions[menuItem.label];',
    'const action = updater_1.updateActions[Object.keys(updater_1.updateActions).find((key) => menuTranslationMap[key] === menuItem.label) ?? menuItem.label];');
  add('dist/menu.js', "return { label: 'Connect to WSL', submenu };", "return { label: '连接到 WSL', submenu };");
  add('dist/menu.js', "return { label: 'Reopen Locally', click:", "return { label: '本地重新打开', click:");
  add('dist/tray.js', "countItem.label =\n                (count > 0 ? `${count}` : 'No') +\n                    ' agent' +\n                    (count === 1 ? '' : 's') +\n                    ' running';",
    "countItem.label = count > 0 ? `${count} 个智能体运行中` : '没有智能体在运行';");
  add('dist/tray.js', 'contextMenu = electron_1.Menu.buildFromTemplate(actions);', `contextMenu = electron_1.Menu.buildFromTemplate(actions.map(action => ({
        ...action,
        label: action.label === 'No agents running' ? '没有智能体在运行' :
            action.label === \`Open \${electron_1.app.getName()}\` ? \`打开 \${electron_1.app.getName()}\` :
            action.label === 'Quit' ? '退出' : action.label,
    })));`);
  add('dist/provisionSplash.js', '<div>Setting up WSL: ${escapeHtml(distro)}</div>', '<div>正在配置 WSL: ${escapeHtml(distro)}</div>');
  add('dist/ipcHandlers.js', electronImport, electronImport + contextDefinitions);
  add('dist/ipcHandlers.js', "title: 'Open workspace',", "title: '打开工作区',");
  add('dist/ipcHandlers.js', "title: 'Open workspaces',", "title: '打开工作区',");
  add('dist/ipcHandlers.js', "electron_1.dialog.showErrorBox('Cannot open folder', t.error);", "electron_1.dialog.showErrorBox('无法打开文件夹', t.error);");
  add('dist/ipcHandlers.js', "message: 'Folder is on the Windows filesystem',", "message: '文件夹位于 Windows 文件系统中',");
  add('dist/ipcHandlers.js', "label: item.label ?? '',", "label: translateContextLabel(item.label) ?? '',", 2);
  add('dist/wsl.js', "warning: 'This folder is on the Windows filesystem. Accessing it from WSL (via /mnt) can be slow — for best performance keep projects inside the WSL filesystem.',",
    "warning: '此文件夹位于 Windows 文件系统。从 WSL 访问（通过 /mnt）可能较慢 — 为获得最佳性能，建议将项目保留在 WSL 文件系统中。',");
  add('dist/wsl.js', 'error: `This folder belongs to the WSL distro "${unc[1]}", but this window is connected to "${distro}".`,',
    'error: `此文件夹属于 WSL 发行版 "${unc[1]}"，但当前窗口连接到 "${distro}"。`,');
  add('dist/wsl.js', 'error: `This location cannot be opened in WSL: ${winPath}`', 'error: `无法在 WSL 中打开此位置: ${winPath}`');
  add('dist/wsl.js', "onStatus?.('Downloading the Antigravity binary\\u2026');", "onStatus?.('正在下载 Antigravity 二进制组件\\u2026');");
  add('dist/wsl.js', 'onStatus?.(`Installing into ${distro}\\u2026`);', 'onStatus?.(`正在安装到 ${distro}\\u2026`);');
  for (const [old, replacement] of [
    ["title: 'WSL distro not found',", "title: '未找到 WSL 发行版',"],
    ['message: `The WSL distro "${WSL_DISTRO}" is no longer installed.`,', 'message: `WSL 发行版 "${WSL_DISTRO}" 已不再安装。`,'],
    ["detail: 'Antigravity opened on Windows instead.',", "detail: 'Antigravity 已改为在 Windows 本地打开。',"],
    ["await electron_1.dialog.showErrorBox('WSL setup failed', msg);", "await electron_1.dialog.showErrorBox('WSL 配置失败', msg);"],
    ["await electron_1.dialog.showErrorBox('Startup failed', msg);", "await electron_1.dialog.showErrorBox('启动失败', msg);"],
    ["await electron_1.dialog.showErrorBox('Binary not found', msg);", "await electron_1.dialog.showErrorBox('未找到核心二进制组件', msg);"],
    ["title: 'Confirm Quit',", "title: '确认退出',"],
    ["message: 'Are you sure you want to quit?',", "message: '您确定要退出吗？',"],
    ["detail: 'There may be agents or background tasks running.',", "detail: '可能还有智能体或后台任务正在运行。',"],
    ["buttons: ['Cancel', 'Quit'],", "buttons: ['取消', '退出'],"],
    ["label: 'New Window',", "label: '新建窗口',"],
  ]) add('dist/main.js', old, replacement);
  add('dist/updater.js', "title: 'Check for Updates',", "title: '检查更新',");
  add('dist/updater.js', "message: 'No updates available',", "message: '当前已是最新版本，暂无可用更新。',");
  add('dist/updater.js', "buttons: ['OK'],", "buttons: ['确定'],");
  add('dist/updater.js', 'item.label = step;', `item.label = (${JSON.stringify(Object.fromEntries(Object.entries(menuLabels).filter(([key]) => key.includes('Update'))))})[step] ?? step;`);
  for (const [old, replacement] of [
    ['<title>Welcome to Antigravity</title>', '<title>欢迎使用 Antigravity</title>'],
    ['Setting up…', '正在配置…'],
    ['<h1>Welcome to the new Antigravity!</h1>', '<h1>欢迎使用全新 Antigravity！</h1>'],
    ["<p>Antigravity has been redesigned to put agents first with new capabilities. If you'd still like a code editor, you can download it as a separate app named <b>Antigravity IDE</b>.</p>", '<p>Antigravity 经过全面重构，赋予智能体更强大的原生能力。如果您仍需要代码编辑器，可单独下载独立应用 <b>Antigravity IDE</b>。</p>'],
    ['<span>Download the Antigravity IDE</span>', '<span>下载 Antigravity IDE</span>'],
    ['<button class="btn-primary" id="btn-skip">Explore the new Antigravity</button>', '<button class="btn-primary" id="btn-skip">探索全新 Antigravity</button>'],
  ]) add('dist/ideInstall/wizardHtml.js', old, replacement);
  return operations;
}

const hash = bytes => createHash('sha256').update(bytes).digest('hex');

export function patchNativeFiles(files, payload) {
  if (!(files instanceof Map)) throw new TypeError('files must be a Map<string, Buffer>');
  if (typeof payload !== 'string' || !payload.trim()) throw new TypeError('translator payload must be a nonempty string');
  for (const entry of WHITELIST) {
    const bytes = files.get(entry);
    if (!Buffer.isBuffer(bytes)) throw new Error(`${entry} missing or is not a Buffer`);
    if (bytes.includes(MARKER) || bytes.includes('Antigravity 2.0 Chinese Localization Engine') || bytes.includes('const menuTranslationMap = {')) {
      throw new Error(`${entry} already localized; repeat injection refused`);
    }
  }
  const output = new Map(files);
  const changesByEntry = new Map();
  for (const operation of nativePatchOperations(payload)) {
    const { entry, old, replacement, expected } = operation;
    const source = output.get(entry).toString('utf8');
    const count = source.split(old).length - 1;
    if (count !== expected) throw new Error(`Anchor mismatch ${entry}: expected ${expected}, found ${count}: ${JSON.stringify(old)}`);
    if (!changesByEntry.has(entry)) changesByEntry.set(entry, { entry, beforeHash: hash(files.get(entry)), anchors: [] });
    changesByEntry.get(entry).anchors.push({ old, replacement, count, expected });
    output.set(entry, Buffer.from(source.split(old).join(replacement), 'utf8'));
  }
  const changes = [...changesByEntry.values()].map(change => ({ ...change, afterHash: hash(output.get(change.entry)) }));
  return { files: output, changes };
}
