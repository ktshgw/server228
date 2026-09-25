// Isolated Edge/CDP smoke check. Admin writes are intercepted in this browser;
// public rankings/profile requests exercise the deployed site.
import { spawn } from 'node:child_process';
import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
const port = 9342;
const browser = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', [
    '--headless=new', '--disable-gpu', '--no-first-run', `--remote-debugging-port=${port}`,
    '--user-data-dir=E:/333/pythonEPTA/SERVAK/.test-tmp/soms-mmr-browser-check', 'about:blank',
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
    for (let i = 0; i < 120; i++) {
        try { if ((await fetch('http://127.0.0.1:8000/api/private/web-site/rankings?section=somsai')).ok) break; } catch {}
        await delay(250);
    }
    await rpc('Page.navigate', { url: 'http://127.0.0.1:8000/site/#rankings' });
    await until('!!document.querySelector("#rankings-body tr")');
    await evaluate('document.querySelector("[data-ranking-section=somsai]").click()');
    await until('document.querySelector("#rankings-head")?.textContent.includes("MMR SOMSAI")');
    assert(await evaluate('!document.querySelector("#ranking-somsai-filter").hidden'));
    await evaluate('document.querySelector("#ranking-somsai-format").value="2v2"; document.querySelector("#ranking-somsai-format").dispatchEvent(new Event("change"))');
    const board = await evaluate('fetch("/api/private/web-site/rankings?section=somsai&mode=osu").then(r=>r.json())');
    assert(board.items.length, 'Need a SOMSAI profile in deployed catalogue');
    const user = board.items[0].user;
    const id = user.server_id || user.id;
    await rpc('Page.navigate', { url: `http://127.0.0.1:8000/site/#profile/${id}` });
    await until('!document.querySelector("#profile-content")?.hidden && document.querySelector("#profile-mmr")?.textContent !== "—"');
    for (const format of ['1v1', '2v2']) {
        await evaluate(`document.querySelector('#profile-somsai-format').value='${format}'; document.querySelector('#profile-somsai-format').dispatchEvent(new Event('change'))`);
        const rating = await evaluate(`fetch('/api/private/web-site/users/${id}?somsai_format=${format}').then(r=>r.json()).then(p=>p.somsai)`);
        const expected = rating.mmr == null ? '—' : new Intl.NumberFormat('ru-RU').format(Math.round(rating.mmr));
        await until(`document.querySelector('#profile-mmr').textContent === ${JSON.stringify(expected)}`);
    }
    console.log('PASS: deployed SOMSAI leaderboard and real profile MMR in both formats.');

    function adminFixtures() {
        const actualFetch = window.fetch;
        const items = ['1v1', '2v2'].map((format, i) => ({ key: `0:0:${format}`, ruleset_id: 0, variant_id: 0, format, mmr: 1500+i*100, games: 12, version: '0'.repeat(64) }));
        const user = { id: 42, username: 'Browser fixture', country_code: 'RU', is_active: true, somsai_mmr: items };
        window.__mmrWrites = [];
        window.fetch = async (url, options = {}) => {
            if (!String(url).includes('/admin-panel/')) return actualFetch(url, options);
            const path = String(url).split('/admin-panel')[1];
            let data;
            if (path === '/session') data = { csrf_token: 'browser-fixture', user: { id: 99, username: 'Test admin', roles: { owner: true, administrator: true, ranker: true } } };
            else if (path === '/countries') data = [];
            else if (path.startsWith('/users?')) data = { items: [user], page: 1, pages: 1, total: 1 };
            else if (path === '/users/42') data = user;
            else if (path.startsWith('/users/42/scores')) data = { items: [], total: 0 };
            else if (path.startsWith('/users/42/somsai-mmr/')) {
                const body = JSON.parse(options.body);
                window.__mmrWrites.push({ path, body });
                data = { ...items.find(item => path.endsWith(item.format)), mmr: body.mmr };
            } else throw new Error('Unexpected fixture request: ' + path);
            return new Response(JSON.stringify(data), { status: 200, headers: { 'Content-Type': 'application/json' } });
        };
    }
    await rpc('Page.addScriptToEvaluateOnNewDocument', { source: `(${adminFixtures.toString()})()` });
    await rpc('Page.navigate', { url: 'http://127.0.0.1:8000/admin/#users' });
    await until('!!document.querySelector("#users-table-body .row-action")');
    await evaluate('document.querySelector("#users-table-body .row-action").click()');
    await until('document.querySelector("#somsai-mmr-pool")?.options.length === 2');
    assert.equal(await evaluate('!!document.querySelector("[data-view=ranked]")'), false);
    await evaluate('document.querySelector("[data-user-tab=somsai-mmr]").click(); document.querySelector("#somsai-mmr-pool").value="0:0:2v2"; document.querySelector("#somsai-mmr-pool").dispatchEvent(new Event("change"));');
    await until('document.querySelector("#somsai-mmr-current").textContent.includes("1 600")');
    await evaluate('document.querySelector("#somsai-mmr-value").value="2100"; document.querySelector("#somsai-mmr-reason").value="Browser validation"; document.querySelector("#somsai-mmr-form").requestSubmit();');
    await until('window.__mmrWrites.length === 1 && document.querySelector("#somsai-mmr-current").textContent.includes("2 100")');
    const writes = await evaluate('window.__mmrWrites');
    assert.equal(writes[0].path, '/users/42/somsai-mmr/0/0/2v2');
    assert.equal(writes[0].body.mmr, 2100);
    assert.equal(writes[0].body.expected_version, '0'.repeat(64));
    assert.deepEqual(errors, []);
    console.log('PASS: admin DOM, format selector and MMR save payload; intercepted writes only; no browser exceptions.');
} finally {
    if (ws?.readyState === WebSocket.OPEN) {
        try { await rpc('Browser.close'); } catch { /* closed */ }
        ws.close();
    }
    browser.kill();
}
