// =========================================================================
// Antigravity Profile Hub Next - Rust GUI Frontend Logic
// =========================================================================

let isGlassActive = true;
let isDarkMode = true;
let currentView = 'grid';
let profilesData = [];

// --- IPC 通信封装 (与 Rust 后端通信) ---
function sendToRust(cmd, payload = {}) {
  const message = JSON.stringify({ cmd, payload });
  if (window.ipc && typeof window.ipc.postMessage === 'function') {
    window.ipc.postMessage(message);
  } else {
    // 浏览器预览模式下的模拟逻辑
    console.log('[Dev IPC Mock] ->', cmd, payload);
    simulateRustResponse(cmd, payload);
  }
}

// 接收 Rust 后端的主动调用或响应
window.onRustMessage = function (data) {
  try {
    const res = typeof data === 'string' ? JSON.parse(data) : data;
    handleRustEvent(res.event, res.data);
  } catch (e) {
    console.error('Failed to parse message from Rust:', e);
  }
};

function handleRustEvent(event, data) {
  switch (event) {
    case 'profiles_updated':
      renderProfiles(data);
      appendLog(`[STATUS] 已同步 ${data.length} 个 Profile 最新状态。`, 'text-blue-300');
      break;
    case 'command_output':
      appendLog(`[CLI] ${data.message || JSON.stringify(data)}`, data.level === 'error' ? 'text-rose-400' : 'text-slate-300');
      break;
    case 'glass_mode_changed':
      appendLog(`[DWM] 系统硬件级透光状态已变更: ${data.enabled ? 'Acrylic 亚克力已激活' : '已恢复实体扁平'}`, 'text-cyan-400');
      break;
    default:
      appendLog(`[EVENT] ${event}: ${JSON.stringify(data)}`, 'text-slate-400');
  }
}

// --- 初始化 ---
window.addEventListener('DOMContentLoaded', () => {
  // 请求初次 Profile 列表
  sendToRust('status_all');

  // 如果是在独立浏览器中测试，加载演示数据
  if (!window.ipc) {
    loadMockData();
  }
});

// --- 液态玻璃开关 ---
function toggleLiquidGlass() {
  isGlassActive = !isGlassActive;
  const body = document.getElementById('appBody');
  const indicator = document.getElementById('glassToggleIndicator');
  const toggleBtn = document.getElementById('toggleGlassBtn');
  const ambient = document.getElementById('ambientBackground');
  const blurWrapper = document.getElementById('blurControlWrapper');

  if (isGlassActive) {
    body.classList.remove('liquid-glass-inactive');
    body.classList.add('liquid-glass-active');
    indicator.classList.remove('translate-x-0');
    indicator.classList.add('translate-x-5');
    toggleBtn.classList.remove('bg-slate-700');
    toggleBtn.classList.add('bg-blue-600');
    ambient.style.opacity = '1';
    blurWrapper.style.opacity = '1';
    blurWrapper.style.pointerEvents = 'auto';
    appendLog('[UI] 已开启「液态玻璃 (Liquid Glass)」效果：毛玻璃背板滤镜、次表面漫反射与流光高光已激活。', 'text-cyan-400');
  } else {
    body.classList.remove('liquid-glass-active');
    body.classList.add('liquid-glass-inactive');
    indicator.classList.remove('translate-x-5');
    indicator.classList.add('translate-x-0');
    toggleBtn.classList.remove('bg-blue-600');
    toggleBtn.classList.add('bg-slate-700');
    ambient.style.opacity = '0';
    blurWrapper.style.opacity = '0.35';
    blurWrapper.style.pointerEvents = 'none';
    appendLog('[UI] 已关闭「液态玻璃」效果：平滑切换为现代无感极简扁平模式 (Solid Clean Mode)，更低 GPU 开销。', 'text-amber-300');
  }

  // 通知 Rust 调整宿主窗口硬件级 DWM 透光
  sendToRust('set_liquid_glass', { enabled: isGlassActive });
}

// 调节折射模糊强度
function updateBlur(val) {
  document.documentElement.style.setProperty('--glass-blur', val + 'px');
  document.getElementById('blurValue').innerText = val + 'px';
  sendToRust('set_blur_strength', { blur: parseInt(val, 10) });
}

// 视图切换 (网格 / 列表)
function setViewMode(mode) {
  currentView = mode;
  const grid = document.getElementById('profilesGridContainer');
  const list = document.getElementById('profilesListContainer');
  const gridBtn = document.getElementById('viewGridBtn');
  const listBtn = document.getElementById('viewListBtn');

  if (mode === 'grid') {
    grid.classList.remove('hidden');
    list.classList.add('hidden');
    gridBtn.classList.add('bg-blue-600/80', 'text-white');
    gridBtn.classList.remove('text-slate-400');
    listBtn.classList.remove('bg-blue-600/80', 'text-white');
    listBtn.classList.add('text-slate-400');
  } else {
    grid.classList.add('hidden');
    list.classList.remove('hidden');
    listBtn.classList.add('bg-blue-600/80', 'text-white');
    listBtn.classList.remove('text-slate-400');
    gridBtn.classList.remove('bg-blue-600/80', 'text-white');
    gridBtn.classList.add('text-slate-400');
  }
}

