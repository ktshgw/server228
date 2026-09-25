// Isolated Edge/CDP smoke check. Admin writes are intercepted in this browser;
// public rankings/profile requests exercise the deployed site.
import { spawn } from 'node:child_process';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
const port = 9344;
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run', `--remote-debugging-port=${port}`,
    '--user-data-dir=E:/333/pythonEPTA/SERVAK/.test-tmp/beatmap-page-browser-check', 'about:blank',
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
    const result = await rpc('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true });
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


    const { readFile, writeFile } = await import('node:fs/promises');
    if (!process.argv.includes('--deployed')) {
        const assets = new Map();
        for (const [url, path, type] of [
            ['/site/', 'static/site/index.html', 'text/html'],
            ['/site/app.js', 'static/site/app.js', 'text/javascript'],
            ['/site/styles.css', 'static/site/styles.css', 'text/css'],
        ]) assets.set(url, {body:(await readFile(path)).toString('base64'), type});
        ws.addEventListener('message', event => {
            const data=JSON.parse(event.data);
            if (data.method !== 'Fetch.requestPaused') return;
            const asset=assets.get(new URL(data.params.request.url).pathname);
            (asset ? rpc('Fetch.fulfillRequest', {requestId:data.params.requestId,responseCode:200,responseHeaders:[{name:'Content-Type',value:asset.type}],body:asset.body})
                : rpc('Fetch.continueRequest',{requestId:data.params.requestId})).catch(error=>errors.push(error.message));
        });
        await rpc('Fetch.enable',{patterns:[{urlPattern:'*/site/'},{urlPattern:'*/site/app.js*'},{urlPattern:'*/site/styles.css*'}]});
    }
    if (process.argv.includes('--live')) {
        const home=await (await fetch('http://127.0.0.1:8000/api/private/web-site/home')).json();
        const recent=home.recent_scores[0], setId=recent.beatmapset.id, beatmapId=recent.beatmap.id;
        const board=await (await fetch('http://127.0.0.1:8000/api/private/web-site/beatmaps/'+beatmapId+'/scores')).json();
        assert(Array.isArray(board.items));
        for(const filter of ['scope=country','scope=friends','mods=DT','mods=NM']){
            assert.equal((await fetch('http://127.0.0.1:8000/api/private/web-site/beatmaps/'+beatmapId+'/scores?'+filter)).status,401);
        }
        await rpc('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
        await rpc('Page.navigate',{url:'http://127.0.0.1:8000/site/#beatmap/'+setId+'/'+beatmapId});
        await until('document.querySelector("#beatmap-detail-content").hidden===false && document.querySelectorAll("#beatmap-detail-facts meter").length===5');
        await until('!document.querySelector("#beatmap-comment-list").textContent.includes("Загружаем")');
        if(board.items.length)await until('document.querySelectorAll("#beatmap-leaderboard-body tr").length>0');
        assert.equal(await evaluate('document.querySelector("#beatmap-detail-mirror").href'),'https://beatconnect.io/b/'+setId);
        assert.equal(await evaluate('document.querySelectorAll("#beatmap-detail-modes button").length'),4);
        assert.equal(await evaluate('document.querySelector("#beatmap-preview-audio").volume'),0.1);
        assert(await evaluate('document.querySelector("#beatmap-detail-creator").href === document.querySelector("#beatmap-mapper-link").href'));
        for (const mode of ['osu', 'taiko', 'fruits', 'mania']) {
            const response = await fetch('http://127.0.0.1:8000/api/private/web-site/beatmaps/'+beatmapId+'/scores?mode='+mode);
            assert.equal(response.status,200);
            const modeBoard = await response.json();
            assert.equal(modeBoard.mode,mode);
            assert(modeBoard.items.every(item => item.score.ruleset === mode));
            const enabled = await evaluate('!document.querySelector("#beatmap-detail-modes [data-mode='+mode+']").disabled');
            if (!enabled) continue;
            await evaluate('document.querySelector("#beatmap-detail-modes [data-mode='+mode+']").click()');
            await until('document.querySelector("#beatmap-detail-modes [data-mode='+mode+']").getAttribute("aria-pressed")==="true" && !document.querySelector("#beatmap-leaderboard-empty strong").textContent.includes("Загружаем")');
            assert(await evaluate('!document.querySelector("#beatmap-leaderboard-empty strong").textContent.includes("недоступна")'));
        }
        await evaluate('document.querySelector("#beatmap-preview-toggle").click()');
        await until('document.querySelector("#beatmap-preview-audio").currentTime>0');
        await evaluate('location.hash="home"');
        await until('document.querySelector("#beatmap-preview-audio").paused');
        await evaluate('location.hash="beatmap/'+setId+'/'+beatmapId+'"');
        await until('!document.querySelector("#beatmap-detail-content").hidden && document.querySelector("#beatmap-detail-loading").hidden && document.querySelectorAll("#beatmap-leaderboard-body tr").length>0');
        await until('!document.querySelector(".beatmap-detail-cover-image") || document.querySelector(".beatmap-detail-cover-image").complete');
        await writeFile('.test-tmp/beatmap-page-live.png',Buffer.from((await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true})).data,'base64'));
        assert.deepEqual(errors,[]);
        console.log('PASS: live catalogue metadata, local leaderboard, comments, audio playback/pause on navigation, Beatconnect and HTTP supporter gates. Map set '+setId);
    } else {
    function fixtures() {
        const actualFetch=window.fetch;
        const supporter=localStorage.getItem('beatmap-fixture-supporter') === 'true';
        const player={id:1500000000,server_id:1,username:'ADmNH_PAZyMA',country_code:'DE',avatar_url:'/site/soms-default-avatar.png',roles:supporter?['supporter']:[]};
        const rival={...player,id:1500000001,server_id:2,username:'mindblock',country_code:'RU',negative_pp_badge:true,negative_pp_score_count:100,negative_pp_title:{tier:2,name:'Сомелье дристни'}};
        const map={id:110870,title:'Stop Saying We Sound Like Dragonforce',artist:'FRASER EDWARDS',creator:'Sh4rq_',user_id:2,status:'ranked',ranked:1,bpm:200,play_count:858300,favourite_count:435,
            submitted_date:'2025-03-06T12:00:00Z',ranked_date:'2025-07-08T12:00:00Z',last_updated:'2025-07-08T12:00:00Z',
            cover_url:'https://assets.ppy.sh/beatmaps/110870/covers/cover@2x.jpg',
            description:{description:'<p>Collab between me and Sayuka &lt;3</p><p>Hitsounds by riot1133</p><p>Top Diff — Me and Sayuka<br>Extra — Maaoobadt<br>Extreme — Gay Girl d<br>Insane — Me</p><script>window.__descriptionXss=true</script>'},
            source:'',tags:'dragon force maaoobadt riot1133 english power metal speed tapping guitar',genre:{id:11},language:{id:2},ratings:[0,2,0,0,1,0,2,4,10,31,120],
            beatmaps:Array.from({length:9},(_,index)=>({id:500+index,version:index===7?'Not Gonosto Collob':['Normal','Hard','Insane','Extra','Extreme','Top Difficulty','Expert'][index%7],
                mode:index===8?'mania':'osu',difficulty_rating:2+index*.96,total_length:278,hit_length:261,bpm:200,ar:10,cs:4.5,accuracy:10,drain:5.5,count_circles:1958,count_sliders:513,max_combo:3179,
                owners:[{id:2,username:'Sh4rq_'},{id:3,username:'Sayuka'}],leaderboard_enabled:true,status:'ranked'}))};
        if (localStorage.getItem('beatmap-fixture-native-only') === 'true') map.beatmaps = map.beatmaps.filter(b => b.mode === 'mania');
        const scores=[{rank:1,user:player,score:{id:1001,rank:'S',total_score:195033959,accuracy:.9981,max_combo:3179,pp:995,ruleset:'osu',statistics:{great:2465,ok:7,meh:0,miss:0},mods:[{acronym:'HD'}],ended_at:'2026-09-01T12:00:00Z',has_replay:true,replay_url:'/api/v2/scores/1001/download'}},
            {rank:2,user:rival,score:{id:1002,rank:'A',total_score:87716187,accuracy:.9287,max_combo:459,pp:-217,negative_pp:true,ruleset:'osu',statistics:{great:2224,ok:161,meh:24,miss:63},mods:[{acronym:'DT',settings:{speed_change:1.3}}],ended_at:'2026-09-05T12:00:00Z',has_replay:false}}];
        const comments=[{id:1,user:rival,body:'Отличная карта! <img src=x onerror="window.__commentXss=true">',created_at:'2026-09-04T12:00:00Z',votes:7,voted:false,can_delete:true}];
        window.__boardQueries=[]; window.__communityWrites=[];
        window.fetch=async(url,options={})=>{
            const parsed=new URL(url,location.origin), path=parsed.pathname.replace('/api/private/web-site','');
            if(!parsed.pathname.includes('/web-site/')) return actualFetch(url,options);
            let data;
            if(path==='/session')data={authenticated:true,user:player,csrf_token:'fixture',permissions:{}};
            else if(path==='/beatmapsets/110870')data=map;
            else if(/^\/beatmaps\/\d+\/scores$/.test(path)){
                window.__boardQueries.push({path,...Object.fromEntries(parsed.searchParams)});
                const mode = parsed.searchParams.get('mode') || 'osu';
                const modeScores = scores.map(item => ({...item,score:{...item.score,ruleset:mode}}));
                data={items:modeScores,top_score:modeScores[0],user_score:modeScores[1],mode,total:2,page:1,pages:1,page_size:50};
            }else if(path.endsWith('/activity'))data={plays:200,passes:31};
            else if(path==='/beatmapsets/110870/comments'&&(!options.method||options.method==='GET'))data={items:comments,total:comments.length,page:1,pages:1};
            else if(path==='/beatmapsets/110870/comments'&&options.method==='POST'){
                const body=JSON.parse(options.body); window.__communityWrites.push({path,body,method:options.method});
                comments.push({id:2,user:player,body:body.body,created_at:new Date().toISOString(),votes:0,voted:false,can_delete:true});data={ok:true};
            }else if(path==='/beatmap-comments/1/vote'){
                window.__communityWrites.push({path,method:options.method});comments[0].voted=JSON.parse(options.body).active;comments[0].votes=comments[0].voted?8:7;data={ok:true};
            }else return actualFetch(url,options);
            return new Response(JSON.stringify(data),{headers:{'Content-Type':'application/json'}});
        };
    }
    await rpc('Page.addScriptToEvaluateOnNewDocument',{source:'('+fixtures.toString()+')()'});
    await rpc('Storage.clearDataForOrigin',{origin:'http://127.0.0.1:8000',storageTypes:'local_storage'});
    await rpc('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
    await rpc('Page.navigate',{url:'http://127.0.0.1:8000/site/#beatmap/110870/507'});
    await until('document.querySelectorAll("#beatmap-leaderboard-body tr").length===2');
    await until('document.querySelectorAll(".beatmap-comment").length===1');
    assert.equal(await evaluate('document.querySelector("#beatmap-detail-mirror").href'),'https://beatconnect.io/b/110870');
    assert.equal(await evaluate('document.querySelector("#beatmap-selected-name").textContent'),'Not Gonosto Collob');
    assert.equal(await evaluate('document.querySelectorAll(".difficulty-row:not([hidden])").length'),8);
    assert.deepEqual(await evaluate('[...document.querySelectorAll("#beatmap-detail-modes button")].map(b => [b.dataset.mode, b.dataset.count, b.querySelector(".beatmap-mode-count")?.textContent || null])'),
        [['osu','8','8'],['taiko','0',null],['fruits','0',null],['mania','1','1']]);
    for (const mode of ['taiko', 'fruits']) {
        await evaluate('document.querySelector("#beatmap-detail-modes [data-mode='+mode+']").click()');
        await until('window.__boardQueries.at(-1).mode==="'+mode+'" && document.querySelectorAll("#beatmap-leaderboard-body tr").length===2');
        assert.equal(await evaluate('window.__boardQueries.at(-1).path'),'/beatmaps/507/scores');
        assert.equal(await evaluate('document.querySelectorAll(".difficulty-row:not([hidden])").length'),8);
        assert(await evaluate('document.querySelector("#beatmap-leaderboard-mode").textContent.includes("Конверт")'));
    }
    await rpc('Page.reload');
    await until('window.__boardQueries?.at(-1)?.mode==="fruits" && document.querySelectorAll("#beatmap-leaderboard-body tr").length===2');
    assert.equal(await evaluate('document.querySelector("#beatmap-detail-modes [aria-pressed=true]").dataset.mode'),'fruits');
    await evaluate('document.querySelector("#beatmap-detail-modes [data-mode=osu]").click()');
    await until('window.__boardQueries.at(-1).mode==="osu" && document.querySelectorAll("#beatmap-leaderboard-body tr").length===2');
    assert.equal(await evaluate('document.querySelectorAll("#beatmap-score-highlights .beatmap-highlight").length'),2);
    assert.equal(await evaluate('document.querySelectorAll("#beatmap-leaderboard-body a[download]").length'),1);
    assert(await evaluate('!window.__descriptionXss && !window.__commentXss && !document.querySelector("#beatmap-comment-list img[src=x]")'));
    const initialQueries=await evaluate('window.__boardQueries.length');
    await evaluate('document.querySelector("[data-beatmap-scope=country]").click();document.querySelector("#beatmap-mod-filters [data-mod=DT]").click()');
    assert.equal(await evaluate('window.__boardQueries.length'),initialQueries);
    assert.equal(await evaluate('document.querySelector("[data-beatmap-scope=global]").getAttribute("aria-pressed")'),'true');
    await writeFile('.test-tmp/beatmap-page-desktop.png',Buffer.from((await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true})).data,'base64'));
    await rpc('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
    assert(await evaluate('document.documentElement.scrollWidth <= 391'),'Mobile page must not overflow horizontally');
    await writeFile('.test-tmp/beatmap-page-mobile.png',Buffer.from((await rpc('Page.captureScreenshot',{format:'png',captureBeyondViewport:true})).data,'base64'));
    await evaluate('localStorage.setItem("beatmap-fixture-supporter","true")');
    await rpc('Page.reload');
    await until('document.querySelector("#beatmap-supporter-notice").hidden && document.querySelectorAll("#beatmap-leaderboard-body tr").length===2');
    await evaluate('document.querySelector("[data-beatmap-scope=country]").click()');
    await until('window.__boardQueries.at(-1).scope==="country"');
    await evaluate('document.querySelector("#beatmap-mod-filters [data-mod=DT]").click()');
    await until('window.__boardQueries.at(-1).mods==="DT"');
    await evaluate('document.querySelector("#beatmap-mod-filters [data-mod=NM]").click()');
    await until('window.__boardQueries.at(-1).mods==="NM"');
    await evaluate('document.querySelector("[data-beatmap-scope=friends]").click()');
    await until('window.__boardQueries.at(-1).scope==="friends"');
    await evaluate('document.querySelector("#beatmap-detail-modes [data-mode=mania]").click()');
    await until('document.querySelector("#beatmap-leaderboard-head").textContent.includes("MAX")');
    assert.equal(await evaluate('document.querySelectorAll(".difficulty-row:not([hidden])").length'),9);
    assert.equal(await evaluate('window.__boardQueries.at(-1).mode'),'mania');
    assert.equal(await evaluate('window.__boardQueries.at(-1).mods'),undefined);
    assert.equal(await evaluate('window.__boardQueries.at(-1).path'),'/beatmaps/507/scores');
    await evaluate('document.querySelector(\'.difficulty-row[data-beatmap-id="508"]\').click()');
    await until('window.__boardQueries.at(-1).path==="/beatmaps/508/scores"');
    assert.equal(await evaluate('window.__boardQueries.at(-1).mode'),'mania');
    assert(await evaluate('!document.querySelector("#beatmap-leaderboard-mode").textContent.includes("Конверт")'));
    await evaluate('document.querySelector("#beatmap-detail-modes [data-mode=osu]").click()');
    await until('document.querySelectorAll(".difficulty-row:not([hidden])").length===8');
    await evaluate('document.querySelector("#beatmap-comment-text").value="Проверка комментария"; document.querySelector("#beatmap-comment-form").requestSubmit()');
    await until('document.querySelectorAll(".beatmap-comment").length===2');
    await evaluate('document.querySelector(".beatmap-comment-vote").click()');
    await until('document.querySelector(".beatmap-comment-vote").getAttribute("aria-pressed")==="true"');
    const duplicateIds=await evaluate('(()=>{const ids=[...document.querySelectorAll("[id]")].map(e=>e.id);return ids.filter((id,index)=>ids.indexOf(id)!==index)})()');
    assert.deepEqual(duplicateIds,[]);
    await evaluate('localStorage.setItem("beatmap-fixture-native-only","true")');
    await rpc('Page.reload');
    await until('window.__boardQueries?.at(-1)?.mode==="mania" && document.querySelectorAll("#beatmap-leaderboard-body tr").length===2');
    assert.equal(await evaluate('document.querySelectorAll("#beatmap-detail-modes button").length'),4);
    assert.equal(await evaluate('document.querySelectorAll("#beatmap-detail-modes button:disabled").length'),3);
    assert.equal(await evaluate('document.querySelectorAll(".difficulty-row:not([hidden])").length'),1);
    assert(await evaluate('document.documentElement.scrollWidth <= 391'),'Native-only mode tabs must fit on mobile');
    assert.deepEqual(errors,[]);
    console.log('PASS: responsive beatmap page, difficulties/rulesets, safe description/comments, Beatconnect, replay links, supporter gates/filters, comment posting/voting, no duplicate IDs or browser exceptions.');
    }
} finally {
    if (ws?.readyState === WebSocket.OPEN) {
        try { await rpc('Browser.close'); } catch {}
        ws.close();
    }
    browser.kill();
}
