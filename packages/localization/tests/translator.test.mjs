import test from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { readFile } from 'node:fs/promises';

// A missing installer is the initial RED state, not an unrelated import error.
const module = await import('../src/translator.mjs').catch(error => {
  if (error.code !== 'ERR_MODULE_NOT_FOUND') throw error;
  return { installTranslator: () => null, createTranslatorPayload: () => '' };
});
const { installTranslator, createTranslatorPayload } = module;
const dictionary = { Settings: '设置', Help: '帮助', Cancel: '取消', Plan: '计划', New: '新建', Default: '默认', Copy: '复制', 'Search conversations': '搜索对话', 'Gemini 3': '不应翻译', Antigravity: '不应翻译' };
const make = (html, url = 'https://127.0.0.1:43123/') => new JSDOM(html, { url, runScripts: 'outside-only', pretendToBeVisual: true });
const settle = async () => { await new Promise(resolve => setTimeout(resolve, 70)); };
const start = async dom => { const controller = installTranslator(dom.window, dictionary); await settle(); return controller; };

test('translates semantic UI and preserves text spacing', async () => {
  const dom = make('<nav><button>  Settings  </button><label>Help</label></nav>');
  await start(dom);
  assert.equal(dom.window.document.querySelector('button').textContent, '  设置  ');
  assert.equal(dom.window.document.querySelector('label').textContent, '帮助');
  dom.window.close();
});

test('protects complete user, assistant, thought, artifact and code bodies including their buttons', async () => {
  const html = ['user-message', 'assistant-message', 'markdown', 'prose', 'thought-content', 'thinking-content', 'artifact-content', 'monaco-editor', 'diff-editor', 'terminal', 'group/user-input-step'].map(name => `<section class="${name}"><button title="Help">Help</button><p>Plan</p></section>`).join('') + '<pre>Help</pre><code>Help</code><div data-message-role="assistant"><button>Help</button></div>';
  const dom = make(html);
  await start(dom);
  assert.equal(dom.window.document.body.innerHTML, html);
  dom.window.close();
});

test('preserves editable contents while translating plain search placeholder', async () => {
  const dom = make('<input value="Plan" placeholder="Search conversations"><textarea>Settings</textarea><div contenteditable="">Help</div><div contenteditable="true">Help</div><div contenteditable="TRUE" role="button">Help</div><div contenteditable="plaintext-only"><button>Help</button></div><div role="textbox">Help</div>');
  await start(dom);
  assert.equal(dom.window.document.querySelector('input').value, 'Plan');
  assert.equal(dom.window.document.querySelector('input').placeholder, '搜索对话');
  for (const node of dom.window.document.querySelectorAll('[contenteditable],[role=textbox]')) assert.equal(node.textContent, 'Help');
  assert.equal(dom.window.document.querySelector('textarea').value, 'Settings');
  dom.window.close();
});

test('protects exact dictionary matches used as user-controlled names', async () => {
  const html = ['conversation-title', 'conversation-name', 'project-name', 'file-name', 'filename'].map(name => `<button class="${name}" title="Help">Plan</button>`).join('') + '<div role="treeitem">New</div><button data-conversation-id="123">Default</button><button data-project-id="123">Help</button>';
  const dom = make(html);
  await start(dom);
  assert.equal(dom.window.document.body.innerHTML, html);
  dom.window.close();
});

test('leaves ambiguous text and model or brand names unchanged', async () => {
  const dom = make('<div>Settings</div><button>Gemini 3</button><button>Antigravity</button>');
  await start(dom);
  assert.equal(dom.window.document.body.textContent, 'SettingsGemini 3Antigravity');
  dom.window.close();
});

test('translates accessibility UI attributes without changing DOM markup', async () => {
  const dom = make('<button aria-label="Settings" title="Help"><svg><path d="M1 1"></path></svg><span>Cancel</span></button>');
  await start(dom);
  const button = dom.window.document.querySelector('button');
  assert.equal(button.getAttribute('aria-label'), '设置');
  assert.equal(button.title, '帮助');
  assert.equal(button.querySelector('span').textContent, '取消');
  assert.equal(button.querySelector('path').getAttribute('d'), 'M1 1');
  dom.window.close();
});

test('allows explicit chat toolbar outside message body', async () => {
  const dom = make('<section class="chat"><div class="message-content">Copy</div><div role="toolbar"><button>Copy</button></div></section>');
  await start(dom);
  assert.equal(dom.window.document.querySelector('.message-content').textContent, 'Copy');
  assert.equal(dom.window.document.querySelector('button').textContent, '复制');
  dom.window.close();
});