// 主题切换
function toggleTheme() {
  isDarkMode = !isDarkMode;
  const html = document.documentElement;
  const body = document.getElementById('appBody');
  if (isDarkMode) {
    html.classList.add('dark');
    body.classList.remove('light');
    body.style.backgroundColor = '#090D16';
    appendLog('[THEME] 切换为 Fluent 深色模式。', 'text-indigo-300');
  } else {
    html.classList.remove('dark');
    body.classList.add('light');
    body.style.backgroundColor = '#EEF2F6';
    appendLog('[THEME] 切换为 Fluent 浅色透光模式。', 'text-indigo-300');
  }
}

// --- 控制台日志系统 ---
function appendLog(msg, colorClass = 'text-slate-300') {
  const output = document.getElementById('consoleOutput');
  const item = document.createElement('div');
  item.className = colorClass + ' transition-opacity duration-300';
  item.textContent = msg;
  output.appendChild(item);
  output.scrollTop = output.scrollHeight;
}

function clearConsole() {
  const output = document.getElementById('consoleOutput');
  output.innerHTML = '<div class="text-slate-500 italic">[控制台已清空]</div>';
}

function copyConsoleLogs() {
  const output = document.getElementById('consoleOutput');
  navigator.clipboard.writeText(output.innerText).then(() => {
    appendLog('[SYSTEM] 控制台诊断文本已复制至剪贴板。', 'text-emerald-400');
  });
}

