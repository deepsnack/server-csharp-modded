'use strict';

// 通行证商人管理页：复用通行证后台的 admin token / 登录流程 / Portal SSO。
const ADMIN_API = '/battlepass/api/admin';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
const ICON_API = '/battlepass/api/icons/';
const TRADER_ID = '66f1b2c3d4e5a6b7c8d90011'; // 与 BattlePassTraderSync.TraderIdHex 一致
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

// 支持 Portal SSO：URL 片段 #sso=token
(function () {
    const m = location.hash.match(/sso=([a-zA-Z0-9]+)/);
    if (m) { ADMIN_TOKEN = m[1]; sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN); history.replaceState(null, '', location.pathname); }
})();

function el(id) { return document.getElementById(id); }
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN };
    const res = await fetch(ADMIN_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// ---- 登录 ----
async function adminLogin() {
    const password = el('admin-pass').value;
    const res = await fetch(REGISTER_ADMIN_LOGIN, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password })
    });
    const r = await res.json();
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    ADMIN_TOKEN = r.token;
    sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN);
    enterConsole();
}
function logout() { ADMIN_TOKEN = ''; sessionStorage.removeItem('bp_admin_token'); el('trader-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
function enterConsole() {
    el('login-view').classList.add('hidden');
    el('trader-view').classList.remove('hidden');
    loadMeta(); loadOffers();
}

// ---- 标签 ----
document.querySelectorAll('.tab[data-tab]').forEach(tab => {
    tab.onclick = () => {
        document.querySelectorAll('.tab[data-tab]').forEach(t => t.classList.remove('active'));
        tab.classList.add('active');
        document.querySelectorAll('.tab-pane').forEach(p => p.classList.add('hidden'));
        el('pane-' + tab.dataset.tab).classList.remove('hidden');
    };
});

// ---- 商人设置 ----
async function loadMeta() {
    const r = await api('/trader-meta');
    if (!r.success) { if (r.message) toast(r.message, false); return; }
    const c = r.config || {};
    el('m-name').value = c.name || '';
    el('m-nickname').value = c.nickname || '';
    el('m-surname').value = c.surname || '';
    el('m-desc').value = c.description || '';
    el('m-location').value = c.location || '';
    el('m-currency').value = c.currency || 'RUB';
    el('m-resupply').value = c.resupplySeconds ?? 3600;
    el('m-unlocked').checked = c.unlockedByDefault !== false;
    el('m-insurance').checked = !!c.insuranceAvailable;
    el('m-repair').checked = !!c.repairAvailable;
    refreshAvatarPreview(c.avatarFile);
}

function refreshAvatarPreview(avatarFile) {
    const img = el('avatar-preview');
    if (avatarFile) {
        const ext = avatarFile.toLowerCase().endsWith('.jpg') ? '.jpg' : '.png';
        img.src = '/files/trader/avatar/' + TRADER_ID + ext + '?t=' + Date.now();
        img.style.display = 'block';
    } else {
        img.removeAttribute('src');
        img.style.display = 'none';
    }
}

el('save-meta').onclick = async () => {
    const cfg = {
        name: el('m-name').value.trim(),
        nickname: el('m-nickname').value.trim(),
        surname: el('m-surname').value.trim() || null,
        description: el('m-desc').value.trim(),
        location: el('m-location').value.trim(),
        currency: el('m-currency').value,
        resupplySeconds: +el('m-resupply').value || 3600,
        unlockedByDefault: el('m-unlocked').checked,
        insuranceAvailable: el('m-insurance').checked,
        repairAvailable: el('m-repair').checked
    };
    if (!cfg.name) return toast('请填写商人名称', false);
    const r = await api('/trader-meta', 'POST', cfg);
    toast(r.success ? '已保存（游戏内可能需重登刷新）' : (r.message || '失败'), r.success);
};

// ---- 头像上传 ----
el('avatar-file').onchange = () => {
    const f = el('avatar-file').files[0];
    if (!f) return;
    const reader = new FileReader();
    reader.onload = () => { el('avatar-preview').src = reader.result; el('avatar-preview').style.display = 'block'; };
    reader.readAsDataURL(f);
};
el('upload-avatar').onclick = async () => {
    const f = el('avatar-file').files[0];
    if (!f) return toast('请先选择图片', false);
    if (f.size > 2 * 1024 * 1024) return toast('图片超过 2MB', false);
    const dataUrl = await new Promise((res, rej) => {
        const r = new FileReader(); r.onload = () => res(r.result); r.onerror = rej; r.readAsDataURL(f);
    });
    const r = await api('/trader-avatar', 'POST', { image: dataUrl });
    if (r.success) { toast('头像已上传（游戏内可能需重登刷新）', true); refreshAvatarPreview(r.avatarFile); }
    else toast(r.message || '上传失败', false);
};

// ---- 货架管理 ----
function parseCost(text) {
    const out = [];
    (text || '').split(/\r?\n/).forEach(line => {
        const parts = line.trim().split(/\s+/);
        if (parts.length >= 2 && isTpl(parts[0])) {
            const count = parseInt(parts[1], 10);
            if (count > 0) out.push({ tpl: parts[0], count });
        }
    });
    return out;
}
function costToText(cost) {
    return (cost || []).map(c => `${c.tpl} ${c.count}`).join('\n');
}

el('o-tpl').addEventListener('input', () => {
    const tpl = el('o-tpl').value.trim();
    const icon = el('o-tpl-icon');
    if (isTpl(tpl)) { icon.src = ICON_API + tpl; icon.style.display = 'inline-block'; icon.onerror = () => icon.style.display = 'none'; }
    else { icon.style.display = 'none'; }
});

async function loadOffers() {
    const r = await api('/offers');
    if (!r.success) return;
    const box = el('offer-rows'); box.innerHTML = '';
    (r.offers || []).forEach(o => {
        const div = document.createElement('div');
        div.className = 'row-item';
        const costStr = (o.cost && o.cost.length) ? o.cost.map(c => `${c.count}×${c.tpl.slice(0, 6)}…`).join(' + ') : '免费';
        const stockStr = (o.stock <= 0) ? '∞' : o.stock;
        const limitStr = (o.buyLimit > 0) ? ('限购' + o.buyLimit) : '不限购';
        const left = document.createElement('div');
        left.innerHTML = `<div class="offer-title">${isTpl(o.tpl) ? `<img class="rw-icon" src="${ICON_API}${o.tpl}" alt="" onerror="this.remove()" />` : ''}${esc(o.name || o.id)}</div>
            <div class="meta">${esc(o.id)} · tpl ${esc(o.tpl.slice(0, 8))}… · 库存 ${stockStr} · ${limitStr} · 价 ${esc(costStr)}</div>`;
        div.appendChild(left);
        const acts = document.createElement('div'); acts.className = 'acts';
        const edit = document.createElement('button'); edit.className = 'mini'; edit.textContent = '编辑'; edit.onclick = () => fillOffer(o);
        const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '删除'; del.onclick = () => delOffer(o.id);
        acts.appendChild(edit); acts.appendChild(del); div.appendChild(acts);
        box.appendChild(div);
    });
    if (!r.offers || r.offers.length === 0) box.innerHTML = '<div class="row-item">暂无货架项</div>';
}

function fillOffer(o) {
    el('o-id').value = o.id; el('o-tpl').value = o.tpl; el('o-name').value = o.name || '';
    el('o-stock').value = o.stock ?? -1; el('o-buylimit').value = o.buyLimit ?? 0;
    el('o-cost').value = costToText(o.cost);
    el('o-tpl').dispatchEvent(new Event('input'));
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function clearOffer() {
    el('o-id').value = ''; el('o-tpl').value = ''; el('o-name').value = '';
    el('o-stock').value = -1; el('o-buylimit').value = 0; el('o-cost').value = '';
    el('o-tpl-icon').style.display = 'none';
}
el('clear-offer').onclick = clearOffer;
el('refresh-offers').onclick = loadOffers;

el('save-offer').onclick = async () => {
    const offer = {
        id: el('o-id').value.trim(),
        tpl: el('o-tpl').value.trim(),
        name: el('o-name').value.trim() || null,
        stock: +el('o-stock').value,
        buyLimit: +el('o-buylimit').value,
        cost: parseCost(el('o-cost').value)
    };
    if (!offer.id) return toast('请填写货架项 ID', false);
    if (!isTpl(offer.tpl)) return toast('商品 tpl 必须是 24 位十六进制 id', false);
    const r = await api('/offers', 'POST', offer);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) { clearOffer(); loadOffers(); }
};
async function delOffer(id) {
    if (!confirm('删除货架项 ' + id + ' ?')) return;
    const r = await api('/offers', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success); loadOffers();
}

el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;

if (ADMIN_TOKEN) enterConsole();