test('updates inserted UI, replaced text and changed accessibility labels', async () => {
  const dom = make('<button>Settings</button>');
  await start(dom);
  const button = dom.window.document.querySelector('button');
  button.firstChild.data = 'Help';
  button.setAttribute('aria-label', 'Cancel');
  dom.window.document.body.insertAdjacentHTML('beforeend', '<button>Plan</button>');
  await settle();
  assert.equal(button.textContent, '帮助');
  assert.equal(button.getAttribute('aria-label'), '取消');
  assert.equal(dom.window.document.body.lastChild.textContent, '计划');
  dom.window.close();
});

test('restores own current translations and language but preserves newer application updates', async () => {
  const dom = make('<html lang="en"><body><button id="a" title="Help">Settings</button><button id="b">Cancel</button></body></html>');
  const controller = await start(dom);
  assert.ok(controller?.stop);
  const a = dom.window.document.getElementById('a');
  const b = dom.window.document.getElementById('b');
  b.firstChild.data = 'Application changed text';
  a.title = 'Application changed title';
  controller.stop();
  assert.equal(a.textContent, 'Settings');
  assert.equal(a.title, 'Application changed title');
  assert.equal(b.textContent, 'Application changed text');
  assert.equal(dom.window.document.documentElement.lang, 'en');
  dom.window.close();
});

test('stop cancels pending mutation work and permits a later installation', async () => {
  const dom = make('<button>Settings</button>');
  const controller = await start(dom);
  assert.ok(controller?.stop);
  dom.window.document.body.insertAdjacentHTML('beforeend', '<button>Help</button>');
  controller.stop();
  await settle();
  assert.equal(dom.window.document.body.lastChild.textContent, 'Help');
  await start(dom);
  assert.equal(dom.window.document.body.lastChild.textContent, '帮助');
  dom.window.close();
});

test('repeated installation shares one controller and one restoration history', async () => {
  const dom = make('<button>Settings</button>');
  const first = await start(dom);
  assert.ok(first?.stop);
  assert.equal(installTranslator(dom.window, dictionary), first);
  first.stop();
  assert.equal(dom.window.document.querySelector('button').textContent, 'Settings');
  dom.window.close();
});

test('handles UI removal and reinsertion without retaining stale translations', async () => {
  const dom = make('<button>Settings</button>');
  const controller = await start(dom);
  assert.ok(controller?.stop);
  const button = dom.window.document.querySelector('button');
  button.remove();
  await settle();
  button.textContent = 'Help';
  dom.window.document.body.append(button);
  await settle();
  assert.equal(button.textContent, '帮助');
  controller.stop();
  assert.equal(button.textContent, 'Help');
  dom.window.close();
});

test('refuses unrelated pages including non-root local paths', async () => {
  for (const url of ['https://example.com/', 'http://127.0.0.1:43123/', 'https://localhost:43123/', 'https://127.0.0.1:43123/plugin', 'file:///C:/unrelated.html']) {
    const dom = make('<button>Settings</button>', url);
    assert.equal(installTranslator(dom.window, dictionary), null);
    await settle();
    assert.equal(dom.window.document.querySelector('button').textContent, 'Settings');
    dom.window.close();
  }
});

test('does not install in frames even with the official origin', async () => {
  const dom = make('<iframe></iframe>');
  const frame = dom.window.document.querySelector('iframe').contentWindow;
  frame.document.body.innerHTML = '<button>Settings</button>';
  assert.equal(installTranslator(frame, dictionary), null);
  dom.window.close();
});

test('standalone payload executes without imports or Node globals', async () => {
  const dom = make('<button>Settings</button>');
  dom.window.eval(createTranslatorPayload(dictionary));
  await settle();
  assert.equal(dom.window.document.querySelector('button').textContent, '设置');
  assert.equal(dom.window.document.documentElement.lang, 'zh-CN');
  dom.window.close();
});

test('bounded mutation backlog still processes a large inserted UI batch', async t => {
  const dom = make('<nav></nav>');
  t.after(() => dom.window.close());
  const frames = [];
  dom.window.requestAnimationFrame = callback => { frames.push(callback); return frames.length; };
  installTranslator(dom.window, dictionary);
  frames.shift()();
  await Promise.resolve();
  assert.equal(frames.length, 0);
  dom.window.document.querySelector('nav').innerHTML = '<button>Help</button>'.repeat(2500);
  await Promise.resolve();
  assert.equal(frames.length, 1);
  frames.shift()();
  await Promise.resolve();
  assert.ok(dom.window.document.body.textContent.includes('Help'), 'a large batch must span multiple frames');
  for (let frame = 1; frames.length && frame < 100; frame++) {
    frames.shift()();
    await Promise.resolve();
  }
  assert.equal(frames.length, 0, 'the mutation backlog did not finish');
  assert.equal(dom.window.document.querySelectorAll('button').length, 2500);
  assert.equal([...dom.window.document.querySelectorAll('button')].every(button => button.textContent === '帮助'), true);
});

