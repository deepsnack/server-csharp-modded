'use strict';

// 通行证网页商店管理页：复用通行证后台 admin token / 登录 / Portal SSO；货架项即 BpTraderOffer（存 shop.json）。
// REGISTER_ADMIN_LOGIN / getAdminToken / isCollaborator / ensureAdminSession / submitChange 由 auth.js 提供。
const ADMIN_API = '/battlepass/api/admin';
const ICON_API = '/battlepass/api/icons/';
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

function el(id) { return document.getElementById(id); }
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() };
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
function logout() {
    ADMIN_TOKEN = ''; clearAdminToken(); clearActorType();
    const b = el('collab-banner'); if (b) b.remove();
    el('shop-view').classList.add('hidden'); el('login-view').classList.remove('hidden');
}

let offerCostEd = null; // 统一价格编辑器（BpPrice）
function enterConsole() {
    el('login-view').classList.add('hidden');
    el('shop-view').classList.remove('hidden');
    if (isCollaborator()) showCollaboratorBanner();
    if (!offerCostEd) offerCostEd = BpPrice.createEditor(el('o-cost-editor'));
    loadOffers();
}

// 协管态提示条：修改需管理员审核后生效
function showCollaboratorBanner() {
    if (el('collab-banner')) return;
    const bar = document.createElement('div');
    bar.id = 'collab-banner';
    bar.className = 'hint';
    bar.style.cssText = 'margin:10px 16px;padding:10px 14px;border-left:4px solid #e0a030;background:rgba(224,160,48,.12);font-weight:600;';
    bar.textContent = '协管模式：你的修改将提交管理员审核后生效';
    const view = el('shop-view');
    const topbar = view.querySelector('.topbar');
    if (topbar && topbar.nextSibling) view.insertBefore(bar, topbar.nextSibling);
    else view.insertBefore(bar, view.firstChild);
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

// ---- 商品类型 / 奖池 / 刷新模式联动 ----
let shopPools = []; // [{id,name}]，供限定抽奖券选择

function rewardType() { return (el('o-reward-type') && el('o-reward-type').value) || 'item'; }
function isVirtualType(t) { return t === 'lotteryGlobalTickets' || t === 'lotteryPoolTickets' || t === 'lotteryExchangeCoins'; }

function renderPoolOptions(selectedId) {
    const sel = el('o-pool'); if (!sel) return;
    sel.innerHTML = shopPools.length
        ? shopPools.map(p => `<option value="${esc(p.id)}">${esc(p.name || p.id)}</option>`).join('')
        : '<option value="">（暂无奖池，请先在抽奖后台创建）</option>';
    if (selectedId) sel.value = selectedId;
}

function applyTypeUi() {
    const t = rewardType();
    const virtual = isVirtualType(t);
    if (el('o-item-wrap')) el('o-item-wrap').style.display = virtual ? 'none' : '';
    if (el('o-pool-wrap')) el('o-pool-wrap').style.display = (t === 'lotteryPoolTickets') ? '' : 'none';
    const scLabel = el('o-sellcount-label');
    if (scLabel) scLabel.textContent = virtual ? '单次购买发放数量（券 / 兑换币的张数）' : '单次购买发放数量（堆叠物品如 GP 币 / 卢布可 >1）';
}

function refreshMode() { return (el('o-refresh-mode') && el('o-refresh-mode').value) || 'inherit'; }
function applyRefreshModeUi() {
    if (el('o-refresh-custom-wrap')) el('o-refresh-custom-wrap').style.display = (refreshMode() === 'custom') ? '' : 'none';
}

if (el('o-reward-type')) el('o-reward-type').addEventListener('change', applyTypeUi);
if (el('o-refresh-mode')) el('o-refresh-mode').addEventListener('change', applyRefreshModeUi);

// ---- 货架列表 ----
async function loadOffers() {
    const r = await api('/shop');
    if (!r.success) { if (r.message) toast(r.message, false); return; }
    shopPools = r.pools || [];
    renderPoolOptions(el('o-pool') && el('o-pool').value);
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
        const typeLabel = { lotteryGlobalTickets: '通用抽奖券', lotteryPoolTickets: '限定抽奖券', lotteryExchangeCoins: '抽奖兑换币' }[o.rewardType] || '';
        const virtual = isVirtualType(o.rewardType);
        const kindStr = virtual ? `虚拟·${typeLabel}${o.rewardType === 'lotteryPoolTickets' && o.poolId ? '(' + esc(o.poolId) + ')' : ''}` : ('tpl ' + esc((o.tpl || '').slice(0, 8)) + '…');
        const refreshStr = (o.refreshSeconds === null || o.refreshSeconds === undefined) ? '继承全局刷新'
            : (o.refreshSeconds <= 0 ? '永不刷新' : `独立刷新 ${+(o.refreshSeconds / 3600).toFixed(2)}h`);
        const left = document.createElement('div');
        left.innerHTML = `<div class="offer-title">${(!virtual && isTpl(o.tpl)) ? `<img class="rw-icon" src="${ICON_API}${o.tpl}" alt="" onerror="this.remove()" />` : ''}${esc(o.name || o.id)}${qtyStr}</div>
            <div class="meta">${esc(o.id)} · ${kindStr} · 库存 ${stockStr} · ${limitStr} · ${refreshStr} · 价 ${esc(costStr)}</div>`;
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
    el('o-id').value = o.id; el('o-tpl').value = o.tpl || ''; el('o-name').value = o.name || '';
    el('o-sellcount').value = o.sellCount > 0 ? o.sellCount : 1;
    el('o-stock').value = o.stock ?? -1; el('o-buylimit').value = o.buyLimit ?? 0;
    el('o-reward-type').value = isVirtualType(o.rewardType) ? o.rewardType : 'item';
    renderPoolOptions(o.poolId || '');
    applyTypeUi();
    // 刷新模式回填：null=继承；0=永不；>0=自定义
    if (o.refreshSeconds === null || o.refreshSeconds === undefined) { el('o-refresh-mode').value = 'inherit'; }
    else if (o.refreshSeconds <= 0) { el('o-refresh-mode').value = 'never'; }
    else { el('o-refresh-mode').value = 'custom'; el('o-refresh-custom-hours').value = +(o.refreshSeconds / 3600).toFixed(2); }
    applyRefreshModeUi();
    offerCostEd.setCost(o.cost);
    el('o-tpl').dispatchEvent(new Event('input'));
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function clearOffer() {
    el('o-id').value = ''; el('o-tpl').value = ''; el('o-name').value = '';
    el('o-sellcount').value = 1; el('o-stock').value = -1; el('o-buylimit').value = 0; offerCostEd.clear();
    el('o-tpl-icon').style.display = 'none';
    el('o-reward-type').value = 'item'; applyTypeUi();
    el('o-refresh-mode').value = 'inherit'; el('o-refresh-custom-hours').value = 24; applyRefreshModeUi();
}
el('clear-offer').onclick = clearOffer;
el('refresh-offers').onclick = loadOffers;

if (el('save-refresh')) el('save-refresh').onclick = async () => {
    const hours = Math.max(0, +el('o-refresh-hours').value || 0);
    const seconds = Math.round(hours * 3600);
    if (isCollaborator()) {
        const r = await submitChange('shop', 'shop.refreshPeriod', { seconds });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/shop/refresh-period', 'POST', { seconds });
    toast(r.success ? (hours > 0 ? `已设刷新周期 ${hours} 小时` : '已关闭自动刷新') : (r.message || '失败'), r.success);
};

el('save-offer').onclick = async () => {
    const { cost, invalid } = offerCostEd.collect();
    if (invalid) return toast('价格项有误：物品需选有效物品、数量需为正整数', false);
    const type = rewardType();
    const virtual = isVirtualType(type);
    // 刷新模式 → refreshSeconds：inherit=null；never=0；custom=小时*3600
    let refreshSeconds = null;
    const mode = refreshMode();
    if (mode === 'never') refreshSeconds = 0;
    else if (mode === 'custom') refreshSeconds = Math.max(0, Math.round((+el('o-refresh-custom-hours').value || 0) * 3600));
    const offer = {
        id: el('o-id').value.trim(),
        tpl: virtual ? '' : el('o-tpl').value.trim(),
        name: el('o-name').value.trim() || null,
        sellCount: Math.max(1, +el('o-sellcount').value || 1),
        stock: +el('o-stock').value,
        buyLimit: +el('o-buylimit').value,
        rewardType: type,
        poolId: type === 'lotteryPoolTickets' ? (el('o-pool').value || '') : null,
        refreshSeconds,
        cost
    };
    if (!offer.id) return toast('请填写商品 ID', false);
    if (!virtual && !isTpl(offer.tpl)) return toast('商品 tpl 必须是 24 位十六进制 id', false);
    if (type === 'lotteryPoolTickets' && !offer.poolId) return toast('限定抽奖券必须选择绑定奖池', false);
    if (isCollaborator()) {
        const r = await submitChange('shop', 'shop.upsert', offer);
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/shop', 'POST', offer);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) { clearOffer(); loadOffers(); }
};
async function delOffer(id) {
    if (!confirm('删除商品 ' + id + ' ?')) return;
    if (isCollaborator()) {
        const r = await submitChange('shop', 'shop.delete', { id });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/shop', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success); loadOffers();
}

function applyPendingShopEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    const payload = change.proposedPayload || {};
    if (change.commandType === 'shop.upsert') fillOffer(payload);
    else if (change.commandType === 'shop.refreshPeriod') el('o-refresh-hours').value = Math.max(0, +(payload.seconds || 0) / 3600);
    else if (change.commandType === 'shop.delete') toast('请选择新的商品并点击删除，以更新原删除审核单', true);
}

el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;

// 入口：先尝试建立管理会话（含协管 #bpsso= 免密落地），有会话则进控制台，否则显示登录卡/协管 gate
bootstrapAdminPage({ moduleCap: 'shop.read', onReady: edit => { ADMIN_TOKEN = getAdminToken(); enterConsole(); applyPendingShopEdit(edit); } });
