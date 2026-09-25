// Real site assets and browser controls; account writes stay inside this fixture.
import { spawn } from 'node:child_process';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
const output = 'E:/333/pythonEPTA/SERVAK/.test-tmp/site-avatar-20260912';
await mkdir(output, { recursive: true });
const port = 9357;
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run', `--remote-debugging-port=${port}`,
    `--user-data-dir=${output}/browser-${Date.now()}`, 'about:blank',
], { windowsHide: true, stdio: 'ignore' });
let ws, nextId = 0;
const pending = new Map(), errors = [];
function rpc(method, params = {}) {
    return new Promise((resolve, reject) => {
        const id = ++nextId;
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
    for (let i = 0; i < 80; i++) { if (await evaluate(expression)) return; await delay(125); }
    throw new Error(`UI timeout: ${expression}; errors=${JSON.stringify(errors)}; status=${await evaluate('document.querySelector("#avatar-status")?.textContent')}`);
}
async function selectFile(name) {
    const document = await rpc('DOM.getDocument');
    const input = await rpc('DOM.querySelector', { nodeId: document.root.nodeId, selector: '#avatar-file' });
    await rpc('DOM.setFileInputFiles', { nodeId: input.nodeId, files: [`${output}/${name}`] });
}
function fixture(profile) {
    const actualFetch = window.fetch;
    const own = profile.user;
    own.username = 'Avatar test';
    own.avatar_url = '/site/soms-default-avatar.png?fixture=old';
    own.has_custom_avatar = true;
    window.avatarWrites = [];
    window.avatarFailNext = false;
    window.fetch = async (url, options = {}) => {
        const path = new URL(url, location.origin).pathname.replace('/api/private/web-site', '');
        const method = options.method || 'GET';
        let data;
        if (path === '/session' && method === 'GET') data = { authenticated: !window.avatarGuest, user: own, csrf_token: 'avatar-fixture', permissions: {} };
        else if (path === '/session' && method === 'DELETE') { window.avatarGuest = true; data = {}; }
        else if (path === '/notifications') data = { items: [], unread: 0, preferences: {}, supporter: false };
        else if (path.endsWith('/friendship')) data = { is_friend: false, is_mutual: false, follower_count: 0 };
        else if (/^\/users\/\d+$/.test(path)) {
            const id = Number(path.split('/')[2]);
            data = { ...profile, user: { ...own, id, server_id: id, username: id === own.id ? own.username : 'Other player' } };
        } else if (path.includes('/scores') || path.includes('/achievements')) data = { items: [], total: 0, pages: 1, page: 1, latest: [], groups: [] };
        else if (path === '/me/avatar') {
            if (options.headers.get('X-CSRF-Token') !== 'avatar-fixture') throw new Error('Avatar mutation lacks CSRF');
            if (window.avatarFailNext) { window.avatarFailNext = false; return new Response(JSON.stringify({ detail: 'The uploaded file is not a valid image' }), { status: 422, headers: { 'Content-Type': 'application/json' } }); }
            if (method === 'POST') {
                const file = options.body.get('avatar');
                if (!(file instanceof File) || file.size < 1) throw new Error('Expected multipart avatar file');
                own.avatar_url = '/site/soms-default-avatar.png?fixture=new';
                own.has_custom_avatar = true;
            } else if (method === 'DELETE') {
                own.avatar_url = '/site/soms-default-avatar.png';
                own.has_custom_avatar = false;
            } else throw new Error('Unexpected avatar method');
            window.avatarWrites.push(method);
            data = { url: own.avatar_url, has_custom_avatar: own.has_custom_avatar };
        } else {
            if (method !== 'GET') throw new Error('Unexpected mutation: ' + path);
            return actualFetch(url, options);
        }
        return new Response(JSON.stringify(data), { headers: { 'Content-Type': 'application/json' } });
    };
}
try {
    let target;
    for (let i = 0; i < 60 && !target; i++) {
        try { target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(item => item.type === 'page'); }
        catch { await delay(150); }
    }
    assert(target, 'Browser did not start');
    ws = new WebSocket(target.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
    ws.onmessage = event => {
        const data = JSON.parse(event.data);
        if (data.method === 'Runtime.exceptionThrown') errors.push(data.params.exceptionDetails.exception?.description || data.params.exceptionDetails.text);
        const task = pending.get(data.id);
        if (!task) return;
        pending.delete(data.id);
        data.error ? task.reject(new Error(data.error.message)) : task.resolve(data.result);
    };
    await rpc('Runtime.enable'); await rpc('Page.enable');
    const assets = new Map();
    if (!process.argv.includes('--deployed')) {
        for (const [url, file, type] of [['/site/', 'index.html', 'text/html'], ['/site/app.js', 'app.js', 'text/javascript'], ['/site/styles.css', 'styles.css', 'text/css']])
            assets.set(url, { body: (await readFile(`static/site/${file}`)).toString('base64'), type });
        ws.addEventListener('message', event => {
            const data = JSON.parse(event.data);
            if (data.method !== 'Fetch.requestPaused') return;
            const asset = assets.get(new URL(data.params.request.url).pathname);
            (asset ? rpc('Fetch.fulfillRequest', { requestId: data.params.requestId, responseCode: 200, responseHeaders: [{ name: 'Content-Type', value: asset.type }], body: asset.body })
                : rpc('Fetch.continueRequest', { requestId: data.params.requestId })).catch(error => errors.push(error.message));
        });
        await rpc('Fetch.enable', { patterns: [{ urlPattern: '*/site/' }, { urlPattern: '*/site/app.js*' }, { urlPattern: '*/site/styles.css*' }] });
    }
    const profile = await (await fetch('http://127.0.0.1:8000/api/private/web-site/users/1?mode=osu')).json();
    assert(profile.user?.id, 'Fixture profile unavailable');
    await rpc('Page.addScriptToEvaluateOnNewDocument', { source: `(${fixture.toString()})(${JSON.stringify(profile)})` });
    await rpc('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1080, deviceScaleFactor: 1, mobile: false });
    await rpc('Page.navigate', { url: `http://127.0.0.1:8000/site/#profile/${profile.user.id}` });
    await until('document.querySelector("#profile-name")?.textContent.includes("Avatar test")');
    assert(await evaluate('!document.querySelector("#profile-avatar-edit").hidden'));
    await evaluate('document.querySelector("#profile-avatar-edit").click()');
    await until('!document.querySelector("#settings-content").hidden && !!document.querySelector("#settings-avatar-preview img")');
    const png = await evaluate('(()=>{const canvas=document.createElement("canvas");canvas.width=96;canvas.height=48;const ctx=canvas.getContext("2d");ctx.fillStyle="#ff66aa";ctx.fillRect(0,0,96,48);return canvas.toDataURL().split(",")[1];})()');
    await writeFile(`${output}/avatar.png`, Buffer.from(png, 'base64'));
    await writeFile(`${output}/invalid.txt`, 'invalid');
    await selectFile('invalid.txt');
    await until('document.querySelector("#avatar-status").classList.contains("is-error")');
    assert.equal(await evaluate('window.avatarWrites.length'), 0);
    await selectFile('avatar.png');
    await until('!document.querySelector("#avatar-pending-actions").hidden');
    assert(await evaluate('document.querySelector("#settings-avatar-preview img").src.startsWith("blob:")'));
    await evaluate('document.querySelector("#avatar-cancel").click()');
    assert(await evaluate('document.querySelector("#avatar-pending-actions").hidden && document.querySelector("#settings-avatar-preview img").src.includes("fixture=old")'));
    await selectFile('avatar.png');
    await until('!document.querySelector("#avatar-save").disabled');
    await evaluate('window.avatarFailNext=true; document.querySelector("#avatar-form").requestSubmit()');
    await until('document.querySelector("#avatar-status").classList.contains("is-error")');
    assert(await evaluate('!document.querySelector("#avatar-pending-actions").hidden && document.querySelector("#header-avatar img").src.includes("fixture=old")'));
    await evaluate('document.querySelector("#avatar-form").requestSubmit()');
    await until('document.querySelector("#avatar-status").textContent==="Аватар сохранён."');
    assert(await evaluate('["#header-avatar","#user-menu-avatar","#profile-avatar","#settings-avatar-preview"].every(s=>document.querySelector(s+" img").src.includes("fixture=new"))'));
    console.log('PASS: own-profile entry, preview, cancel, rejected files, multipart + CSRF and immediate avatar refresh.');
    await evaluate('document.querySelector("#avatar-settings").scrollIntoView({block:"start"})');
    await until('document.querySelector("#settings-avatar-preview img")?.naturalWidth > 0');
    await writeFile(`${output}/desktop.png`, Buffer.from((await rpc('Page.captureScreenshot', { format: 'png' })).data, 'base64'));
    await rpc('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
    assert(await evaluate('document.documentElement.scrollWidth <= innerWidth'));
    await writeFile(`${output}/mobile.png`, Buffer.from((await rpc('Page.captureScreenshot', { format: 'png' })).data, 'base64'));
    await evaluate('document.querySelector("#avatar-delete").click()');
    await until('document.querySelector("#avatar-status").textContent==="Аватар удалён."');
    assert(await evaluate('document.querySelector("#avatar-delete").disabled && !document.querySelector("#header-avatar img").src.includes("fixture=")'));
    await evaluate('location.hash="profile/999"');
    await until('document.querySelector("#profile-name").textContent.includes("Other player")');
    assert(await evaluate('document.querySelector("#profile-avatar-edit").hidden'));
    await evaluate('location.hash="settings"');
    await until('!document.querySelector("#settings-content").hidden');
    await evaluate('document.querySelector("#logout-button").click()');
    await until('document.querySelector("#auth-button").hidden===false');
    await evaluate('location.hash="settings"');
    await until('document.querySelector("#settings-content").hidden && !document.querySelector("#settings-guest").hidden');
    assert.deepEqual(await evaluate('window.avatarWrites'), ['POST', 'DELETE']);
    assert.deepEqual(errors, []);
    console.log('PASS: deletion/default avatar, mobile layout, foreign profile and guest restrictions; no real account writes.');
} finally {
    ws?.close(); browser.kill();
}