test('stop restores a removed node even before its removal callback runs', async () => {
  const dom = make('<button>Settings</button>');
  const controller = await start(dom);
  const button = dom.window.document.querySelector('button');
  button.remove();
  controller.stop();
  assert.equal(button.textContent, 'Settings');
  dom.window.close();
});

test('thought disclosure translates its trigger but leaves sibling body untouched', async () => {
  const dom = make('<section><button data-testid="thinking-collapsible-trigger">Plan</button><div><button>Help</button><p>Settings</p></div></section>');
  await start(dom);
  assert.equal(dom.window.document.querySelector('[data-testid]').textContent, '计划');
  assert.equal(dom.window.document.querySelector('div').textContent, 'HelpSettings');
  dom.window.close();
});

test('new protection boundaries undo earlier UI translation', async () => {
  const dom = make('<section><button>Settings</button></section>');
  await start(dom);
  dom.window.document.querySelector('section').className = 'assistant-message';
  await settle();
  assert.equal(dom.window.document.querySelector('button').textContent, 'Settings');
  dom.window.close();
});

test('preserves newer app language changes and absent original language', async () => {
  const dom = make('<button>Help</button>');
  let controller = await start(dom);
  controller.stop();
  assert.equal(dom.window.document.documentElement.hasAttribute('lang'), false);
  controller = await start(dom);
  dom.window.document.documentElement.lang = 'fr';
  controller.stop();
  assert.equal(dom.window.document.documentElement.lang, 'fr');
  dom.window.close();
});

test('preserves user content and names identified by test IDs', async () => {
  const html = ['assistant-message-content', 'conversation-title', 'project-name', 'artifact-content'].map(id => `<section data-testid="${id}"><button title="Help">Plan</button></section>`).join('');
  const dom = make(html);
  await start(dom);
  assert.equal(dom.window.document.body.innerHTML, html);
  dom.window.close();
});

test('translates branded UI help when the brand or model text is preserved', async () => {
  const dom = make('<div role="tooltip">Antigravity supports projects</div><button>Gemini models</button>');
  installTranslator(dom.window, { ...dictionary, 'Antigravity supports projects': 'Antigravity 支持项目', 'Gemini models': 'Gemini 模型' });
  await settle();
  assert.equal(dom.window.document.querySelector('[role=tooltip]').textContent, 'Antigravity 支持项目');
  assert.equal(dom.window.document.querySelector('button').textContent, 'Gemini 模型');
  dom.window.close();
});

test('introducing a thinking disclosure protects and restores its already translated sibling body', async () => {
  const dom = make('<section role="dialog"><div id="body"><button>Settings</button></div></section>');
  await start(dom);
  const section = dom.window.document.querySelector('section');
  const body = dom.window.document.getElementById('body');
  assert.equal(body.textContent, '设置');
  section.insertAdjacentHTML('afterbegin', '<button data-testid="thinking-collapsible-trigger">Plan</button>');
  await settle();
  assert.equal(section.firstElementChild.textContent, '计划');
  assert.equal(body.textContent, 'Settings');
  body.querySelector('button').firstChild.data = 'Help';
  await settle();
  assert.equal(body.textContent, 'Help');
  dom.window.close();
});

test('late code and editor protection attributes restore previously translated descendant UI', async () => {
  for (const attribute of ['data-code', 'data-language', 'data-line', 'data-line-number', 'data-lexical-editor']) {
    const dom = make('<section role="dialog"><button>Settings</button></section>');
    await start(dom);
    const section = dom.window.document.querySelector('section');
    assert.equal(section.textContent, '设置');
    section.setAttribute(attribute, 'true');
    await settle();
    assert.equal(section.textContent, 'Settings', attribute);
    section.querySelector('button').firstChild.data = 'Help';
    await settle();
    assert.equal(section.textContent, 'Help', attribute);
    dom.window.close();
  }
});

