'use strict';

// 通行证网页商店管理页：复用通行证后台 admin token / 登录 / Portal SSO；货架项即 BpTraderOffer（存 shop.json）。
const ADMIN_API = '/battlepass/api/admin';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
const ICON_API = '/battlepass/api/icons/';
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

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
function logout() { ADMIN_TOKEN = ''; sessionStorage.removeItem('bp_admin_token'); el('shop-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }

let offerCostEd = null; // 统一价格编辑器（BpPrice）
function enterConsole() {
    el('login-view').classList.add('hidden');
    el('shop-view').classList.remove('hidden');
    if (!offerCostEd) offerCostEd = BpPrice.createEditor(el('o-cost-editor'));
    loadOffers();
}

// ---- 商品 tpl 搜索 ----
el('o-tpl').addEventListener('input', () => {
    const tpl = el('o-tpl').value.trim();
    const icon = el('o-tpl-icon');
    if (isTpl(tpl)) { icon.src = ICON_API + tpl; icon.style.display = 'inline-block'; icon.onerror = () => icon.style.display = 'none'; }
    else { icon.style.display = 'none'; }
});

BpPicker.attachItem(el('o-tpl'), el('o-tpl-results'), ds => {
    el('o-tpl').value = ds.tpl;
    const icon = el('o-tpl-icon');
    icon.src = ICON_API + ds.tpl; icon.style.display = 'inline-block';
    icon.onerror = () => icon.style.display = 'none';
    if (!el('o-name').value.trim()) el('o-name').value = ds.name || '';
});

// ---- 货架列表 ----
async function loadOffers() {
    const r = await api('/shop');
    if (!r.success) { if (r.message) toast(r.message, false); return; }
    const rh = el('o-refresh-hours');
    if (rh) rh.value = (r.refreshSeconds > 0) ? +(r.refreshSeconds / 3600).toFixed(2) : 0;
    const box = el('offer-rows'); box.innerHTML = '';
    (r.offers || []).forEach(o => {
        const div = document.createElement('div');
        div.className = 'row-item';
        const costStr = (o.cost && o.cost.length) ? o.cost.map(c => `${c.count}×${c.tpl.slice(0, 6)}…`).join(' + ') : '免费';
        const stockStr = (o.stock <= 0) ? '∞' : o.stock;
        const limitStr = (o.buyLimit > 0) ? ('限购' + o.buyLimit) : '不限购';
        const qtyStr = (o.sellCount > 1) ? ` ×${o.sellCount}` : '';
        const left = document.createElement('div');
        left.innerHTML = `<div class="offer-title">${isTpl(o.tpl) ? `<img class="rw-icon" src="${ICON_API}${o.tpl}" alt="" onerror="this.remove()" />` : ''}${esc(o.name || o.id)}${qtyStr}</div>
            <div class="meta">${esc(o.id)} · tpl ${esc(o.tpl.slice(0, 8))}… · 库存 ${stockStr} · ${limitStr} · 价 ${esc(costStr)}</div>`;
        div.appendChild(left);
        const acts = document.createElement('div'); acts.className = 'acts';
        const edit = document.createElement('button'); edit.className = 'mini'; edit.textContent = '编辑'; edit.onclick = () => fillOffer(o);
        const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '删除'; del.onclick = () => delOffer(o.id);
        acts.appendChild(edit); acts.appendChild(del); div.appendChild(acts);
        box.appendChild(div);
    });
    if (!r.offers || r.offers.length === 0) box.innerHTML = '<div class="row-item">暂无商品</div>';
}

function fillOffer(o) {
    el('o-id').value = o.id; el('o-tpl').value = o.tpl; el('o-name').value = o.name || '';
    el('o-sellcount').value = o.sellCount > 0 ? o.sellCount : 1;
    el('o-stock').value = o.stock ?? -1; el('o-buylimit').value = o.buyLimit ?? 0;
    offerCostEd.setCost(o.cost);
    el('o-tpl').dispatchEvent(new Event('input'));
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function clearOffer() {
    el('o-id').value = ''; el('o-tpl').value = ''; el('o-name').value = '';
    el('o-sellcount').value = 1; el('o-stock').value = -1; el('o-buylimit').value = 0; offerCostEd.clear();
    el('o-tpl-icon').style.display = 'none';
}
el('clear-offer').onclick = clearOffer;
el('refresh-offers').onclick = loadOffers;

if (el('save-refresh')) el('save-refresh').onclick = async () => {
    const hours = Math.max(0, +el('o-refresh-hours').value || 0);
    const r = await api('/shop/refresh-period', 'POST', { seconds: Math.round(hours * 3600) });
    toast(r.success ? (hours > 0 ? `已设刷新周期 ${hours} 小时` : '已关闭自动刷新') : (r.message || '失败'), r.success);
};

el('save-offer').onclick = async () => {
    const { cost, invalid } = offerCostEd.collect();
    if (invalid) return toast('价格项有误：物品需选有效物品、数量需为正整数', false);
    const offer = {
        id: el('o-id').value.trim(),
        tpl: el('o-tpl').value.trim(),
        name: el('o-name').value.trim() || null,
        sellCount: Math.max(1, +el('o-sellcount').value || 1),
        stock: +el('o-stock').value,
        buyLimit: +el('o-buylimit').value,
        cost
    };
    if (!offer.id) return toast('请填写商品 ID', false);
    if (!isTpl(offer.tpl)) return toast('商品 tpl 必须是 24 位十六进制 id', false);
    const r = await api('/shop', 'POST', offer);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) { clearOffer(); loadOffers(); }
};
async function delOffer(id) {
    if (!confirm('删除商品 ' + id + ' ?')) return;
    const r = await api('/shop', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success); loadOffers();
}

el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;

if (ADMIN_TOKEN) enterConsole();
