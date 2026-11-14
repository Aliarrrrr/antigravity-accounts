import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import {JSDOM} from 'jsdom';
import {installTranslator} from '../src/translator.mjs';

const dictionaryPath=new URL('../dictionaries/zh-CN.json',import.meta.url);
const dictionarySource=await fs.readFile(dictionaryPath,'utf8');
const dictionary=JSON.parse(dictionarySource);
const settle=()=>new Promise(resolve=>setTimeout(resolve,100));
const make=(html,route='/')=>new JSDOM(html,{url:'https://127.0.0.1:43123'+route,pretendToBeVisual:true});

test('shipped dictionary has one value per source phrase',()=>{
  const keys=[...dictionarySource.matchAll(/^\s*"((?:[^"\\]|\\.)*)":/gm)].map(match=>JSON.parse('"'+match[1]+'"'));
  assert.equal(new Set(keys).size,keys.length,'duplicate source phrases hide earlier translations');
});

test('updated browser, permission, planning, login and automation labels translate',async()=>{
  const phrases=['Open Link in New Tab','Open in Built-in Browser','Sandbox Allow Network','Browser Actuation Permissions','Proceed with Plan','Use Business Account','Continue with Google Cloud','Having trouble signing in?','New Agent Script','Prompt to execute on schedule...'];
  const dom=make(phrases.map(text=>'<button>'+text+'</button>').join(''));
  try {
    installTranslator(dom.window,dictionary);
    await settle();
    assert.deepEqual([...dom.window.document.querySelectorAll('button')].map(node=>node.textContent),phrases.map(text=>dictionary[text]));
  } finally {dom.window.close();}
});

test('new translations preserve matching conversation, code, edit values and skill names',async()=>{
  const html='<section data-message-role="assistant"><button>Open Terminal</button></section><code>Open Terminal</code><textarea>Open Terminal</textarea><input value="Open Terminal" placeholder="Prompt to execute on schedule..."><div data-testid="customization-card" data-card-name="Review"><div class="flex-1 min-w-0 flex flex-col gap-0.5 pr-4"><div class="flex items-center gap-1.5 min-w-0"><div class="font-medium text-sm text-foreground truncate"><button>Review</button></div></div><div class="text-xs text-muted-foreground truncate">Open Terminal</div></div><div class="relative z-10"><button>Delete</button></div></div><div class="group relative flex items-center rounded-lg"><button class="w-full min-w-0 px-3 py-1.5 text-left rounded-lg"><div class="flex items-baseline gap-2.5 min-w-0"><span class="text-sm font-medium flex-shrink-0">Open Terminal</span><span class="text-sm text-muted-foreground truncate" title="Review">Review</span></div></button></div><nav><button>Open Terminal</button></nav>';
  const dom=make(html);
  try {
    installTranslator(dom.window,dictionary);
    await settle();
    const document=dom.window.document;
    assert.equal(document.querySelector('[data-message-role] button').textContent,'Open Terminal');
    assert.equal(document.querySelector('code').textContent,'Open Terminal');
    assert.equal(document.querySelector('textarea').value,'Open Terminal');
    assert.equal(document.querySelector('input').value,'Open Terminal');
    assert.equal(document.querySelector('input').placeholder,dictionary['Prompt to execute on schedule...']);
    assert.equal(document.querySelector('[data-card-name] button').textContent,'Review');
    assert.equal(document.querySelector('[data-card-name] .text-xs').textContent,'Open Terminal');
    assert.equal(document.querySelector('[data-card-name] .relative button').textContent,dictionary.Delete);
    assert.equal(document.querySelector('div.group > button span').textContent,'Open Terminal');
    assert.equal(document.querySelector('div.group > button span[title]').title,'Review');
    assert.equal(document.querySelector('nav button').textContent,dictionary['Open Terminal']);
  } finally {dom.window.close();}
});

test('customizations route translates toolbar and settings without changing prompt content',async()=>{
  const dom=make('<h1>Customizations</h1><h2>Skills &amp; Rules</h2><nav><button>Project Settings</button><button>Close Settings</button><button>Installed</button></nav><input placeholder="Search customizations..."><div role="dialog"><label>Command Setup Script</label><input value="echo Open Terminal"><div data-testid="plugin-suggested-prompt-row" role="button">Open Terminal</div></div>','/customizations?settingsOpen=true');
  try {
    assert.ok(installTranslator(dom.window,dictionary),'official customizations route was rejected');
    await settle();
    const document=dom.window.document;
    assert.equal(document.querySelector('nav button').textContent,dictionary['Project Settings']);
    assert.equal(document.querySelector('label').textContent,dictionary['Command Setup Script']);
    assert.equal(document.querySelector('h1').textContent,'自定义配置');
    assert.equal(document.querySelector('h2').textContent,'技能与规则');
    assert.equal(document.querySelector('input').placeholder,'搜索自定义配置…');
    assert.equal(document.querySelector('input[value]').value,'echo Open Terminal');
    assert.equal(document.querySelector('[data-testid="plugin-suggested-prompt-row"]').textContent,'Open Terminal');
  } finally {dom.window.close();}
});