test('coalescing many new protection boundaries restores every earlier UI translation', async () => {
  const dom = make('<section><button>Settings</button></section>'.repeat(100));
  await start(dom);
  assert.equal([...dom.window.document.querySelectorAll('button')].every(button => button.textContent === '设置'), true);
  for (const section of dom.window.document.querySelectorAll('section')) section.className = 'assistant-message';
  const deadline = Date.now() + 5000;
  while (dom.window.document.body.textContent.includes('设置') && Date.now() < deadline) await settle();
  assert.equal([...dom.window.document.querySelectorAll('button')].every(button => button.textContent === 'Settings'), true);
  dom.window.close();
});

test('changing an existing control into a thinking trigger rechecks its sibling body', async () => {
  const dom = make('<section role="dialog"><button id="trigger">Plan</button><div id="body"><button>Settings</button></div></section>');
  await start(dom);
  const body = dom.window.document.getElementById('body');
  assert.equal(body.textContent, '设置');
  dom.window.document.getElementById('trigger').setAttribute('data-testid', 'thinking-collapsible-trigger');
  await settle();
  assert.equal(body.textContent, 'Settings');
  dom.window.close();
});

test('works on verified official history and UUID conversation routes', async () => {
  for (const path of ['/history', '/c/01a1056b-aeab-7430-b3ed-f7520989b8f7']) {
    const dom = make('<button>Settings</button>', `https://127.0.0.1:43123${path}`);
    await start(dom);
    assert.equal(dom.window.document.querySelector('button').textContent, '设置', path);
    dom.window.close();
  }
  for (const path of ['/c/not-a-uuid', '/c/01a1056b-aeab-7430-b3ed-f7520989b8f7/plugin', '/history/plugin']) {
    const dom = make('<button>Settings</button>', `https://127.0.0.1:43123${path}`);
    assert.equal(installTranslator(dom.window, dictionary), null, path);
    dom.window.close();
  }
});

test('preserves observed sidebar conversation and project names that collide with UI terms', async () => {
  const html = '<nav><a href="/c/01a1056b-aeab-7430-b3ed-f7520989b8f7" title="New">New</a><div data-testid="conversation-row-sidebar" data-cascade-id="uuid"><span>Plan</span></div><button data-project-card=""><span>Default</span></button></nav><button data-testid="project-selector-trigger" aria-label="Select project, current: New"><span>New</span></button><button aria-label="Select project, current: Plan">Plan</button>';
  const dom = make(html);
  await start(dom);
  assert.equal(dom.window.document.body.innerHTML, html);
  dom.window.close();
});

test('translates observed input overlay while preserving adjacent editable text', async () => {
  const dom = make('<section data-testid="agent-input-box"><div><p class="pointer-events-none select-none">Ask anything, @ to mention, / for actions</p><div contenteditable="true">Settings</div></div></section>');
  installTranslator(dom.window, { ...dictionary, 'Ask anything, @ to mention, / for actions': '输入任何内容，@ 用于提及，/ 用于执行操作' });
  await settle();
  assert.equal(dom.window.document.querySelector('p').textContent, '输入任何内容，@ 用于提及，/ 用于执行操作');
  assert.equal(dom.window.document.querySelector('[contenteditable]').textContent, 'Settings');
  dom.window.close();
});

test('observed account navigation never translates account names matching UI terms', async () => {
  const dom = make('<div role="dialog"><button data-testid="settings-nav-item-Account"><span>New</span><span>user@example.test</span></button><button>Settings</button></div>');
  await start(dom);
  assert.equal(dom.window.document.querySelector('[data-testid]').textContent, 'Newuser@example.test');
  assert.equal(dom.window.document.querySelector('button:last-child').textContent, '设置');
  dom.window.close();
});

test('translates this version announcement and split settings help from the shipped dictionary', async () => {
  const shippedDictionary = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<article data-testid="nux-card"><h3>Gemini 3.6 &amp; 3.7 Flash Deprecation</h3><p>Make sure you are using Gemini 3.8 Flash! We will be turning down Gemini 3.6 Flash and Gemini 3.7 Flash shortly.</p></article><div role="dialog"><h3>Global Permissions</h3><p><span>Modified in</span> <button><span>2</span> <span>projects</span></button></p><p>Modify permissions for file, terminal, and MCP tools.</p><div data-testid="plan-command-fyi-alert"><span>Type</span> <code>/</code> <span>and select</span> <code>plan</code></div></div>');
  installTranslator(dom.window, shippedDictionary);
  await settle();
  assert.equal(dom.window.document.querySelector('article h3').textContent, 'Gemini 3.6 & 3.7 Flash 停用通知');
  assert.equal(dom.window.document.querySelector('article p').textContent, '请确认您正在使用 Gemini 3.8 Flash！我们即将停止提供 Gemini 3.6 Flash 和 Gemini 3.7 Flash。');
  assert.equal(dom.window.document.querySelector('[role=dialog] h3').textContent, '全局权限');
  assert.equal(dom.window.document.querySelector('[role=dialog] p').textContent, '已在以下位置修改 2 项目');
  assert.equal(dom.window.document.querySelectorAll('[role=dialog] p')[1].textContent, '修改文件、终端和 MCP 工具的权限。');
  assert.equal(dom.window.document.querySelector('[data-testid=plan-command-fyi-alert]').textContent, '输入 / 并选择 plan');
  dom.window.close();
});