// --- Profile 渲染与交互 ---
function renderProfiles(profiles) {
  profilesData = profiles;
  const gridContainer = document.getElementById('profilesGridContainer');
  const tableBody = document.getElementById('profilesTableBody');
  const badge = document.getElementById('profileCountBadge');

  badge.textContent = `共 ${profiles.length} 个配置`;
  gridContainer.innerHTML = '';
  tableBody.innerHTML = '';

  profiles.forEach(p => {
    const isRunning = p.state === 'Running' || p.state === 'running';
    const isWaiting = p.state === 'WaitingLogin' || p.state === 'waiting_login';
    const stateColor = isRunning ? 'emerald' : isWaiting ? 'amber' : 'slate';
    const stateText = isRunning ? '正在运行' : isWaiting ? '等待授权' : '就绪 (Stopped)';

    // 1. 卡片元素
    const card = document.createElement('div');
    card.className = `glass-card rounded-2xl p-5 flex flex-col justify-between space-y-4 group ${isWaiting ? 'border-amber-500/30' : ''}`;
    card.setAttribute('data-name', p.name || '未命名');

    card.innerHTML = `
      <div>
        <div class="flex items-start justify-between">
          <div>
            <span class="inline-flex items-center space-x-1.5 px-2.5 py-1 rounded-full text-[11px] font-medium bg-${stateColor}-500/15 border border-${stateColor}-500/30 text-${stateColor}-400">
              <span class="w-1.5 h-1.5 rounded-full bg-${stateColor}-400 ${isRunning ? 'animate-pulse' : ''}"></span>
              <span>${stateText}</span>
            </span>
            <h3 class="text-base font-bold text-white mt-2 group-hover:text-blue-300 transition-colors">
              ${escapeHtml(p.name)}
            </h3>
          </div>
          <div class="p-2 rounded-xl bg-white/5 text-slate-400">
            <svg class="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24" stroke-width="1.8">
              <path stroke-linecap="round" stroke-linejoin="round" d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
            </svg>
          </div>
        </div>

        <div class="mt-3.5 space-y-2 text-xs">
          <div class="flex items-center justify-between text-slate-400 py-1 border-b border-white/5">
            <span>Workspace 路径</span>
            <span class="font-mono text-slate-300 truncate max-w-[170px]" title="${escapeHtml(p.workspacePath || '未指定')}">
              ${escapeHtml(p.workspacePath || '未指定')}
            </span>
          </div>
          <div class="flex items-center justify-between text-slate-400 py-1 border-b border-white/5">
            <span>诊断 / 状态</span>
            <span class="text-slate-300 truncate max-w-[170px]" title="${escapeHtml(p.detail || '')}">
              ${escapeHtml(p.detail || '正常')}
            </span>
          </div>
        </div>
      </div>

      <div class="pt-3 border-t border-white/10 flex items-center justify-between gap-2">
        <div class="flex items-center space-x-1.5">
          ${isRunning
            ? `<button onclick="actionStop('${p.profileId}')" class="glass-button-secondary text-rose-300 hover:text-rose-200 text-xs px-3 py-1.5 rounded-lg transition-all">关闭</button>`
            : `<button onclick="actionLaunch('${p.profileId}')" class="glass-button-primary text-white text-xs px-3.5 py-1.5 rounded-lg transition-all flex items-center space-x-1">
                 <svg class="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM9.555 7.168A1 1 0 008 8v4a1 1 0 001.555.832l3-2a1 1 0 000-1.664l-3-2z" clip-rule="evenodd"></path></svg>
                 <span>启动</span>
               </button>`
          }
          ${isWaiting
            ? `<button onclick="actionCancelLogin('${p.profileId}')" class="glass-button-secondary text-amber-300 text-xs px-2.5 py-1.5 rounded-lg transition-all">取消等待</button>`
            : `<button onclick="actionBeginLogin('${p.profileId}')" class="glass-button-secondary text-slate-300 text-xs px-2.5 py-1.5 rounded-lg transition-all">准备登录</button>`
          }
        </div>
        <div class="flex items-center space-x-1">
          <button onclick="actionShortcut('${p.profileId}')" class="text-xs text-slate-400 hover:text-white px-2 py-1 rounded" title="桌面快捷方式">快捷方式</button>
          <button onclick="actionRename('${p.profileId}')" class="text-xs text-blue-400 hover:text-blue-300 px-2 py-1 rounded">重命名</button>
        </div>
      </div>
    `;
    gridContainer.appendChild(card);

    // 2. 表格行
    const tr = document.createElement('tr');
    tr.className = 'hover:bg-white/5 transition-colors';
    tr.innerHTML = `
      <td class="py-3.5 px-4 font-semibold text-white flex items-center space-x-2">
        <span class="w-2 h-2 rounded-full bg-${stateColor}-400"></span>
        <span>${escapeHtml(p.name)}</span>
      </td>
      <td class="py-3.5 px-4">
        <span class="px-2 py-0.5 rounded-full text-[11px] bg-${stateColor}-500/20 text-${stateColor}-300">${stateText}</span>
      </td>
      <td class="py-3.5 px-4 font-mono text-slate-300">${escapeHtml(p.workspacePath || '未配置')}</td>
      <td class="py-3.5 px-4 text-slate-400 truncate max-w-[200px]">${escapeHtml(p.detail || '')}</td>
      <td class="py-3.5 px-4 text-right space-x-2">
        ${isRunning
          ? `<button onclick="actionStop('${p.profileId}')" class="text-rose-400 hover:text-rose-300">关闭</button>`
          : `<button onclick="actionLaunch('${p.profileId}')" class="text-blue-400 hover:text-blue-300 font-medium">启动</button>`
        }
        <button onclick="actionBeginLogin('${p.profileId}')" class="text-slate-300 hover:text-white">登录</button>
        <button onclick="actionRename('${p.profileId}')" class="text-slate-400 hover:text-white">命名</button>
      </td>
    `;
    tableBody.appendChild(tr);
  });
}

