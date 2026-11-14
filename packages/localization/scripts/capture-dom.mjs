// Read-only diagnostics for the application's own existing DevTools endpoint.
import fs from 'node:fs/promises';
const [portFile,output]=process.argv.slice(2);
if(!portFile||!output) throw new Error('Usage: node scripts/capture-dom.mjs DevToolsActivePort output.json');
const port=Number((await fs.readFile(portFile,'utf8')).split(/\r?\n/)[0]);
const pages=await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const page=pages.find(p=>p.type==='page'&&/^https:\/\/127\.0\.0\.1:\d+\//.test(p.url));
if(!page) throw new Error('official_renderer_not_found');
const socket=new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve,reject)=>{socket.addEventListener('open',resolve,{once:true});socket.addEventListener('error',reject,{once:true});});
const expression=`(()=>{
  const describe=e=>({tag:e.tagName,classes:e.className?.toString(),role:e.getAttribute('role'),testid:e.getAttribute('data-testid'),aria:e.getAttribute('aria-label'),href:e.tagName==='A'?e.getAttribute('href'):undefined,attributes:[...e.attributes].map(a=>a.name)});
  const walker=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT),texts=[];
  while(walker.nextNode()){
    const node=walker.currentNode,e=node.parentElement,text=node.data.trim();
    if(!text||!e||e.closest('script,style,textarea,input,[contenteditable]'))continue;
    const parents=[];for(let p=e;p&&parents.length<8;p=p.parentElement)parents.push(describe(p));
    texts.push({text:text.slice(0,350),visible:!!e.getClientRects().length,parents});
  }
  const projectButton=document.querySelector('[data-testid="settings-nav-item-mutiagy"]');
  const settingsProjectStructure=projectButton?[projectButton.parentElement,projectButton.parentElement.parentElement].map(p=>({parent:describe(p),children:[...p.children].map(e=>({element:describe(e),text:e.matches('[data-testid="settings-nav-item-Account"]')?'[account omitted]':e.textContent.trim().slice(0,120)}))})):null;
  const testDraft='New / Plan / Default\\nconst New = "Plan"; // Default';
  const testDraftPreserved=[...document.querySelectorAll('[contenteditable="true"]')].some(e=>e.innerText===testDraft||e.textContent===testDraft);
  const controls=[...document.querySelectorAll('button,[role="combobox"],[contenteditable="true"]')]
    .filter(e=>!e.closest('[data-testid="settings-nav-item-Account"],[data-quotable],[role="article"],.markdown,.prose,pre,code'))
    .map(e=>({element:describe(e),title:e.getAttribute('title'),visible:!!e.getClientRects().length,
      parents:[e.parentElement,e.parentElement?.parentElement].filter(Boolean).map(describe)}));
  return {url:location.href,localized:!!window.__antigravityZhCN,lang:document.documentElement.lang,texts,controls,settingsProjectStructure,testDraftPreserved};
})()`;
const result=await new Promise((resolve,reject)=>{socket.addEventListener('message',event=>{const message=JSON.parse(event.data);if(message.id===1){if(message.error||message.result?.exceptionDetails)reject(new Error(JSON.stringify(message.error||message.result.exceptionDetails)));else resolve(message.result.result.value);}});socket.send(JSON.stringify({id:1,method:'Runtime.evaluate',params:{expression,returnByValue:true}}));});
socket.close();
await fs.writeFile(output,JSON.stringify(result,null,2)+'\n');
console.log(JSON.stringify({output,url:result.url,localized:result.localized,texts:result.texts.length}));