test('observed settings project group protects project names even when they equal fixed categories', async () => {
  const dom = make('<div role="dialog"><div class="flex flex-col gap-0.5"><button data-testid="settings-nav-item-General">General</button></div><div class="px-4 mt-2"><h2>Projects</h2></div><div class="flex flex-col gap-0.5"><button data-testid="settings-nav-item-General">General</button><button data-testid="settings-nav-item-Plan">Plan</button><button>Show All</button></div></div>');
  installTranslator(dom.window, { ...dictionary, General: '通用', Projects: '项目', 'Show All': '显示全部' });
  await settle();
  const buttons = dom.window.document.querySelectorAll('button');
  assert.equal(buttons[0].textContent, '通用');
  assert.equal(dom.window.document.querySelector('h2').textContent, '项目');
  assert.equal(buttons[1].textContent, 'General');
  assert.equal(buttons[2].textContent, 'Plan');
  assert.equal(buttons[3].textContent, '显示全部');
  buttons[2].firstChild.data = 'New';
  await settle();
  assert.equal(buttons[2].textContent, 'New');
  dom.window.close();
});

test('observed customization card preserves skill names and descriptions while its action translates', async () => {
  const dom = make('<div role="dialog"><div class="group flex items-start justify-between gap-3 px-3 py-2 border-border border-b"><div class="flex flex-col gap-0.5 min-w-0 flex-1 space-y-0"><div class="flex items-center gap-2"><span class="text-sm font-medium truncate" data-tooltip-id="skill">Plan</span><span>Global</span></div><div class="text-xs text-muted-foreground line-clamp-2 mt-1">Settings</div></div><button>Help</button></div></div>');
  await start(dom);
  const content = dom.window.document.querySelector('.space-y-0');
  assert.equal(content.textContent, 'PlanGlobalSettings');
  assert.equal(dom.window.document.querySelector('button').textContent, '帮助');
  dom.window.close();
});

test('observed conversation view protects actual response and file boundaries while its toolbar translates', async () => {
  const dom = make('<section data-testid="conversation-view" data-cascade-id="uuid"><div role="article" aria-label="Agent response" data-quotable=""><button title="Settings">Plan</button></div><div data-testid="cascade-system-message-toolbar"><button>Copy</button></div><div role="button" data-testid="changed-file-row" data-file-uri="file:///Plan"><span data-testid="file-title-name">Plan</span></div><span data-testid="breadcrumb-segment">New</span></section>');
  await start(dom);
  assert.equal(dom.window.document.querySelector('[role=article]').innerHTML, '<button title="Settings">Plan</button>');
  assert.equal(dom.window.document.querySelector('[data-testid=cascade-system-message-toolbar]').textContent, '复制');
  assert.equal(dom.window.document.querySelector('[data-file-uri]').textContent, 'Plan');
  assert.equal(dom.window.document.querySelector('[data-testid=breadcrumb-segment]').textContent, 'New');
  dom.window.close();
});

test('shipped settings dictionary translates new UI labels and changing weekly reset counts', async () => {
  const shippedDictionary = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<div role="dialog"><label>Contrast</label><button>Strong</button><h3>Windows Subsystem for Linux</h3><p>Run the app against a Linux environment. Connecting relaunches the app into the selected WSL distro.</p><button>Connect</button><label>App version</label><p id="reset">You have used some of your weekly limit, it will fully refresh in 5 days, 4 hours.</p></div>');
  installTranslator(dom.window, shippedDictionary);
  await settle();
  assert.equal(dom.window.document.querySelector('label').textContent, '对比度');
  assert.equal(dom.window.document.querySelector('button').textContent, '强');
  assert.equal(dom.window.document.querySelector('h3').textContent, 'Windows Linux 子系统');
  const reset = dom.window.document.getElementById('reset');
  assert.equal(reset.textContent, '您已使用部分每周额度，将在 5 天 4 小时后完全恢复。');
  reset.firstChild.data = 'You have used some of your weekly limit, it will fully refresh in 1 day, 2 hours.';
  await settle();
  assert.equal(reset.textContent, '您已使用部分每周额度，将在 1 天 2 小时后完全恢复。');
  dom.window.close();
});