function escapeHtml(str) {
  if (!str) return '';
  return String(str).replace(/[&<>"']/g, m => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[m]);
}

// --- 搜索筛选 ---
function filterProfiles() {
  const query = document.getElementById('searchInput').value.toLowerCase().trim();
  const cards = document.querySelectorAll('#profilesGridContainer > div');
  let count = 0;
  cards.forEach(card => {
    const text = card.innerText.toLowerCase();
    if (text.includes(query)) {
      card.style.display = 'flex';
      count++;
    } else {
      card.style.display = 'none';
    }
  });
  document.getElementById('profileCountBadge').textContent = `显示 ${count} 个配置`;
}

// --- 业务操作指令 ---
function actionLaunch(id) {
  appendLog(`[ACTION] 正在启动实例 ${id}...`, 'text-blue-400');
  sendToRust('launch_profile', { id });
}
function actionStop(id) {
  appendLog(`[ACTION] 正在优雅关闭实例 ${id}...`, 'text-rose-400');
  sendToRust('stop_profile', { id, force: false });
}
function actionForceStop(id) {
  if (confirm('强制停止可能导致未保存数据丢失，是否确认强制停止？')) {
    appendLog(`[ACTION] 正在强制停止实例 ${id}...`, 'text-rose-500');
    sendToRust('stop_profile', { id, force: true });
  }
}
function actionBeginLogin(id) {
  appendLog(`[ACTION] 为实例 ${id} 申请 OAuth 授权等待槽位...`, 'text-amber-400');
  sendToRust('begin_login', { id });
}
function actionCancelLogin(id) {
  appendLog(`[ACTION] 取消实例 ${id} 授权等待槽位...`, 'text-slate-400');
  sendToRust('cancel_login', { id });
}
function actionRename(id) {
  const newName = prompt('请输入新的账号标签名称：');
  if (newName && newName.trim()) {
    sendToRust('rename_profile', { id, name: newName.trim() });
  }
}
function actionSetWorkspace(id) {
  const path = prompt('请输入工作区 (Workspace) 路径：');
  if (path !== null) {
    sendToRust('set_workspace', { id, path: path.trim() });
  }
}
function actionShortcut(id) {
  appendLog(`[ACTION] 正在生成桌面快捷方式...`, 'text-blue-300');
  sendToRust('shortcut', { id });
}

// --- 顶部工具栏操作 ---
function refreshAll() {
  appendLog('[ACTION] 正在刷新全部 Profile 状态...', 'text-blue-300');
  sendToRust('status_all');
}
function runProbe() {
  appendLog('[ACTION] 正在启动独立探针检测环境兼容性...', 'text-indigo-400');
  sendToRust('probe');
}
function runDoctor() {
  appendLog('[ACTION] 正在运行 Doctor 完整诊断...', 'text-emerald-400');
  sendToRust('doctor');
}
function openLogs() {
  sendToRust('open_logs');
}
function registerProtocol() {
  appendLog('[ACTION] 正在注册协议回调...', 'text-blue-300');
  sendToRust('register_protocol');
}

// --- 创建 Profile 弹窗 ---
function openCreateModal() {
  const modal = document.getElementById('createModal');
  const box = document.getElementById('createModalBox');
  modal.classList.remove('opacity-0', 'pointer-events-none');
  box.classList.remove('scale-95');
  box.classList.add('scale-100');
  document.getElementById('newProfileName').focus();
}

function closeCreateModal() {
  const modal = document.getElementById('createModal');
  const box = document.getElementById('createModalBox');
  modal.classList.add('opacity-0', 'pointer-events-none');
  box.classList.remove('scale-100');
  box.classList.add('scale-95');
}

function confirmCreateProfile() {
  const name = document.getElementById('newProfileName').value.trim();
  const ws = document.getElementById('newProfileWorkspace').value.trim();
  const bridge = document.getElementById('newProfileBridge').checked;

  if (!name) {
    alert('请输入账号标签名称！');
    return;
  }

  closeCreateModal();
  appendLog(`[ACTION] 正在创建 Profile [${name}]...`, 'text-emerald-400');
  sendToRust('create_profile', { name, workspace: ws || null, bridge: bridge ? 'on' : 'off' });
}

// --- 浏览器本地预览模拟数据 ---
function loadMockData() {
  renderProfiles([
    {
      profileId: "9e41-0001",
      name: "主工作区 (Primary-Dev)",
      state: "Running",
      workspacePath: "D:\\workspaces\\client-alpha",
      detail: "官方窗口已连接 (PID: 28412)"
    },
    {
      profileId: "9e41-0002",
      name: "团队备用 (Secondary-Audit)",
      state: "WaitingLogin",
      workspacePath: "D:\\audit\\repo-audit",
      detail: "已占用 OAuth 授权插槽 #1"
    },
    {
      profileId: "9e41-0003",
      name: "纯净沙箱 (Clean-Sandbox)",
      state: "Stopped",
      workspacePath: "",
      detail: "无桥接纯净隔离"
    }
  ]);
}

function simulateRustResponse(cmd, payload) {
  setTimeout(() => {
    switch (cmd) {
      case 'status_all':
        loadMockData();
        break;
      case 'create_profile':
        profilesData.push({
          profileId: "mock-" + Date.now(),
          name: payload.name,
          state: "Stopped",
          workspacePath: payload.workspace || "",
          detail: "新建实例就绪"
        });
        renderProfiles(profilesData);
        appendLog(`[SUCCESS] 实例 [${payload.name}] 已成功创建！`, 'text-emerald-400');
        break;
      case 'launch_profile':
        const target = profilesData.find(p => p.profileId === payload.id);
        if (target) target.state = 'Running';
        renderProfiles(profilesData);
        appendLog(`[SUCCESS] 实例已启动 (PID: ${Math.floor(Math.random() * 20000 + 10000)})`, 'text-emerald-400');
        break;
      case 'stop_profile':
        const pStop = profilesData.find(p => p.profileId === payload.id);
        if (pStop) pStop.state = 'Stopped';
        renderProfiles(profilesData);
        appendLog(`[SUCCESS] 实例已停止。`, 'text-slate-300');
        break;
      default:
        appendLog(`[SUCCESS] 指令 ${cmd} 执行完毕。`, 'text-emerald-400');
    }
  }, 400);
}
