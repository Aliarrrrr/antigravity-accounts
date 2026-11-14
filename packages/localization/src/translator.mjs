/** Installs UI-only localization. This function is serialized into the preload payload. */
export function installTranslator(window, dictionary) {
  const document = window.document;
  const location = window.location;
  const officialRoute = /^(?:\/|\/history|\/onboarding|\/customizations|\/c\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
  if (window.top !== window || location.protocol !== 'https:' || location.hostname !== '127.0.0.1' || !location.port || !officialRoute.test(location.pathname)) return null;
  const key = '__antigravityZhCN';
  if (window[key]) return window[key];

  // Ambiguous nodes are excluded: dictionary membership alone is not evidence of UI.
  const bodySelector = [
    'script', 'style', 'noscript', 'iframe', 'object', 'embed', 'pre', 'code',
    '[role="code"]', '[data-message-role]', '[data-message-id]', '[data-filename]', '[data-file-path]',
    '[data-language]', '[data-code]', '[data-line]', '[data-line-number]',
    '.markdown', '.prose', '.user-message', '.assistant-message', '.message-content', '.message-body',
    '.thought-content', '.thinking-content', '.thought-box', '.artifact-content', '.artifact-body',
    '.monaco-editor', '.monaco-diff-editor', '.diff-editor', '.editor-instance', '.terminal', '.xterm',
    '.font-mono[style*="white-space: pre"]', '.font-mono[style*="white-space:pre"]', '[data-testid="plugin-suggested-prompt-row"]',
    '[data-testid="mcp-server-note"]',
    '.cursor-edit', '[class~="group/user-input-step"]', '[data-user-content]', '[data-quotable]',
    '[role="article"][aria-label="Agent response"]', '[data-file-uri]',
    'div.group.items-start.justify-between.gap-3.px-3.py-2 > div.space-y-0.min-w-0.flex-1'
  ].join(',');
  const editableSelector = 'textarea,[contenteditable=""],[contenteditable="true" i],[contenteditable="plaintext-only" i],[role="textbox"],[data-lexical-editor],.cursor-text,.input-area,.chat-input';
  const protectedSelector = [
    bodySelector, editableSelector, '[role="treeitem"]', '[data-conversation-id]', '[data-project-id]',
    '.conversation-title', '.conversation-name', '.project-name', '.file-name', '.filename',
    'a[href^="/c/"]', '[data-project-card]', '[data-testid="conversation-row-sidebar"]',
    '[data-testid="customization-card"][data-card-name] > div.flex-1.min-w-0',
    'div.group.relative.flex.items-center.rounded-lg > button > div.flex.items-baseline.min-w-0',
    '[data-testid="doc-row-modal"] h1', '[data-testid="doc-row-modal"] h2', '[data-testid="doc-row-modal"] p',
    '[data-testid="project-selector-trigger"]', '[data-testid="model-selector-trigger"]', '[aria-label^="Select project, current:"]', '[data-testid="settings-nav-item-Account"]',
    '[data-testid="file-title-name"]', '[data-testid="breadcrumb-segment"]',
    'div.h-9.text-sm.select-none.w-full > div.flex.items-center > div.bg-transparent > span[data-tooltip-id]'
  ].join(',');
  const uiSelector = 'button,[role="button"],[role="menuitem"],[role="menuitemcheckbox"],[role="menuitemradio"],[role="tab"],label,nav,[role="navigation"],[role="menu"],[role="dialog"],[role="tooltip"],[role="status"],[role="alert"],[role="toolbar"],select,option,[data-localize-ui],[data-testid="agent-input-box"] p.pointer-events-none.select-none,[data-testid="agent-input-box"] span.pointer-events-none.select-none,[data-testid="nux-card"]';
  const histories = new WeakMap();
  const translatedSubtrees = new WeakSet();
  const queue = new Set();
  let traversal = null;
  let frame = null;
  let stopped = false;
  let thinkingTriggers = new WeakMap();
  let protectionCache = new WeakMap();
  const originalLang = document.documentElement.getAttribute('lang');
  document.documentElement.setAttribute('lang', 'zh-CN');

  function hardBodyElement(element) {
    if (element.closest(bodySelector)) return true;
    for (let current = element; current; current = current.parentElement) {
      const markers = [...current.classList, current.getAttribute('data-testid') || ''];
      if (markers.some(token => /(?:^|[-_/])(?:message-body|message-content|assistant-message|user-message|artifact-content|thought-content|thinking-content|code-line|view-line|hljs)(?:$|[-_/])/.test(token))) return true;
      const parent = current.parentElement;
      if (!parent) continue;
      if (!thinkingTriggers.has(parent)) thinkingTriggers.set(parent, parent.querySelector(':scope > button[data-testid="thinking-collapsible-trigger"]'));
      const trigger = thinkingTriggers.get(parent);
      if (trigger && current !== trigger && !trigger.contains(current)) return true;
    }
    return false;
  }

  function scopedAria(element) {
    if (hardBodyElement(element)) return null;
    const current = element.getAttribute('aria-label');
    const history = histories.get(element)?.get('aria-label');
    const original = history && current === history.translated ? history.original : current;
    if (typeof original !== 'string') return null;
    if (location.pathname === '/' && element.matches('.agy-home section') && original === 'Announcements and tips') {
      return typeof dictionary[original] === 'string' ? { original, translated: dictionary[original] } : null;
    }
    const ownComposer = original === 'Message input' && element.closest('[data-testid="agent-input-box"]') &&
      element.matches('input,textarea,[role="textbox"],[contenteditable],[data-lexical-editor]') &&
      !element.parentElement?.closest('input,textarea,[contenteditable],[role="textbox"],[data-lexical-editor]');
    if (ownComposer) return typeof dictionary['Message input'] === 'string' ? { original, translated: dictionary['Message input'] } : null;
    if (element.closest(editableSelector) || !element.matches('button,[role="button"],[role="combobox"]')) return null;
    if (element.closest('[data-testid="conversation-row-sidebar"]') && !element.textContent.trim() && ['More options', 'Pin conversation', 'Archive conversation'].includes(original)) {
      return typeof dictionary[original] === 'string' ? { original, translated: dictionary[original] } : null;
    }
    const marker = element.getAttribute('data-testid');
    const prefix = marker === 'project-selector-trigger' ? 'Select project, current:' : marker === 'model-selector-trigger' ? 'Select model, current:' : null;
    if (prefix && original.startsWith(prefix) && typeof dictionary[prefix] === 'string') return { original, translated: dictionary[prefix] + original.slice(prefix.length) };
    return null;
  }

  function scopedText(node, element) {
    if (location.pathname !== '/' || !element.matches('[data-testid="home-greeting"] > h1.home-greeting-title')) return null;
    const history = histories.get(node)?.get('');
    const original = history && node.data === history.translated ? history.original : node.data;
    const trimmed = original.trim();
    const greetingTemplates = [
      [/^Hey (.+), what should we build today\?$/, 'Hey {name}, what should we build today?'],
      [/^Welcome back, (.+)\. Where should we pick up\?$/, 'Welcome back, {name}. Where should we pick up?'],
      [/^Good to see you, (.+)\. What's next\?$/, "Good to see you, {name}. What's next?"],
    ];
    let translated;
    for (const [pattern, key] of greetingTemplates) {
      const greeting = pattern.exec(trimmed);
      const template = dictionary[key];
      if (greeting && typeof template === 'string') {
        translated = template.replace('{name}', () => greeting[1]);
        break;
      }
    }
    if (['Hey there, what should we build today?', 'Welcome back. Where should we pick up?', "Good to see you. What's next?"].includes(trimmed)) translated = dictionary[trimmed];
    if (typeof translated !== 'string') return null;
    const start = original.indexOf(trimmed);
    return { original, translated: original.slice(0, start) + translated + original.slice(start + trimmed.length) };
  }

  function ariaTraversal(element) {
    if (hardBodyElement(element)) return false;
    return !!scopedAria(element) || (!element.closest(editableSelector) && !!element.closest('[data-testid="conversation-row-sidebar"]'));
  }

  function computeProtection(element) {
    if (element.closest(protectedSelector)) return true;
    if(location.pathname==='/customizations' && element.closest('h1,h2,[data-slot="dialog-title"]')) {
      const documentDialog=element.closest('[role="dialog"]');
      if(documentDialog?.querySelector('[data-testid="doc-row-modal"]')) return true;
    }
    const projectTitle = element.closest('h2 span.truncate.inline-block.text-lg.font-medium');
    if (projectTitle && [...projectTitle.closest('h2').querySelectorAll('button')].some(button => ['Edit project name', dictionary['Edit project name']].includes(button.getAttribute('aria-label')))) return true;
    const projectName = element.closest('strong');
    const deletionRow = projectName?.closest('div.py-2.px-3');
    if (deletionRow?.querySelector('[data-testid="delete-project-button"]') && projectName.parentElement.querySelector(':scope > strong') === projectName) return true;
    const settingsButton = element.closest('button[data-testid^="settings-nav-item-"]');
    if (settingsButton) {
      const header = settingsButton.parentElement?.previousElementSibling;
      const heading = header?.matches('div.px-4.mt-2') ? header.querySelector(':scope > h2') : null;
      if (heading && ['Projects', dictionary.Projects].includes(heading.textContent.trim())) return true;
    }
    // Thinking disclosure body is a sibling of its known UI trigger.
    for (let current = element; current?.parentElement; current = current.parentElement) {
      const parent = current.parentElement;
      if (!thinkingTriggers.has(parent)) thinkingTriggers.set(parent, parent.querySelector(':scope > button[data-testid="thinking-collapsible-trigger"]'));
      const trigger = thinkingTriggers.get(parent);
      if (trigger && current !== trigger && !trigger.contains(current)) return true;
    }
    // Name/code classes use tokens, avoiding accidental matches against ordinary UI text.
    for (let current = element; current; current = current.parentElement) {
      const markers = [...current.classList, current.getAttribute('data-testid') || ''];
      if (markers.some(token => /(?:^|[-_/])(?:conversation-title|conversation-name|project-name|file-name|filename|message-body|message-content|assistant-message|user-message|artifact-content|thought-content|thinking-content|code-line|view-line|hljs)(?:$|[-_/])/.test(token))) return true;
    }
    return false;
  }

  function protectedElement(element) {
    if(!protectionCache.has(element)) protectionCache.set(element,computeProtection(element));
    return protectionCache.get(element);
  }

  function translate(value) {
    const trimmed = value.trim();
    let result = Object.prototype.hasOwnProperty.call(dictionary, trimmed) ? dictionary[trimmed] : null;
    const weeklyReset = /^You have used some of your weekly limit, it will fully refresh in (\d+) days?, (\d+) hours?\.$/.exec(trimmed);
    const weeklyTemplate = dictionary['You have used some of your weekly limit, it will fully refresh in {days} days, {hours} hours.'];
    if (weeklyReset && typeof weeklyTemplate === 'string') result = weeklyTemplate.replace('{days}', weeklyReset[1]).replace('{hours}', weeklyReset[2]);
    const activeCount = /^(\d+) active conversations?$/.exec(trimmed);
    if (activeCount && typeof dictionary['{count} active conversations'] === 'string') result = dictionary['{count} active conversations'].replace('{count}', activeCount[1]);
    const seeAllCount = /^See all \((\d+)\)$/.exec(trimmed);
    if (seeAllCount && typeof dictionary['See all ({count})'] === 'string') result = dictionary['See all ({count})'].replace('{count}', seeAllCount[1]);
    const names = trimmed.match(/Antigravity|Gemini(?:\s+\d+(?:\.\d+)*(?:\s+(?:Flash|Pro|Ultra|Lite))*)?|Claude(?:\s+(?:Sonnet|Opus|Haiku)(?:\s+\d+(?:\.\d+)*)?)?|GPT(?:[-\s]+\d+(?:\.\d+)*(?:[-\s]+(?:mini|nano))?)?/gi) || [];
    if (names.some(name => name === trimmed || typeof result !== 'string' || !result.includes(name))) return value;
    return typeof result === 'string' && result !== trimmed ? value.slice(0, value.indexOf(trimmed)) + result + value.slice(value.indexOf(trimmed) + trimmed.length) : value;
  }

  function write(node, attribute, value, override) {
    const current = attribute ? node.getAttribute(attribute) : node.data;
    let history = histories.get(node);
    const previous = history?.get(attribute);
    if (previous && current === previous.translated) return;
    // An application update supersedes our restoration record, including unknown text.
    if (previous) history.delete(attribute);
    const translated = typeof override === 'string' ? override : translate(value);
    if (translated === value) return;
    if (!history) { history = new Map(); histories.set(node, history); }
    history.set(attribute, { original: value, translated, scoped: typeof override === 'string' });
    // Weak ancestry marks let newly protected branches restore prior output in
    // the normal frame budget while untouched content remains pruned.
    for (let ancestor = node.nodeType === 1 ? node : node.parentElement; ancestor; ancestor = ancestor.parentElement) translatedSubtrees.add(ancestor);
    if (attribute) node.setAttribute(attribute, translated);
    else node.data = translated;
  }

  function restore(node, keepAttribute = null) {
    const history = histories.get(node);
    if (!history) return;
    for (const [attribute, entry] of history) {
      if (attribute === keepAttribute) continue;
      const current = attribute ? node.getAttribute(attribute) : node.data;
      if (current === entry.translated) {
        if (attribute) node.setAttribute(attribute, entry.original);
        else node.data = entry.original;
      }
      history.delete(attribute);
    }
    if (!history.size) histories.delete(node);
  }

  function restoreTree(root) {
    restore(root);
    const walker = document.createTreeWalker(root, window.NodeFilter.SHOW_ELEMENT | window.NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) restore(walker.currentNode);
  }

  function process(node) {
    const element = node.nodeType === 1 ? node : node.parentElement;
    if (!element) return;
    const aria = node.nodeType === 1 ? scopedAria(element) : null;
    const text = node.nodeType === 3 ? scopedText(node, element) : null;
    if (histories.get(node)?.get('aria-label')?.scoped && !aria) restore(node);
    if (histories.get(node)?.get('')?.scoped && !text) restore(node);
    if (protectedElement(element)) {
      restore(node, aria ? 'aria-label' : null);
      if (aria) write(element, 'aria-label', aria.original, aria.translated);
      return;
    }
    const isUI = element.closest(uiSelector) || (location.pathname === '/customizations' && element.closest('h1,h2,h3')) || (location.pathname === '/onboarding' && element.closest('.entrance-title,.entrance-auth-panel > div > div.text-sm.font-semibold.text-center'));
    if (node.nodeType === 3) {
      if (!element.closest('input') && (isUI || text)) write(node, '', text ? text.original : node.data, text?.translated);
    } else if (node.nodeType === 1 && (element.matches('input') || isUI || aria)) {
      for (const attribute of ['aria-label', 'title', 'placeholder']) {
        if (attribute === 'aria-label' && aria) { write(element, attribute, aria.original, aria.translated); continue; }
        if (element.hasAttribute(attribute)) write(element, attribute, element.getAttribute(attribute));
      }
    }
  }

  function makeTraversal(root) {
    const walker = document.createTreeWalker(root, window.NodeFilter.SHOW_ELEMENT | window.NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        if (node.nodeType === 1 && protectedElement(node) && !translatedSubtrees.has(node) && !ariaTraversal(node)) return window.NodeFilter.FILTER_REJECT;
        return window.NodeFilter.FILTER_ACCEPT;
      }
    });
    // Inputs permit UI attributes, but never their values or editable descendants.
    let first = true;
    return { root, next() { if (first) { first = false; return root; } return walker.nextNode(); } };
  }

  function schedule() {
    if (!stopped && frame === null) frame = window.requestAnimationFrame(flush);
  }

  function enqueue(node) {
    if (stopped || !node.isConnected) return;
    if (queue.size >= 64) { queue.clear(); queue.add(document.documentElement); traversal = null; }
    else if (!queue.has(document.documentElement)) queue.add(node);
    schedule();
  }

  function flush() {
    frame = null;
    if (stopped) return;
    thinkingTriggers = new WeakMap();
    protectionCache = new WeakMap();
    let budget = 400;
    while (budget-- > 0) {
      if (!traversal) {
        const root = queue.values().next().value;
        if (!root) break;
        queue.delete(root);
        if (!root.isConnected) continue;
        const element = root.nodeType === 1 ? root : root.parentElement;
        if (element && protectedElement(element) && !translatedSubtrees.has(element) && !ariaTraversal(element)) continue;
        traversal = makeTraversal(root);
      }
      if (!traversal.root.isConnected) { traversal = null; continue; }
      const node = traversal.next();
      if (!node) { traversal = null; continue; }
      process(node);
    }
    if (queue.size || traversal) schedule();
  }

  const observer = new window.MutationObserver(records => {
    for (const record of records) {
      if (record.type === 'childList') {
        for (const node of record.removedNodes) {
          restoreTree(node);
          queue.delete(node);
          if (traversal?.root === node) traversal = null;
        }
        // Disclosure controls can introduce a protection boundary for existing
        // siblings. Recheck the changed parent, not only its newly added nodes.
        enqueue(record.target);
      } else {
        const attribute = record.type === 'attributes' ? record.attributeName : '';
        const history = histories.get(record.target)?.get(attribute);
        const current = attribute ? record.target.getAttribute(attribute) : record.target.data;
        if (!history || current !== history.translated) enqueue(record.target);
        if ((attribute === 'data-testid' || attribute === 'aria-label') && record.target.parentElement) enqueue(record.target.parentElement);
      }
    }
  });
  observer.observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['aria-label', 'title', 'placeholder', 'class', 'style', 'contenteditable', 'role', 'data-testid', 'data-card-name', 'data-message-role', 'data-message-id', 'data-conversation-id', 'data-project-id', 'data-filename', 'data-file-path', 'data-user-content', 'data-code', 'data-language', 'data-line', 'data-line-number', 'data-lexical-editor', 'data-localize-ui', 'href', 'data-project-card', 'data-quotable', 'data-file-uri'] });
  const controller = {
    stop() {
      if (stopped) return;
      stopped = true;
      for (const record of observer.takeRecords()) {
        if (record.type === 'childList') for (const node of record.removedNodes) restoreTree(node);
      }
      observer.disconnect();
      if (frame !== null) window.cancelAnimationFrame(frame);
      frame = null;
      queue.clear();
      traversal = null;
      restoreTree(document.documentElement);
      if (document.documentElement.getAttribute('lang') === 'zh-CN') {
        if (originalLang === null) document.documentElement.removeAttribute('lang');
        else document.documentElement.setAttribute('lang', originalLang);
      }
      if (window[key] === controller) delete window[key];
    }
  };
  window[key] = controller;
  enqueue(document.documentElement);
  return controller;
}

/** Standalone renderer script: never executes upstream installer code. */
export function createTranslatorPayload(dictionary) {
  const literal = JSON.stringify(dictionary).replace(/</g, '\\u003c').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
  return `(() => { (${installTranslator.toString()})(window, ${literal}); })();`;
}