test('shipped dictionary localizes verified environment, version and MCP controls and queue descriptions', async () => {
  const shippedDictionary = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<button aria-label="Select Environment">Local</button><button aria-label="Refresh MCP servers">Refresh MCP servers</button><button aria-label="Queue until after the current turn.">Queue</button><button aria-label="Interrupt the agent and send immediately.">Send Immediately</button><button>Version 2.19.1</button>');
  installTranslator(dom.window, shippedDictionary);
  await settle();
  const buttons = dom.window.document.querySelectorAll('button');
  assert.equal(buttons[0].textContent, '本地');
  assert.equal(buttons[0].getAttribute('aria-label'), '选择环境');
  assert.equal(buttons[1].textContent, '刷新 MCP 服务器');
  assert.equal(buttons[1].getAttribute('aria-label'), '刷新 MCP 服务器');
  assert.equal(buttons[2].getAttribute('aria-label'), '等待当前轮次结束后发送。');
  assert.equal(buttons[3].getAttribute('aria-label'), '中断智能体并立即发送。');
  assert.equal(buttons[4].textContent, '版本 2.19.1');
  dom.window.close();
});

test('observed project settings title and folder names remain unchanged beside translated UI', async () => {
  const shippedDictionary = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<div role="dialog"><h2><div class="flex items-center gap-2 h-7"><span class="truncate inline-block text-lg font-medium">Plan</span><button aria-label="Edit project name"></button></div></h2><p>Manage project folders, agent settings, and permissions.</p><div class="flex items-center justify-between gap-1.5 pl-2 pr-1.5 h-9 text-sm select-none w-full"><div class="flex items-center gap-1.5"><div class="flex items-center gap-1.5 bg-transparent"><span data-tooltip-id="folder" title="Default">New</span></div></div></div><button>Add Folder</button><div class="py-2 px-3 w-full"><div><span class="text-xs text-muted-foreground whitespace-pre-wrap"><span>Permanently delete <strong>Plan</strong> including <strong>3 active conversations</strong>.</span></span></div><div class="shrink-0"><button data-testid="delete-project-button">Delete</button></div></div></div>');
  installTranslator(dom.window, shippedDictionary);
  await settle();
  const title = dom.window.document.querySelector('h2 span');
  assert.equal(title.textContent, 'Plan');
  assert.equal(dom.window.document.querySelector('h2 button').getAttribute('aria-label'), '编辑项目名称');
  assert.equal(dom.window.document.querySelector('[data-tooltip-id]').innerHTML, 'New');
  assert.equal(dom.window.document.querySelector('[data-tooltip-id]').title, 'Default');
  assert.equal(dom.window.document.querySelector('strong').textContent, 'Plan');
  assert.equal(dom.window.document.querySelectorAll('strong')[1].textContent, '3 个活动对话');
  assert.equal(dom.window.document.querySelector('[data-testid=delete-project-button]').textContent, '删除');
  assert.equal(dom.window.document.querySelector('[role=dialog] > button').textContent, '添加文件夹');
  assert.equal(dom.window.document.querySelector('p').textContent, '管理项目文件夹、智能体设置和专属权限。');
  dom.window.document.querySelector('h2 button').setAttribute('title', 'Help');
  title.firstChild.data = 'New';
  await settle();
  assert.equal(title.textContent, 'New');
  assert.equal(dom.window.document.querySelector('h2 button').title, '帮助');
  dom.window.close();
});

test('verified onboarding route translates its narrow welcome headings and protects entry fields', async () => {
  const shippedDictionary = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const html = '<div class="entrance-title">Welcome to <span>Antigravity</span></div><div class="entrance-auth-panel"><div><div class="text-sm font-semibold text-foreground text-center">Sign in</div><input value="New"><button>Continue with Google</button><button>Use business account</button></div></div>';
  const dom = make(html, 'https://127.0.0.1:43123/onboarding');
  installTranslator(dom.window, shippedDictionary);
  await settle();
  assert.equal(dom.window.document.querySelector('.entrance-title').textContent, '欢迎使用 Antigravity');
  assert.equal(dom.window.document.querySelector('.text-center').textContent, '登录');
  assert.equal(dom.window.document.querySelector('input').value, 'New');
  assert.equal(dom.window.document.querySelectorAll('button')[0].textContent, '使用 Google 账号继续');
  assert.equal(dom.window.document.querySelectorAll('button')[1].textContent, '使用企业账号');
  dom.window.close();
  const unrelated = make(html);
  installTranslator(unrelated.window, shippedDictionary);
  await settle();
  assert.equal(unrelated.window.document.querySelector('.entrance-title').textContent, 'Welcome to Antigravity');
  assert.equal(unrelated.window.document.querySelector('.text-center').textContent, 'Sign in');
  unrelated.window.close();
});

