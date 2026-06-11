'use strict';

// 跳蚤黑名单接管管理页。复用通行证后台 admin token / SSO。
const FLEA_API = '/battlepass/api/admin/flea';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
const ICON_API = '/battlepass/api/icons/';
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';
let CFG = null;

(function () {
    const m = location.hash.match(/sso=([a-zA-Z0-9]+)/);
    if (m) { ADMIN_TOKEN = m[1]; sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN); history.replaceState(null, '', location.pathname); }
})();

function el(id) { return document.getElementById(id); }
function esc(s) { return (s == null ? '' : String(s)).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) { const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err'); setTimeout(() => { t.className = 'toast'; }, 2600); }
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN };
    const res = await fetch(FLEA_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

async function adminLogin() {
    const password = el('admin-pass').value;
    const res = await fetch(REGISTER_ADMIN_LOGIN, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password }) });
    const r = await res.json();
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    ADMIN_TOKEN = r.token; sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN);
    enterConsole();
}
function logout() { ADMIN_TOKEN = ''; sessionStorage.removeItem('bp_admin_token'); el('flea-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
function enterConsole() { el('login-view').classList.add('hidden'); el('flea-view').classList.remove('hidden'); loadConfig(); }

// 三态下拉：不接管 / 开 / 关
function fillToggle(sel, val) {
    sel.innerHTML = `<option value="">不接管（保持原值）</option><option value="true">开启</option><option value="false">关闭</option>`;
    sel.value = val === true ? 'true' : (val === false ? 'false' : '');
}

async function loadConfig() {
    const r = await api('/config');
    if (!r.success) { toast(r.message || '加载失败', false); return; }
    CFG = r.config || {};
    CFG.blacklistTpls = CFG.blacklistTpls || [];
    CFG.whitelistTpls = CFG.whitelistTpls || [];
    CFG.blacklistCategories = CFG.blacklistCategories || [];
    document.querySelectorAll('select[data-k]').forEach(sel => fillToggle(sel, CFG[sel.dataset.k]));
    renderChips();
}

function renderChips() {
    chipBox('bl-chips', CFG.blacklistTpls, 'blacklistTpls');
    chipBox('wl-chips', CFG.whitelistTpls, 'whitelistTpls');
    chipBox('cat-chips', CFG.blacklistCategories, 'blacklistCategories');
}
function chipBox(boxId, arr, key) {
    const box = el(boxId); box.innerHTML = '';
    (arr || []).forEach(v => {
        const chip = document.createElement('span'); chip.className = 'chip';
        chip.innerHTML = `<img class="rw-icon" src="${ICON_API}${v}" onerror="this.remove()" /> ${esc(v)} `;
        const x = document.createElement('button'); x.className = 'chip-x'; x.textContent = '×';
        x.onclick = () => { CFG[key] = arr.filter(a => a !== v); renderChips(); };
        chip.appendChild(x); box.appendChild(chip);
    });
    if (!arr || arr.length === 0) box.innerHTML = '<span class="meta">（空）</span>';
}

// 搜索加入黑/白名单
async function searchInto(qId, rowsId, key) {
    const q = el(qId).value.trim();
    const r = await api(`/search?q=${encodeURIComponent(q)}&source=all`);
    const box = el(rowsId); box.innerHTML = '';
    if (!r.success) return;
    (r.items || []).forEach(it => {
        const div = document.createElement('div'); div.className = 'search-item';
        div.innerHTML = `<img class="rw-icon" src="${ICON_API}${it.tpl}" onerror="this.remove()" /><div class="si-text"><div class="si-name">${esc(it.name || it.tpl)} ${it.isMod ? '<span class="badge mod">MOD</span>' : ''}</div><div class="meta">${esc(it.tpl)}</div></div>`;
        div.onclick = () => { if (!CFG[key].includes(it.tpl)) CFG[key].push(it.tpl); renderChips(); box.innerHTML = ''; el(qId).value = ''; };
        box.appendChild(div);
    });
    if (!r.items || r.items.length === 0) box.innerHTML = '<div class="search-item">无结果</div>';
}

el('save-flea').onclick = async () => {
    document.querySelectorAll('select[data-k]').forEach(sel => {
        const v = sel.value; CFG[sel.dataset.k] = v === '' ? null : (v === 'true');
    });
    const r = await api('/config', 'POST', CFG);
    toast(r.success ? '已保存并应用' : (r.message || '失败'), r.success);
};

el('bl-search').onclick = () => searchInto('bl-q', 'bl-search-rows', 'blacklistTpls');
el('wl-search').onclick = () => searchInto('wl-q', 'wl-search-rows', 'whitelistTpls');
el('bl-q').addEventListener('keydown', e => { if (e.key === 'Enter') el('bl-search').click(); });
el('wl-q').addEventListener('keydown', e => { if (e.key === 'Enter') el('wl-search').click(); });
el('cat-add').onclick = () => {
    const v = el('cat-input').value.trim();
    if (!isTpl(v)) return toast('父类 ID 必须是 24 位 hex', false);
    if (!CFG.blacklistCategories.includes(v)) CFG.blacklistCategories.push(v);
    el('cat-input').value = ''; renderChips();
};

el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;

if (ADMIN_TOKEN) enterConsole();
