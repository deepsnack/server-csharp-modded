'use strict';

// 通行证商人管理页：复用通行证后台 admin token / 登录 / Portal SSO。
// REGISTER_ADMIN_LOGIN / getAdminToken / isCollaborator / ensureAdminSession / submitChange 由 auth.js 提供（勿重复声明 const）。
const ADMIN_API = '/battlepass/api/admin';
const ICON_API = '/battlepass/api/icons/';
const TRADER_ID = '66f1b2c3d4e5a6b7c8d90011'; // 与 BattlePassTraderSync.TraderIdHex 一致
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

function el(id) { return document.getElementById(id); }
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }

function setResupplySeconds(seconds) {
    const total = Number.isFinite(+seconds) && +seconds >= 60 ? Math.floor(+seconds) : 3600;
    const units = [86400, 3600, 60];
    const unit = units.find(candidate => total % candidate === 0) || 1;
    el('m-resupply-value').value = total / unit;
    el('m-resupply-unit').value = String(unit);
}

function getResupplySeconds() {
    const value = Number(el('m-resupply-value').value);
    const unit = Number(el('m-resupply-unit').value);
    return Number.isInteger(value) && value > 0 ? value * unit : 0;
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() };
    const res = await fetch(ADMIN_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// ---- 登录（密码登录复用 auth.js 的 adminLogin，含会话交换）----
async function doAdminLogin() {
    const password = el('admin-pass').value;
    const r = await adminLogin(password);
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    ADMIN_TOKEN = getAdminToken();
    setActorType(r.actorType || 'admin');
    enterConsole();
}
function logout() {
    ADMIN_TOKEN = ''; clearAdminToken(); clearActorType();
    const b = el('collab-banner'); if (b) b.remove();
    el('trader-view').classList.add('hidden'); el('login-view').classList.remove('hidden');
}
let offerCostEd = null; // 统一价格编辑器（BpPrice）
function enterConsole() {
    el('login-view').classList.add('hidden');
    el('trader-view').classList.remove('hidden');
    BpPrice.fillCurrencySelect(el('m-currency'));
    if (!offerCostEd) offerCostEd = BpPrice.createEditor(el('o-cost-editor'));
    if (isCollaborator()) applyCollaboratorUi();
    loadMeta(); loadOffers();
}

// 协管态：商人元信息/头像属商人级配置（仅管理员），协管只能提交货架商品增删改。
// 隐藏「商人设置」标签及其面板，默认停在「货架」标签，并显示提示条。
function applyCollaboratorUi() {
    showCollaboratorBanner();
    // 隐藏商人设置标签 + 面板（其保存端点未对协管放行写）
    document.querySelectorAll('.tab[data-tab]').forEach(t => {
        if (t.dataset.tab === 'meta') t.style.display = 'none';
    });
    const metaPane = el('pane-meta'); if (metaPane) metaPane.classList.add('hidden');
    // 默认切到货架标签
    const offersTab = document.querySelector('.tab[data-tab="offers"]');
    if (offersTab) offersTab.click();
}

function showCollaboratorBanner() {
    if (el('collab-banner')) return;
    const bar = document.createElement('div');
    bar.id = 'collab-banner';
    bar.className = 'hint';
    bar.style.cssText = 'margin:10px 16px;padding:10px 14px;border-left:4px solid #e0a030;background:rgba(224,160,48,.12);font-weight:600;';
    bar.textContent = '协管模式：货架商品的修改将提交管理员审核后生效；商人名称/头像等设置仅管理员可用。';
    const view = el('trader-view');
    const topbar = view.querySelector('.topbar');
    if (topbar && topbar.nextSibling) view.insertBefore(bar, topbar.nextSibling);
    else view.insertBefore(bar, view.firstChild);
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
    BpPrice.fillCurrencySelect(el('m-currency'), c.currency || 'RUB');
    setResupplySeconds(c.resupplySeconds ?? 3600);
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
    if (isCollaborator()) return toast('商人设置仅管理员可用', false);
    const resupplySeconds = getResupplySeconds();
    if (!Number.isSafeInteger(resupplySeconds) || resupplySeconds < 60 || resupplySeconds > 2147483647) {
        return toast('补货周期需为至少 60 秒的整数', false);
    }

    const cfg = {
        name: el('m-name').value.trim(),
        nickname: el('m-nickname').value.trim(),
        surname: el('m-surname').value.trim() || null,
        description: el('m-desc').value.trim(),
        location: el('m-location').value.trim(),
        currency: el('m-currency').value,
        resupplySeconds,
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
    if (isCollaborator()) return toast('商人头像仅管理员可用', false);
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
// 价格/以物易物统一由 BpPrice 价格编辑器处理（offerCostEd），不再手填 tpl。

el('o-tpl').addEventListener('input', () => {
    const tpl = el('o-tpl').value.trim();
    const icon = el('o-tpl-icon');
    if (isTpl(tpl)) { icon.src = ICON_API + tpl; icon.style.display = 'inline-block'; icon.onerror = () => icon.style.display = 'none'; }
    else { icon.style.display = 'none'; }
});

// 商品图形化搜索（统一组件 BpPicker，走统一查询接口，兼容 mod 物品）：
// 选中后回填 tpl、显示图标；展示名为空时顺带回填物品名。
BpPicker.attachItem(el('o-tpl'), el('o-tpl-results'), ds => {
    el('o-tpl').value = ds.tpl;
    const icon = el('o-tpl-icon');
    icon.src = ICON_API + ds.tpl; icon.style.display = 'inline-block';
    icon.onerror = () => icon.style.display = 'none';
    if (!el('o-name').value.trim()) el('o-name').value = ds.name || '';
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
    offerCostEd.setCost(o.cost);
    el('o-tpl').dispatchEvent(new Event('input'));
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function clearOffer() {
    el('o-id').value = ''; el('o-tpl').value = ''; el('o-name').value = '';
    el('o-stock').value = -1; el('o-buylimit').value = 0; offerCostEd.clear();
    el('o-tpl-icon').style.display = 'none';
}
el('clear-offer').onclick = clearOffer;
el('refresh-offers').onclick = loadOffers;

el('save-offer').onclick = async () => {
    const { cost, invalid } = offerCostEd.collect();
    if (invalid) return toast('价格项有误：物品需选有效物品、数量需为正整数', false);
    const offer = {
        id: el('o-id').value.trim(),
        tpl: el('o-tpl').value.trim(),
        name: el('o-name').value.trim() || null,
        stock: +el('o-stock').value,
        buyLimit: +el('o-buylimit').value,
        cost
    };
    if (!offer.id) return toast('请填写货架项 ID', false);
    if (!isTpl(offer.tpl)) return toast('商品 tpl 必须是 24 位十六进制 id', false);
    if (isCollaborator()) {
        const r = await submitChange('trader', 'trader.offer.upsert', offer);
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/offers', 'POST', offer);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) { clearOffer(); loadOffers(); }
};
async function delOffer(id) {
    if (!confirm('删除货架项 ' + id + ' ?')) return;
    if (isCollaborator()) {
        const r = await submitChange('trader', 'trader.offer.delete', { id });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/offers', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success); loadOffers();
}

function applyPendingTraderEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    if (change.commandType === 'trader.offer.upsert') fillOffer(change.proposedPayload || {});
    else if (change.commandType === 'trader.offer.delete') toast('请选择新的货架项并点击删除，以更新原删除审核单', true);
}

el('admin-login-btn').onclick = doAdminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doAdminLogin(); });
el('admin-logout').onclick = logout;

// 入口：协管从玩家页带 #bpsso= 免密落地换会话；管理员用已存会话或密码登录。
bootstrapAdminPage({ moduleCap: 'trader.read', onReady: edit => { ADMIN_TOKEN = getAdminToken(); enterConsole(); applyPendingTraderEdit(edit); } });