test('a late project edit label restores an already translated sibling title', async () => {
  const dom = make('<div role="dialog"><h2><div><span class="truncate inline-block text-lg font-medium">Plan</span><button aria-label="Help"></button></div></h2></div>');
  installTranslator(dom.window, { ...dictionary, 'Edit project name': '编辑项目名称' });
  await settle();
  assert.equal(dom.window.document.querySelector('span').textContent, '计划');
  dom.window.document.querySelector('button').setAttribute('aria-label', 'Edit project name');
  await settle();
  assert.equal(dom.window.document.querySelector('span').textContent, 'Plan');
  assert.equal(dom.window.document.querySelector('button').getAttribute('aria-label'), '编辑项目名称');
  dom.window.close();
});

test('official conversation row actions translate only their accessibility labels', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<nav><div data-testid="conversation-row-sidebar"><span title="Plan">Plan</span><button aria-label="More options" title="Plan"></button><button aria-label="Pin conversation"></button><button role="button" aria-label="Archive conversation"></button></div></nav>');
  const originalButton = dom.window.document.querySelector('button');
  let clicks = 0;
  originalButton.addEventListener('click', () => clicks++);
  installTranslator(dom.window, shipped);
  await settle();
  assert.deepEqual([...dom.window.document.querySelectorAll('button')].map(button => button.getAttribute('aria-label')), ['更多选项', '置顶对话', '归档对话']);
  assert.equal(dom.window.document.querySelector('span').textContent, 'Plan');
  assert.equal(dom.window.document.querySelector('span').title, 'Plan');
  assert.equal(dom.window.document.querySelector('button').title, 'Plan');
  assert.equal(dom.window.document.querySelector('button'), originalButton);
  dom.window.document.querySelector('button').click();
  assert.equal(clicks, 1);
  dom.window.close();
});

test('official composer accessibility label translates without changing editable content', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<div data-testid="agent-input-box"><div contenteditable="true" role="textbox" aria-label="Message input" title="Plan"><span>Settings</span><code>Plan</code></div></div>');
  const controller = installTranslator(dom.window, shipped);
  await settle();
  const input = dom.window.document.querySelector('[contenteditable]');
  assert.equal(input.getAttribute('aria-label'), '消息输入框');
  assert.equal(input.innerHTML, '<span>Settings</span><code>Plan</code>');
  assert.equal(input.title, 'Plan');
  assert.equal(input.getAttribute('contenteditable'), 'true');
  input.querySelector('span').firstChild.data = 'New';
  await settle();
  assert.equal(input.innerHTML, '<span>New</span><code>Plan</code>');
  assert.equal(installTranslator(dom.window, shipped), controller);
  controller.stop();
  assert.equal(input.getAttribute('aria-label'), 'Message input');
  assert.equal(input.innerHTML, '<span>New</span><code>Plan</code>');
  dom.window.close();
});

test('official selector accessibility prefixes preserve every suffix byte', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<button data-testid="project-selector-trigger" aria-label="Select project, current:  Plan &amp; New  ">Plan</button><button role="combobox" data-testid="model-selector-trigger" aria-label="Select model, current: Gemini 3.8 Flash High">Gemini 3.8 Flash High</button>');
  installTranslator(dom.window, shipped);
  await settle();
  const [project, model] = dom.window.document.querySelectorAll('button');
  assert.equal(project.getAttribute('aria-label'), '选择项目，当前：  Plan & New  ');
  assert.equal(project.textContent, 'Plan');
  assert.equal(model.getAttribute('aria-label'), '选择模型，当前： Gemini 3.8 Flash High');
  assert.equal(model.textContent, 'Gemini 3.8 Flash High');
  project.setAttribute('aria-label', 'Select project, current: Default');
  model.setAttribute('aria-label', 'Select model, current: Claude Opus 4.1 & New  ');
  await settle();
  assert.equal(project.getAttribute('aria-label'), '选择项目，当前： Default');
  assert.equal(model.getAttribute('aria-label'), '选择模型，当前： Claude Opus 4.1 & New  ');
  dom.window.close();
});

