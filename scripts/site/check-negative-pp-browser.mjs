// Isolated Edge/CDP smoke check. Admin writes are intercepted in this browser;
// public rankings/profile requests exercise the deployed site.
import { spawn } from 'node:child_process';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
const port = 9343;
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run', `--remote-debugging-port=${port}`,
    '--user-data-dir=E:/333/pythonEPTA/SERVAK/.test-tmp/negative-pp-browser-check', 'about:blank',
], { windowsHide: true, stdio: 'ignore' });
let ws;
let sequence = 0;
const pending = new Map();
const errors = [];
function rpc(method, params = {}) {
    return new Promise((resolve, reject) => {
        const id = ++sequence;
        const timer = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 15000);
        pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); }, reject });
        ws.send(JSON.stringify({ id, method, params }));
    });
}
async function evaluate(expression) {
    const result = await rpc('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result?.value;
}
async function until(expression) {
    for (let i = 0; i < 80; i++) {
        if (await evaluate(expression)) return;
        await delay(150);
    }
    const diagnostic = await evaluate('({url:location.href, toasts:document.querySelector("#toast-region")?.textContent, body:document.body.innerText.slice(-1200)})');
    console.error({errors, diagnostic});
    throw new Error(`UI timeout: ${expression}`);
}
try {
    let target;
    for (let i = 0; i < 60 && !target; i++) {
        try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page'); }
        catch { await delay(200); }
    }
    assert(target, 'Test browser did not start');
    ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
    ws.onmessage = event => {
        const data = JSON.parse(event.data);
        if (data.method === 'Runtime.exceptionThrown') errors.push(data.params.exceptionDetails.exception?.description || data.params.exceptionDetails.text);
        if (!data.id) return;
        const task = pending.get(data.id);
        if (!task) return;
        pending.delete(data.id);
        data.error ? task.reject(new Error(data.error.message)) : task.resolve(data.result);
    };
    await rpc('Runtime.enable');
    await rpc('Page.enable');

    await (async function verify() {
  if(process.argv.includes("--local-assets")){
    const {readFile}=await import("node:fs/promises");
    const assets=new Map();
    for(const [url,file,type] of [
      ["/site/app.js","static/site/app.js","text/javascript"],
      ["/site/styles.css","static/site/styles.css","text/css"],
      ["/admin/","static/admin/index.html","text/html"],
    ]) assets.set(url,{body:(await readFile(file)).toString("base64"),type});
    ws.addEventListener("message",event=>{
      const data=JSON.parse(event.data);
      if(data.method!=="Fetch.requestPaused")return;
      const asset=assets.get(new URL(data.params.request.url).pathname);
      const reply=asset?rpc("Fetch.fulfillRequest",{requestId:data.params.requestId,responseCode:200,responseHeaders:[{name:"Content-Type",value:asset.type}],body:asset.body}):rpc("Fetch.continueRequest",{requestId:data.params.requestId});
      reply.catch(error=>errors.push(error.message));
    });
    await rpc("Fetch.enable",{patterns:[{urlPattern:"*/site/app.js*"},{urlPattern:"*/site/styles.css*"},{urlPattern:"*/admin/"}]});
  }
  const board=await (await fetch("http://127.0.0.1:8000/api/private/web-site/rankings")).json();
  assert(board.items.length);
  const user=board.items[0].user, id=user.server_id||user.id;
  function fixtures() {
    const actualFetch=window.fetch;
    const rules=[];
    window.__penaltyTier=1;
    window.__penaltyWrites=[];
    window.fetch=async(url,options={})=>{
      const parsed=new URL(url,location.origin);
      if(parsed.pathname.includes("/admin-panel/")){
        const path=parsed.pathname.split("/admin-panel")[1];
        const method=options.method||"GET";
        let data;
        if(path==="/session") data={user:{id:42,username:"Browser administrator",roles:{owner:true,administrator:true,ranker:true}},csrf_token:"browser-fixture"};
        else if(path==="/negative-pp"&&method==="GET")data={items:rules};
        else if(path==="/negative-pp/preview")data={kind:"mapper",target_id:600,label:"Guest mapper fixture"};
        else if(path==="/negative-pp"&&method==="POST"){
          const body=JSON.parse(options.body);
          window.__penaltyWrites.push({path,body,method});
          const rule={...body,id:1,target_id:Number(body.reference),label:"Guest mapper fixture"};
          rules.push(rule);
          data={rule,beatmaps:3,user_modes:2};
        }else if(path==="/negative-pp/1"&&method==="DELETE"){
          window.__penaltyWrites.push({path,body:JSON.parse(options.body),method});rules.length=0;data={deleted:true,beatmaps:3,user_modes:2};
        }else if(method==="GET")data={items:[]};
        else throw new Error("Unexpected admin mutation: "+path);
        return new Response(JSON.stringify(data),{headers:{"Content-Type":"application/json"}});
      }
      const response=await actualFetch(url,options);
      if(parsed.pathname.includes("/web-site/")&&response.ok){
        const data=await response.json();
        const titles=[null,"Говноед","Сомелье дристни","Верховный копрогастроном"];
        function titleFor(user,tier=window.__penaltyTier){
          if(!user)return;
          user.negative_pp_badge=tier>0;
          user.negative_pp_score_count=[0,1,100,1000][tier];
          user.negative_pp_title=tier?{tier,name:titles[tier]}:null;
        }
        titleFor(data.user);
        data.leaders?.forEach((item,index)=>titleFor(item.user,index%3+1));
        if(parsed.pathname.endsWith("/rankings"))data.items?.forEach((item,index)=>titleFor(item.user,index%3+1));
        if(parsed.pathname.endsWith("/scores")&&data.items?.length){
          data.items[0].negative_pp=true;
          data.items[0].pp=-Math.abs(data.items[0].pp);
          data.items[0].weighted_pp=data.items[0].pp;
          data.items[0].weight=1;
        }
        return new Response(JSON.stringify(data),{headers:{"Content-Type":"application/json"}});
      }
      return response;
    };
  }
  await rpc("Page.addScriptToEvaluateOnNewDocument",{source:"("+fixtures.toString()+")()"});
  await rpc("Page.navigate",{url:"http://127.0.0.1:8000/site/#profile/"+id});
  await until('document.querySelector("#profile-name .negative-pp-badge")?.textContent==="Говноед"');
  await until('!!document.querySelector("#profile-scores .score-item.is-negative-pp")');
  assert.equal(await evaluate('document.querySelectorAll("#profile-name .negative-pp-badge").length'),1);
  assert(await evaluate('document.querySelector("#profile-scores .is-negative-pp .score-pp").textContent.startsWith("-")'));
  console.log("PASS: profile badge, negative score and weighted contribution tooltip.");
  const titles=[null,"Говноед","Сомелье дристни","Верховный копрогастроном"];
  const colours=[null,"rgb(88, 12, 40)","rgb(255, 32, 47)","rgb(8, 6, 8)"];
  for(const tier of [2,3,0,1]){
    await evaluate(`(()=>{window.__penaltyTier=${tier}; const format=document.querySelector('#profile-somsai-format'); format.value=format.value==='1v1'?'2v2':'1v1'; format.dispatchEvent(new Event('change'));})()`);
    await until(`(document.querySelector('#profile-name').dataset.negativePpTier||'0')==='${tier}'`);
    const rendered=await evaluate(`(()=>{const root=document.querySelector('#profile-name'),badge=root.querySelector('.negative-pp-badge');return {count:root.querySelectorAll('.negative-pp-badge').length,name:badge?.textContent||null,colour:getComputedStyle(root.querySelector('.user-name-text')).color,background:badge?getComputedStyle(badge).backgroundColor:null}})()`);
    assert.equal(rendered.count,tier?1:0);
    assert.equal(rendered.name,titles[tier]);
    if(tier){assert.equal(rendered.colour,colours[tier]);assert.equal(rendered.background,colours[tier]);}
  }
  await rpc("Emulation.setDeviceMetricsOverride",{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await evaluate('location.hash="home"');
  await until('document.querySelectorAll("#home-leaderboard .negative-pp-badge").length>=3');
  for(const tier of [1,2,3]){
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('#home-leaderboard [data-negative-pp-tier="${tier}"] .user-name-text')).color`),colours[tier]);
  }
  const {writeFile:saveImage}=await import("node:fs/promises");
  const homeImage=await rpc("Page.captureScreenshot",{format:"png",captureBeyondViewport:true});
  await saveImage("E:/333/pythonEPTA/SERVAK/.test-tmp/negative-pp-titles-home.png",Buffer.from(homeImage.data,"base64"));
  await evaluate('location.hash="rankings"');
  await until('document.querySelectorAll("#rankings-body .negative-pp-badge").length>=3');
  for(const tier of [1,2,3]){
    assert.equal(await evaluate(`getComputedStyle(document.querySelector('#rankings-body [data-negative-pp-tier="${tier}"] .user-name-text')).color`),colours[tier]);
  }
  assert(await evaluate(`Array.from(document.querySelectorAll('#rankings-body .player-cell')).every(player=>{
    const name=player.querySelector('.has-negative-pp-title'), country=player.querySelector('small');
    if(!name)return true;
    return country.getBoundingClientRect().top >= name.getBoundingClientRect().bottom - 1
      && name.getBoundingClientRect().right <= player.closest('td').getBoundingClientRect().right;
  })`),"Titles must remain inside the player column, with country on its own line");
  const rankingImage=await rpc("Page.captureScreenshot",{format:"png",captureBeyondViewport:true});
  await saveImage("E:/333/pythonEPTA/SERVAK/.test-tmp/negative-pp-titles-ranking.png",Buffer.from(rankingImage.data,"base64"));
  console.log("PASS: all three titles and nickname colours, promotion/removal without duplicates, home leaders and rankings.");
  await rpc("Emulation.setDeviceMetricsOverride",{width:390,height:844,deviceScaleFactor:1,mobile:false});
  await rpc("Page.navigate",{url:"http://127.0.0.1:8000/admin/#negative-pp"});
  await until('document.querySelector("#view-negative-pp")?.hidden===false');
  const navLayout = await evaluate('({height:document.querySelector(".nav").clientHeight,scrollHeight:document.querySelector(".nav").scrollHeight,rows:[...document.querySelectorAll(".nav-item:not([hidden])")].map(b=>Math.round(b.getBoundingClientRect().top))})');
  console.log({navLayout});
  assert.equal(new Set(navLayout.rows).size, 1, "Navigation must remain a single scrollable row on mobile");
  assert(navLayout.scrollHeight <= navLayout.height + 1, "Navigation labels must not be clipped by the scrollbar");
  await rpc("Emulation.setDeviceMetricsOverride",{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await evaluate('document.querySelector("#negative-pp-kind").value="mapper"; document.querySelector("#negative-pp-reference").value="Guest mapper fixture"; document.querySelector("#negative-pp-preview").click()');
  await until('document.querySelector("#negative-pp-add").disabled===false');
  await evaluate('document.querySelector("#negative-pp-reason").value="Browser test"; document.querySelector("#negative-pp-form").requestSubmit()');
  await until('!!document.querySelector("#negative-pp-list article")');
  assert.equal(await evaluate("window.__penaltyWrites[0].body.reference"),"600");
  const screenshot=await rpc("Page.captureScreenshot",{format:"png",captureBeyondViewport:true});
  const {writeFile}=await import("node:fs/promises");
  await writeFile("E:/333/pythonEPTA/SERVAK/.test-tmp/negative-pp-admin.png",Buffer.from(screenshot.data,"base64"));
  await evaluate('document.querySelector("#negative-pp-list input").value="Remove fixture"; document.querySelector("#negative-pp-list form").requestSubmit()');
  await until('window.__penaltyWrites.length===2 && !document.querySelector("#negative-pp-list article")');
  assert.equal(await evaluate("window.__penaltyWrites[1].method"),"DELETE");
  assert.deepEqual(errors,[]);
  console.log("PASS: admin preview, add/delete controls, resolved mapper ID and empty list; no real admin writes or browser errors.");
})();
} finally {
    if (ws?.readyState === WebSocket.OPEN) {
        try { await rpc('Browser.close'); } catch {}
        ws.close();
    }
    browser.kill();
}