test('2.31 home and sidebar labels translate while retaining the name and count',async()=>{
  const dom=make('<div class="agy-home"><div data-testid="home-greeting"><h1 class="home-greeting-title">  Hey A$&amp;B, what should we build today?  </h1></div><section aria-label="Announcements and tips"><p>Start Voice mode</p></section><button data-testid="voice-start-button" aria-label="Start Voice mode"></button></div><div data-testid="conversation-list-sidebar"><div class="group/header"><button aria-label="Project options"><svg></svg></button></div><button id="all">See all (007)</button></div>');
  try {
    const document=dom.window.document,original=document.body.innerHTML;
    const controller=installTranslator(dom.window,dictionary);
    await settle();
    assert.equal(document.querySelector('h1').textContent,'  你好，A$&B，今天想做点什么？  ');
    assert.equal(document.querySelector('[data-testid="voice-start-button"]').getAttribute('aria-label'),'开始语音模式');
    assert.equal(document.querySelector('.group\\/header button').getAttribute('aria-label'),'项目选项');
    assert.equal(document.querySelector('#all').textContent,'查看全部（007）');
    assert.equal(document.querySelector('section').getAttribute('aria-label'),'公告与提示');
    assert.equal(document.querySelector('section p').textContent,'Start Voice mode');
    controller.stop();
    assert.equal(document.body.innerHTML,original);
  } finally {dom.window.close();}
});

test('2.31 phrases in bodies, names, editors and unrelated headings stay unchanged',async()=>{
  const dom=make('<h1>Hey A$&amp;B, what should we build today?</h1><div data-message-role="assistant"><div data-testid="home-greeting"><h1 class="home-greeting-title">Hey A$&amp;B, what should we build today?</h1></div><button aria-label="Project options">See all (7)</button><div class="agy-home"><section aria-label="Announcements and tips"></section></div></div><code>Start Voice mode</code><textarea>See all (7)</textarea><input value="Project options"><button data-project-id="named">See all (7)</button><div contenteditable="true"><button aria-label="Start Voice mode">Project options</button></div>');
  try {
    const original=dom.window.document.body.innerHTML;
    installTranslator(dom.window,dictionary);
    await settle();
    assert.equal(dom.window.document.body.innerHTML,original);
  } finally {dom.window.close();}
  const history=make('<div data-testid="home-greeting"><h1 class="home-greeting-title">Hey A$&amp;B, what should we build today?</h1></div>','/history');
  try {
    installTranslator(history.window,dictionary);
    await settle();
    assert.equal(history.window.document.querySelector('h1').textContent,'Hey A$&B, what should we build today?');
  } finally {history.window.close();}
});

test('2.31 dynamic labels update and scoped home text restores when its markers disappear',async()=>{
  const dom=make('<div class="agy-home"><div data-testid="home-greeting"><h1 class="home-greeting-title">Hey First, what should we build today?</h1></div><section aria-label="Announcements and tips"></section></div><button>See all (7)</button>');
  try {
    const document=dom.window.document,heading=document.querySelector('h1'),button=document.querySelector('button');
    installTranslator(dom.window,dictionary);
    await settle();
    heading.textContent='Hey Next$&Name, what should we build today?';
    button.textContent='See all (013)';
    await settle();
    assert.equal(heading.textContent,'你好，Next$&Name，今天想做点什么？');
    assert.equal(button.textContent,'查看全部（013）');
    heading.parentElement.removeAttribute('data-testid');
    document.querySelector('.agy-home').classList.remove('agy-home');
    await settle();
    assert.equal(heading.textContent,'Hey Next$&Name, what should we build today?');
    assert.equal(document.querySelector('section').getAttribute('aria-label'),'Announcements and tips');
  } finally {dom.window.close();}
});

test('all three 2.31 rotating greetings translate with and without a display name',async()=>{
  const greetings=[
    ['Hey A$&B, what should we build today?','你好，A$&B，今天想做点什么？'],
    ['Welcome back, A$&B. Where should we pick up?','欢迎回来，A$&B。想从哪里继续？'],
    ["Good to see you, A$&B. What's next?",'很高兴见到你，A$&B。接下来做什么？'],
    ['Hey there, what should we build today?','你好，今天想做点什么？'],
    ['Welcome back. Where should we pick up?','欢迎回来。想从哪里继续？'],
    ["Good to see you. What's next?",'很高兴见到你。接下来做什么？'],
  ];
  const dom=make('<div data-testid="home-greeting"><h1 class="home-greeting-title"></h1></div>');
  try {
    const heading=dom.window.document.querySelector('h1');
    installTranslator(dom.window,dictionary);
    for(const [original,translated] of greetings) {
      heading.textContent='  '+original+'  ';
      await settle();
      assert.equal(heading.textContent,'  '+translated+'  ');
    }
  } finally {dom.window.close();}
});