test('body, thought, artifact and code vetoes override forged official accessibility contexts', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const html = ['assistant-message', 'thought-content', 'artifact-content', 'monaco-editor'].map(name => `<section class="${name}"><div data-testid="conversation-row-sidebar"><button aria-label="More options">Plan</button></div><button data-testid="project-selector-trigger" aria-label="Select project, current: Plan">Plan</button><div data-testid="agent-input-box"><div role="textbox" contenteditable="true" aria-label="Message input">Settings</div></div></section>`).join('') + '<pre><div data-testid="conversation-row-sidebar"><button aria-label="Pin conversation">Plan</button></div></pre>';
  const dom = make(html);
  installTranslator(dom.window, shipped);
  await settle();
  assert.equal(dom.window.document.body.innerHTML, html);
  dom.window.close();
});

test('scoped aria translations restore after entering body protection and stop preserves app updates', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<section><div data-testid="conversation-row-sidebar"><button aria-label="More options"></button></div><button data-testid="project-selector-trigger" aria-label="Select project, current: Plan">Plan</button></section>');
  const controller = installTranslator(dom.window, shipped);
  await settle();
  const row = dom.window.document.querySelector('[data-testid=conversation-row-sidebar]');
  const button = row.querySelector('button');
  assert.equal(button.getAttribute('aria-label'), '更多选项');
  row.className = 'assistant-message';
  await settle();
  assert.equal(button.getAttribute('aria-label'), 'More options');
  row.className = '';
  await settle();
  assert.equal(button.getAttribute('aria-label'), '更多选项');
  button.setAttribute('aria-label', 'App changed label');
  controller.stop();
  assert.equal(button.getAttribute('aria-label'), 'App changed label');
  assert.equal(dom.window.document.querySelector('[data-testid=project-selector-trigger]').getAttribute('aria-label'), 'Select project, current: Plan');
  dom.window.close();
});

test('scoped accessibility labels stop when their official selector marker disappears', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const dom = make('<button data-testid="project-selector-trigger" aria-label="Select project, current: Plan">Plan</button>');
  installTranslator(dom.window, shipped);
  await settle();
  const button = dom.window.document.querySelector('button');
  assert.equal(button.getAttribute('aria-label'), '选择项目，当前： Plan');
  button.removeAttribute('data-testid');
  await settle();
  assert.equal(button.getAttribute('aria-label'), 'Select project, current: Plan');
  assert.equal(button.textContent, 'Plan');
  dom.window.close();
});

test('conversation title buttons matching an action label remain fully protected', async () => {
  const shipped = JSON.parse(await readFile(new URL('../dictionaries/zh-CN.json', import.meta.url), 'utf8'));
  const html = '<div data-testid="conversation-row-sidebar"><button aria-label="More options">More options</button><button aria-label="Pin conversation">Pin conversation</button></div>';
  const dom = make(html);
  installTranslator(dom.window, shipped);
  await settle();
  assert.equal(dom.window.document.body.innerHTML, html);
  dom.window.close();
});

test('customizations UI translates while plugin names, skill text and theme code stay unchanged', async () => {
  const dom=make('<nav><button>Settings</button></nav><div data-testid="customization-card" data-card-name="Plan"><div class="flex-1 min-w-0"><h3>Plan</h3><p>Help</p></div><button>Copy</button></div><div class="group relative flex items-center rounded-lg"><button><div class="flex items-baseline min-w-0"><span>Plan</span><span>Help</span></div></button></div><div role="dialog"><div class="font-mono" style="white-space: pre"><span>name</span></div></div><div data-testid="plugin-suggested-prompt-row"><button>Plan</button></div><textarea>Settings</textarea>', 'https://127.0.0.1:43123/customizations?settingsOpen=true');
  const controller=installTranslator(dom.window,{...dictionary,name:'名称'});
  await settle();
  assert.ok(controller);
  assert.equal(dom.window.document.querySelector('nav button').textContent,'设置');
  assert.equal(dom.window.document.querySelector('[data-card-name] h3').textContent,'Plan');
  assert.equal(dom.window.document.querySelector('[data-card-name] p').textContent,'Help');
  assert.equal(dom.window.document.querySelector('[data-card-name] button').textContent,'复制');
  assert.equal(dom.window.document.querySelector('.items-baseline').textContent,'PlanHelp');
  assert.equal(dom.window.document.querySelector('.font-mono').textContent,'name');
  assert.equal(dom.window.document.querySelector('[data-testid="plugin-suggested-prompt-row"]').textContent,'Plan');
  assert.equal(dom.window.document.querySelector('textarea').value,'Settings');
  controller.stop();
  assert.equal(dom.window.document.querySelector('nav button').textContent,'Settings');
  dom.window.close();
});
