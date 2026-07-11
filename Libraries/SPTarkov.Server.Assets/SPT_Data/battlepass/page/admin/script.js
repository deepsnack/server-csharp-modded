'use strict';

const ADMIN_API = '/battlepass/api/admin';
const LOTTERY_ADMIN_API = '/battlepass/api/admin/lottery';
// REGISTER_ADMIN_LOGIN / token 存取 / Portal SSO 统一由 auth.js 提供。
let ADMIN_TOKEN = getAdminToken();
let lotteryPoolsForCodes = [];

function el(id) { return document.getElementById(id); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() };
    const res = await fetch(ADMIN_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// ---- 登录：密码登录复用 auth.js 的 adminLogin ----
async function doAdminPasswordLogin() {
    const password = el('admin-pass').value;
    const r = await adminLogin(password);
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    ADMIN_TOKEN = getAdminToken();
    setActorType(r.actorType || 'admin');
    enterConsole();
}

function logout() {
    clearAdminToken(); clearActorType(); ADMIN_TOKEN = '';
    el('admin-view').classList.add('hidden'); el('login-view').classList.remove('hidden');
}

// 协管态：按 capabilities 动态显示可提交模块入口；隐藏管理员专属功能。
// 外链模块 tab 与对应 *.read 能力的映射（这些模块页内已做协管分流）。
const COLLAB_MODULE_TABS = [
    ['tasks.html', 'tasks.read'],
    ['trader.html', 'trader.read'],
    ['shop.html', 'shop.read'],
    ['lottery.html', 'lottery.read'],
    ['recipes.html', 'recipes.read'],
    ['items.html', 'items.read'],
    ['quests.html', 'quests.read'],
    ['flea.html', 'flea.read'],
];

function applyCollaboratorUi() {
    if (!isCollaborator()) return;
    // 管理员专属分页标签：赛季 / 激活码 / 玩家 一律隐藏（协管无对应读写能力）。
    ['season', 'codes', 'players'].forEach(key => {
        document.querySelectorAll('.tab[data-tab="' + key + '"]').forEach(t => t.style.display = 'none');
    });
    // 奖励轨：有 tracks.read 才显示。
    document.querySelectorAll('.tab[data-tab="tracks"]').forEach(t => {
        t.style.display = hasCapability('tracks.read') ? '' : 'none';
    });
    // 外链业务模块：按各自 *.read 能力显示 / 隐藏。
    COLLAB_MODULE_TABS.forEach(([href, cap]) => {
        const a = document.querySelector('.tab[href="' + href + '"]');
        if (a) a.style.display = hasCapability(cap) ? '' : 'none';
    });
    // 管理员专属外链：称号 / 审核 / 协管授权 一律隐藏。
    ['titles.html'].forEach(href => {
        const a = document.querySelector('.tab[href="' + href + '"]');
        if (a) a.style.display = 'none';
    });
    const navReviews = el('nav-reviews'); if (navReviews) navReviews.style.display = 'none';
    const navAudit = el('nav-audit'); if (navAudit) navAudit.style.display = 'none';
    const navAccess = el('nav-access'); if (navAccess) navAccess.style.display = 'none';
    // 我的提交（nav-mychanges）保留给协管。
    // 顶部提示条
    const topbar = document.querySelector('.topbar');
    if (topbar && !el('collab-hint-bar')) {
        const bar = document.createElement('div');
        bar.id = 'collab-hint-bar';
        bar.className = 'hint';
        bar.style.cssText = 'width:100%;margin:0;padding:6px 14px;background:rgba(200,150,40,.12);border-bottom:1px solid rgba(200,150,40,.4);color:#e8c268;font-size:12px';
        bar.textContent = '协管模式：你的修改将提交管理员审核后生效；赛季 / 激活码 / 玩家 / 称号 / 审核 等操作仅管理员可用。';
        topbar.parentNode.insertBefore(bar, topbar.nextSibling);
    }
    // 默认切到第一个可用分页：优先奖励轨，否则不强制切换（外链模块由用户点击进入）。
    if (hasCapability('tracks.read')) {
        const tracksTab = document.querySelector('.tab[data-tab="tracks"]');
        if (tracksTab) tracksTab.click();
    }
}

async function enterConsole() {
    el('login-view').classList.add('hidden');
    el('admin-view').classList.remove('hidden');
    if (isCollaborator()) {
        // 协管只加载可提交的奖励轨；季/码/玩家读接口对协管未放行，跳过以免 401 噪音。
        applyCollaboratorUi();
        if (hasCapability('tracks.read')) {
            await loadLotteryPoolsForCodes();
            await loadTracks();
        }
        return;
    }
    await Promise.all([loadSeason(), loadCodes(), loadPlayers(), (async () => { await loadLotteryPoolsForCodes(); await loadTracks(); })()]);
}

function applyPendingTracksEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    if (change.commandType !== 'tracks.save') return;
    const proposed = deepClone(change.proposedPayload || {});
    Object.keys(proposed).forEach(level => { allTracks[level] = proposed[level]; });
    syncTracksJson();
    resetAndRender();
    buildRewardOverview();
}

// ---- 标签 ----
document.querySelectorAll('.tab').forEach(tab => {
    tab.onclick = () => {
        document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
        tab.classList.add('active');
        document.querySelectorAll('.tab-pane').forEach(p => p.classList.add('hidden'));
        el('pane-' + tab.dataset.tab).classList.remove('hidden');
    };
});

// ---- 赛季 ----
function toLocalDatetimeStr(unixSec) {
    if (!unixSec || unixSec <= 0) return '';
    return new Date(unixSec * 1000).toISOString().slice(0, 16);
}
function toUnixSec(datetimeLocalStr) {
    if (!datetimeLocalStr) return 0;
    return Math.floor(new Date(datetimeLocalStr).getTime() / 1000);
}
let loadedSeason = {}; // 最近一次从服务端读到的赛季，用于保留表单未覆盖的字段（如 cycleXp）
function seasonFormToJson() {
    const curveRaw = el('s-curve').value.trim();
    const xpCurve = curveRaw ? curveRaw.split(',').map(s => +s.trim()).filter(n => n > 0) : [];
    // 以 loadedSeason 为底，表单字段覆盖其上——避免在赛季页保存时丢掉 cycleXp 等不在本表单的字段
    return {
        ...loadedSeason,
        seasonId: el('s-id').value.trim() || 'S1',
        name: el('s-name').value.trim() || 'Season 1',
        startUtc: toUnixSec(el('s-start').value),
        endUtc: toUnixSec(el('s-end').value),
        maxLevel: +el('s-maxlevel').value || 50,
        baseXp: +el('s-basexp').value || 1000,
        xpGrowthPerLevel: +el('s-growth').value || 100,
        premiumXpMultiplier: +el('s-mult').value || 1.0,
        xpCurve: xpCurve.length ? xpCurve : [],
    };
}
function fillSeasonForm(s) {
    el('s-id').value = s.seasonId || 'S1';
    el('s-name').value = s.name || '';
    el('s-start').value = toLocalDatetimeStr(s.startUtc);
    el('s-end').value = toLocalDatetimeStr(s.endUtc);
    el('s-maxlevel').value = s.maxLevel ?? 50;
    el('s-basexp').value = s.baseXp ?? 1000;
    el('s-growth').value = s.xpGrowthPerLevel ?? 100;
    el('s-mult').value = s.premiumXpMultiplier ?? 1.0;
    el('s-curve').value = (s.xpCurve || []).join(', ');
    el('season-json').value = JSON.stringify(s, null, 2);
}
// 表单字段变化时同步 JSON 预览
['s-id','s-name','s-start','s-end','s-maxlevel','s-basexp','s-growth','s-mult','s-curve'].forEach(id => {
    el(id).addEventListener('input', () => {
        el('season-json').value = JSON.stringify(seasonFormToJson(), null, 2);
    });
});
async function loadSeason() {
    const r = await api('/season');
    if (r.success) { loadedSeason = r.season || {}; fillSeasonForm(r.season); }
}
el('save-season').onclick = async () => {
    if (isCollaborator()) return toast('赛季设置仅管理员可用', false);
    let payload;
    // 如果 JSON 细节展开，优先用 JSON 直改内容；否则用表单
    if (el('season-json-details').open) {
        try { payload = JSON.parse(el('season-json').value); } catch (e) { return toast('JSON 格式错误', false); }
    } else {
        payload = seasonFormToJson();
    }
    const r = await api('/season', 'POST', payload);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) fillSeasonForm(payload);
};

// ---- 奖励轨（全等级滚动 · 分步懒加载 · 满级循环奖励）----
const ICON_API = '/battlepass/api/icons/';
let allTracks = {};        // { "1":{free,premium}, ..., "0":{...}=循环奖励（满级后逐轮可领） }
let tracksBaseline = {};   // 载入时的服务端快照；协管提交差量用（仅提交与此不同的等级）
const CYCLE_KEY = '0';
const RENDER_CHUNK = 8;
let trackMaxLevel = 50;
let renderedUpTo = 0;
let cycleRendered = false;
let trackObserver = null;

function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }
function deepClone(v) { try { return JSON.parse(JSON.stringify(v)); } catch { return v; } }

// 顶部工具条：改等级上限 → 重渲染（先回写已编辑内容）
el('track-maxlevel').addEventListener('change', () => {
    const v = Math.max(1, Math.min(500, +el('track-maxlevel').value || 1));
    trackMaxLevel = v; el('track-maxlevel').value = v;
    collectAllFromDom();
    resetAndRender();
});
// 跳转按钮
el('jump-top').onclick = () => window.scrollTo({ top: 0, behavior: 'smooth' });
el('jump-bottom').onclick = () => { while (!cycleRendered) renderNextChunk(); window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' }); };

// 保存
el('save-all-tracks').onclick = () => { collectAllFromDom(); postTracksAndSeason(allTracks); };
el('save-json-tracks').onclick = () => {
    let payload; try { payload = JSON.parse(el('tracks-json').value); } catch (e) { return toast('JSON 格式错误', false); }
    postTracksAndSeason(payload);
};

// 协管差量：只挑出与载入快照 tracksBaseline 不同的等级（含被清空的等级）。
// 比较前用与 RewardEditor.collect 一致的规范化，避免字段顺序/空值差异造成的假变更。
const RW_TOKEN_TYPES = ['lotteryGlobalTickets', 'lotteryPoolTickets', 'lotteryExchangeCoins'];
function canonReward(rw) {
    rw = rw || {};
    const type = rw.type || 'item';
    const isToken = RW_TOKEN_TYPES.includes(type);
    const tpl = (rw.tpl || '').trim();
    return {
        type,
        tpl: type === 'item' ? tpl : (tpl || null),
        count: (type === 'item' || isToken) ? Math.max(1, +rw.count || 1) : 1,
        name: (rw.name || '').trim() || null,
        featured: !!rw.featured,
        foundInRaid: type === 'item' ? !!rw.foundInRaid : false,
        offerId: type === 'purchaseRight' ? (rw.offerId || null) : null,
        recipeId: type === 'recipe' ? (rw.recipeId || null) : null,
        titleId: type === 'title' ? (rw.titleId || null) : null,
        suitId: type === 'clothing' ? (rw.suitId || null) : null,
        poolId: type === 'lotteryPoolTickets' ? (rw.poolId || null) : null,
    };
}
function canonLevelStr(lv) {
    lv = lv || {};
    return JSON.stringify({
        free: (lv.free || []).map(canonReward),
        premium: (lv.premium || []).map(canonReward),
    });
}
// 仅返回内容变化的等级；current 有而 baseline 无=新增，被清空=保留空数组，未动=剔除。
function diffTracks(current, baseline) {
    const diff = {};
    for (const key of Object.keys(current || {})) {
        if (canonLevelStr(current[key]) !== canonLevelStr((baseline || {})[key])) diff[key] = current[key];
    }
    return diff;
}

async function postTracksAndSeason(tracksPayload) {
    // 协管：奖励轨改走审核队列（tracks.save），且只提交差量等级——审核后台按等级合并，非全量替换。
    // 等级上限/循环经验属赛季配置，仅管理员可改，协管提交仅含奖励轨本身。
    if (isCollaborator()) {
        const diff = diffTracks(tracksPayload, tracksBaseline);
        const changed = Object.keys(diff).length;
        if (changed === 0) { toast('奖励轨没有改动，无需提交', false); return false; }
        const sr = await submitChange('tracks', 'tracks.save', diff);
        if (sr.success) { toast(`已提交 ${changed} 个等级的改动，等待管理员批准`, true); return true; }
        toast(sr.message || '提交失败', false); return false;
    }
    const r = await api('/tracks', 'POST', tracksPayload);
    if (!r.success) { toast(r.message || '保存失败', false); return false; }
    // 等级上限 + 循环每轮经验同步进赛季配置（单一数据源）
    try {
        const sr = await api('/season');
        if (sr.success) {
            const season = sr.season;
            season.maxLevel = trackMaxLevel;
            season.cycleXp = Math.max(0, +el('track-cyclexp').value || 0);
            await api('/season', 'POST', season);
        }
    } catch (e) { /* 上限同步失败不影响奖励保存 */ }
    toast('已保存全部', true);
    await loadTracks();
    return true;
}

// 采集所有已渲染等级块回写模型（未渲染等级保留原值）
function collectAllFromDom() {
    rwInvalidCount = 0;
    document.querySelectorAll('#tracks-list .level-block').forEach(b => {
        allTracks[b.dataset.level] = { free: collectRewardsIn(b, 'free'), premium: collectRewardsIn(b, 'premium') };
    });
    if (rwInvalidCount > 0) toast(`有 ${rwInvalidCount} 个奖励缺关键字段（已标红），保存后请补全`, false);
    syncTracksJson();
}

// 按类型收集与校验：item 需要 tpl；purchaseRight/recipe/title/clothing 需要对应引用 id（tpl 可空）。
// 收集委托给共享组件 RewardEditor.collect；缺关键字段的卡保留并标红，invalid 累加到 rwInvalidCount 供保存时提示。
function collectRewardsIn(block, track) {
    const box = block.querySelector(`.reward-cards[data-track="${track}"]`);
    if (!box) return [];
    const { rewards, invalid } = RewardEditor.collect(box);
    rwInvalidCount += invalid;
    return rewards;
}

// 本轮收集中缺关键字段的卡数（collectAllFromDom 前清零，保存时提示）
let rwInvalidCount = 0;

function syncTracksJson() { el('tracks-json').value = JSON.stringify(allTracks, null, 2); }

function updateTracksCountInfo() {
    const configured = Object.keys(allTracks).filter(k =>
        k !== CYCLE_KEY && +k >= 1 && ((allTracks[k].free || []).length || (allTracks[k].premium || []).length)).length;
    el('tracks-count-info').textContent = `已加载 ${Math.min(renderedUpTo, trackMaxLevel)}/${trackMaxLevel} 级 · 有奖励 ${configured} 级`;
}

// ---- 等级块渲染 ----
function levelBlockHtml(level, isCycle, data) {
    const title = isCycle ? '♾️ 循环奖励（满级后每轮 · 逐轮可领）' : `Lv ${level}`;
    const copyBtn = (!isCycle && level > 1) ? `<button class="mini ghost copy-prev-btn" title="从上一级复制">⧉ 复制 Lv${level - 1}</button>` : '';
    const freeCount = (data?.free || []).length;
    const premiumCount = (data?.premium || []).length;
    return `<details class="level-block${isCycle ? ' cycle-block' : ''}" data-level="${isCycle ? CYCLE_KEY : level}">
        <summary class="level-head" aria-expanded="false">
            <span class="level-toggle-main">
                <span class="lv-title">${title}</span>
                <span class="level-reward-summary">免费 ${freeCount} · 付费 ${premiumCount}</span>
            </span>
            ${copyBtn}
            <span class="level-chevron" aria-hidden="true">⌄</span>
        </summary>
        <div class="level-details">
            <div class="track-dual">
                <div class="panel track-col">
                    <div class="panel-head"><h2>🎁 免费</h2><button class="mini add-rw-btn" data-track="free">＋ 添加</button></div>
                    <div class="reward-cards" data-track="free"></div>
                </div>
                <div class="panel track-col">
                    <div class="panel-head"><h2>⭐ 付费</h2><button class="mini add-rw-btn" data-track="premium">＋ 添加</button></div>
                    <div class="reward-cards" data-track="premium"></div>
                </div>
            </div>
        </div>
    </details>`;
}

function renderLevelBlock(level, isCycle) {
    const key = isCycle ? CYCLE_KEY : String(level);
    const data = allTracks[key] || { free: [], premium: [] };
    const wrap = document.createElement('div');
    wrap.innerHTML = levelBlockHtml(level, isCycle, data);
    const block = wrap.firstElementChild;
    el('tracks-list').appendChild(block);
    fillRewardCards(block, 'free', data.free || []);
    fillRewardCards(block, 'premium', data.premium || []);
    wireBlock(block);
}

// 渲染委托给共享组件 RewardEditor.fill（奖池用 lotteryPoolsForCodes）。
function fillRewardCards(block, track, items) {
    const box = block.querySelector(`.reward-cards[data-track="${track}"]`);
    if (box) RewardEditor.fill(box, items, lotteryPoolsForCodes);
}

function wireBlock(block) {
    const summary = block.querySelector('.level-head');
    block.addEventListener('toggle', () => {
        summary?.setAttribute('aria-expanded', String(block.open));
        if (!block.open) updateLevelSummary(block);
    });
    block.querySelectorAll('.add-rw-btn').forEach(btn => btn.onclick = () => {
        const track = btn.dataset.track;
        const lvl = block.dataset.level;
        allTracks[lvl] = { free: collectRewardsIn(block, 'free'), premium: collectRewardsIn(block, 'premium') };
        allTracks[lvl][track].push({ tpl: '', count: 1, name: null, featured: false, type: 'item' });
        fillRewardCards(block, track, allTracks[lvl][track]);
        updateLevelSummary(block);
        syncTracksJson();
    });
    const copyBtn = block.querySelector('.copy-prev-btn');
    if (copyBtn) copyBtn.onclick = event => {
        event.preventDefault();
        event.stopPropagation();
        const level = +block.dataset.level;
        const prev = allTracks[String(level - 1)];
        if (!prev) return toast('上一级无数据', false);
        allTracks[String(level)] = { free: deepClone(prev.free || []), premium: deepClone(prev.premium || []) };
        fillRewardCards(block, 'free', allTracks[String(level)].free);
        fillRewardCards(block, 'premium', allTracks[String(level)].premium);
        updateLevelSummary(block);
        syncTracksJson();
        toast('已从 Lv' + (level - 1) + ' 复制', true);
    };
    block.addEventListener('click', event => {
        if (event.target.closest('.rw-del')) requestAnimationFrame(() => updateLevelSummary(block));
    });
}

function updateLevelSummary(block) {
    const freeCount = block.querySelectorAll('.reward-cards[data-track="free"] > .rw-card').length;
    const premiumCount = block.querySelectorAll('.reward-cards[data-track="premium"] > .rw-card').length;
    const summary = block.querySelector('.level-reward-summary');
    if (summary) summary.textContent = `免费 ${freeCount} · 付费 ${premiumCount}`;
}

// 奖励卡渲染/选择器/类型切换已迁到共享组件 reward-editor.js（RewardEditor）。

// ---- 奖励轨批量追加 ----
function resetBulkRewardEditor() {
    const host = el('bulk-reward-editor');
    fillRewardCards(host, 'bulk', [{ tpl: '', count: 1, name: null, featured: false, type: 'item' }]);
    el('bulk-reward-end').value = String(trackMaxLevel);
    el('bulk-reward-hint').textContent = '配置一种奖励后，将按等级范围追加并立即保存。';
}

async function applyBulkReward() {
    collectAllFromDom();
    const start = Math.max(1, Math.min(trackMaxLevel, +el('bulk-reward-start').value || 1));
    const end = Math.max(start, Math.min(trackMaxLevel, +el('bulk-reward-end').value || trackMaxLevel));
    const step = Math.max(1, +el('bulk-reward-step').value || 1);
    const track = el('bulk-reward-track').value;

    rwInvalidCount = 0;
    const rewards = collectRewardsIn(el('bulk-reward-editor'), 'bulk');
    if (rewards.length !== 1 || rwInvalidCount > 0) {
        toast('请完整配置要批量添加的奖励', false);
        return;
    }

    const levels = [];
    for (let level = start; level <= end; level += step) levels.push(level);
    const trackLabel = track === 'both' ? '免费轨与付费轨' : (track === 'premium' ? '付费轨' : '免费轨');
    if (!confirm(`确认向 ${levels.length} 个等级的${trackLabel}追加该奖励并立即保存？\n\n等级：${start}–${end}，间隔 ${step}。已有奖励不会被覆盖。`)) {
        return;
    }

    const button = el('bulk-reward-apply');
    button.disabled = true;
    try {
        for (const level of levels) {
            const key = String(level);
            allTracks[key] ||= { free: [], premium: [] };
            allTracks[key].free ||= [];
            allTracks[key].premium ||= [];
            if (track === 'free' || track === 'both') allTracks[key].free.push(deepClone(rewards[0]));
            if (track === 'premium' || track === 'both') allTracks[key].premium.push(deepClone(rewards[0]));
        }
        syncTracksJson();
        el('bulk-reward-hint').textContent = `正在保存：${levels.length} 个等级 · ${trackLabel}`;
        const saved = await postTracksAndSeason(allTracks);
        el('bulk-reward-hint').textContent = saved
            ? `已追加到 ${levels.length} 个等级的${trackLabel}`
            : '保存失败，当前页面仍保留批量修改，可再次保存';
    } finally {
        button.disabled = false;
    }
}

el('bulk-reward-reset').onclick = resetBulkRewardEditor;
el('bulk-reward-apply').onclick = applyBulkReward;

// ---- 奖励总览 · 搜索 / 筛选 / 批量移除 ----
const OV_TYPE_LABEL = { item: '物品', purchaseRight: '购买权', recipe: '配方', title: '称号', clothing: '服装', lotteryGlobalTickets: '通用券', lotteryPoolTickets: '限定券', lotteryExchangeCoins: '兑换币' };
function rewardKeyText(r) { return [r.name, r.tpl, r.offerId, r.recipeId, r.titleId, r.suitId, r.poolId].filter(Boolean).join(' '); }
function rewardLabel(r) { return r.name || r.tpl || r.offerId || r.recipeId || r.titleId || r.suitId || r.poolId || '(空)'; }

// 从 allTracks 汇总匹配当前筛选的奖励（含循环键 '0'）。返回 {key,lvl,track,idx,r}[]。
function overviewFilter() {
    const q = el('ov-q').value.trim().toLowerCase();
    const type = el('ov-type').value;
    const track = el('ov-track').value;
    const from = el('ov-from').value.trim();
    const to = el('ov-to').value.trim();
    const lo = from === '' ? -Infinity : +from;
    const hi = to === '' ? Infinity : +to;
    const rows = [];
    Object.keys(allTracks).forEach(key => {
        if (!/^\d+$/.test(key)) return; // 只处理数字等级键（'0'=循环）
        const lvl = +key;
        if (lvl < lo || lvl > hi) return;
        ['free', 'premium'].forEach(tk => {
            if (track && track !== tk) return;
            (allTracks[key]?.[tk] || []).forEach((r, idx) => {
                if (type && (r.type || 'item') !== type) return;
                if (q && !rewardKeyText(r).toLowerCase().includes(q)) return;
                rows.push({ key, lvl, track: tk, idx, r });
            });
        });
    });
    return rows;
}

function buildRewardOverview() {
    if (!el('ov-list')) return;
    const rows = overviewFilter();
    el('ov-count').textContent = `匹配 ${rows.length} 项`;
    if (!rows.length) { el('ov-list').innerHTML = '<div class="muted" style="font-size:11px">无匹配奖励</div>'; return; }
    el('ov-list').innerHTML = '<table class="grid"><tr><th>等级</th><th>轨</th><th>类型</th><th>奖励</th><th>数量</th><th>大奖</th></tr>' +
        rows.map(x => `<tr><td>${x.lvl === 0 ? '♾️循环' : x.lvl}</td><td>${x.track === 'free' ? '免费' : '付费'}</td>` +
            `<td>${esc(OV_TYPE_LABEL[x.r.type || 'item'] || x.r.type)}</td><td>${esc(rewardLabel(x.r))}</td>` +
            `<td>${x.r.count || 1}</td><td>${x.r.featured ? '★' : ''}</td></tr>`).join('') + '</table>';
}

async function batchRemoveRewards() {
    collectAllFromDom();
    const rows = overviewFilter();
    if (!rows.length) { toast('没有匹配的奖励可移除', false); return; }
    if (!confirm(`确认按当前筛选移除 ${rows.length} 项奖励并立即保存？此操作不可撤销。`)) return;
    // 按 (key,track) 分组后倒序 splice，避免删除时索引错位
    const byGroup = {};
    rows.forEach(x => { (byGroup[x.key + '|' + x.track] ||= []).push(x.idx); });
    Object.entries(byGroup).forEach(([g, idxs]) => {
        const sep = g.lastIndexOf('|');
        const key = g.slice(0, sep), track = g.slice(sep + 1);
        idxs.sort((a, b) => b - a).forEach(i => allTracks[key][track].splice(i, 1));
    });
    syncTracksJson();
    const saved = await postTracksAndSeason(allTracks); // 成功会 loadTracks→resetAndRender→buildRewardOverview
    toast(saved ? `已移除 ${rows.length} 项并保存` : '已移除但保存失败，可点「保存全部」重试', saved);
    buildRewardOverview();
}

el('ov-refresh').onclick = () => { collectAllFromDom(); buildRewardOverview(); };
el('ov-remove').onclick = batchRemoveRewards;
['ov-q', 'ov-type', 'ov-track', 'ov-from', 'ov-to'].forEach(id => el(id).addEventListener('input', buildRewardOverview));

// ---- 分步懒加载 ----
function renderNextChunk() {
    if (renderedUpTo < trackMaxLevel) {
        const end = Math.min(trackMaxLevel, renderedUpTo + RENDER_CHUNK);
        for (let lv = renderedUpTo + 1; lv <= end; lv++) renderLevelBlock(lv, false);
        renderedUpTo = end;
    }
    if (renderedUpTo >= trackMaxLevel && !cycleRendered) {
        renderLevelBlock(0, true); // 循环奖励块（列表最底部）
        cycleRendered = true;
        if (trackObserver) trackObserver.disconnect();
    }
    updateTracksCountInfo();
}

function resetAndRender() {
    el('tracks-list').innerHTML = '';
    renderedUpTo = 0; cycleRendered = false;
    if (trackObserver) trackObserver.disconnect();
    renderNextChunk();
    trackObserver = new IntersectionObserver(
        entries => { if (entries.some(e => e.isIntersecting)) renderNextChunk(); },
        { rootMargin: '300px' }
    );
    trackObserver.observe(el('tracks-sentinel'));
}

async function loadTracks() {
    const r = await api('/tracks');
    allTracks = (r.success && r.tracks) ? r.tracks : {};
    tracksBaseline = deepClone(allTracks);
    try {
        const sr = await api('/season');
        if (sr.success) {
            trackMaxLevel = sr.season.maxLevel || 50;
            el('track-maxlevel').value = trackMaxLevel;
            el('track-cyclexp').value = sr.season.cycleXp || 0;
        }
    } catch (e) { /* 用默认上限 */ }
    syncTracksJson();
    resetAndRender();
    if (!el('bulk-reward-editor').querySelector('.rw-card')) resetBulkRewardEditor();
    buildRewardOverview();
}

// ---- 激活码 ----
function lotteryPoolSelectOptions(selected) {
    return RewardEditor.poolOptions(lotteryPoolsForCodes, selected);
}

function refreshLotteryPoolSelect(select) {
    if (!select) return;
    const current = select.value || select.dataset.currentPool || '';
    select.innerHTML = lotteryPoolSelectOptions(current);
    if (current) select.value = current;
}

function refreshRewardPoolSelects() {
    document.querySelectorAll('select.rw-poolid').forEach(refreshLotteryPoolSelect);
}

function renderCodePoolOptions() {
    const select = el('c-pool');
    refreshLotteryPoolSelect(select);
    updateCodePoolVisibility();
}

function updateCodePoolVisibility() {
    const type = el('c-type').value;
    el('c-pool').disabled = type !== 'lotteryPoolTickets';
    const isRewards = type === 'rewards';
    el('c-rewards-wrap').style.display = isRewards ? '' : 'none';
    const vw = el('c-value-wrap'); if (vw) vw.style.display = isRewards ? 'none' : '';
    if (isRewards && !el('c-reward-editor').querySelector('.rw-card')) {
        RewardEditor.addCard(el('c-reward-editor'), null, lotteryPoolsForCodes);
    }
}

async function loadLotteryPoolsForCodes() {
    try {
        const res = await fetch(LOTTERY_ADMIN_API + '/pools', { headers: { 'X-Admin-Token': ADMIN_TOKEN } });
        const r = await res.json();
        if (!r.success) return;
        lotteryPoolsForCodes = r.pools || [];
        renderCodePoolOptions();
        refreshRewardPoolSelects();
    } catch (e) {
        // 抽奖模块未加载不影响主后台其他功能。
    }
}

el('gen-codes').onclick = async () => {
    if (isCollaborator()) return toast('激活码生成仅管理员可用', false);
    const type = el('c-type').value;
    if (type === 'lotteryPoolTickets' && !el('c-pool').value) {
        toast('请选择限定抽奖券绑定的奖池', false);
        return;
    }

    let rewards = null;
    if (type === 'rewards') {
        const res = RewardEditor.collect(el('c-reward-editor'));
        if (!res.rewards.length || res.invalid > 0) { toast('请完整配置激活码奖励（缺关键字段已标红）', false); return; }
        rewards = res.rewards;
    }

    const payload = {
        type,
        value: +el('c-value').value,
        count: +el('c-count').value,
        batchTag: el('c-batch').value.trim() || null,
        poolId: type === 'lotteryPoolTickets' ? (el('c-pool').value || null) : null,
        expiresUtc: toUnixSec(el('c-expire').value),
        maxRedemptions: Math.max(1, +el('c-max-redemptions').value || 1),
        perPlayerOnce: el('c-per-player-once').checked,
        commonCode: el('c-common').checked,
        rewards,
    };
    const r = await api('/codes/generate', 'POST', payload);
    if (r.success) { el('gen-out').value = r.codes.join('\n'); toast('已生成 ' + r.codes.length + ' 个', true); loadCodes(); }
    else toast(r.message || '失败', false);
};
el('c-add-reward').onclick = () => RewardEditor.addCard(el('c-reward-editor'), null, lotteryPoolsForCodes);
el('c-type').onchange = updateCodePoolVisibility;
updateCodePoolVisibility();
el('refresh-codes').onclick = loadCodes;
el('export-codes').onclick = async () => {
    const selected = Array.from(document.querySelectorAll('.code-check:checked')).map(cb => cb.value);
    const res = selected.length
        ? await fetch(ADMIN_API + '/codes/export', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN },
            body: JSON.stringify({ codes: selected }),
        })
        : await fetch(ADMIN_API + '/codes/export', { headers: { 'X-Admin-Token': ADMIN_TOKEN } });
    if (!res.ok) return toast('导出失败', false);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = selected.length ? 'battlepass-selected-codes.csv' : 'battlepass-codes.csv';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
    toast(selected.length ? `已导出 ${selected.length} 个激活码` : '已导出全部激活码', true);
};
async function loadCodes() {
    const r = await api('/codes');
    if (!r.success) return;
    const typeLabel = c => ({
        premium: '付费轨',
        levels: '直升' + c.value + '级',
        lotteryGlobalTickets: '通用券 ×' + c.value,
        lotteryPoolTickets: '限定券 ×' + c.value,
        lotteryExchangeCoins: '兑换币 ×' + c.value,
        rewards: '自定义奖励 ×' + ((c.rewards && c.rewards.length) || 0),
    }[c.type] || c.type || '-');
    const statusLabel = c => {
        const max = c.maxRedemptions || 1;
        const used = c.redeemCount || (c.redeemedBy ? 1 : 0);
        return used >= max ? '已用完' : `可用 ${Math.max(0, max - used)}/${max}`;
    };
    const rows = r.codes.slice().reverse().map(c =>
        `<tr><td><input class="code-check" type="checkbox" value="${esc(c.code)}" /></td><td>${esc(c.code)}</td><td>${esc(typeLabel(c))}</td><td>${esc(c.poolId || '-')}</td>
         <td>${esc(c.batchTag || '-')}</td><td class="${(c.redeemCount || c.redeemedBy) ? 'badge-used' : 'badge-free'}">${esc(statusLabel(c))}</td></tr>`).join('');
    el('code-rows').innerHTML = `<table class="grid"><tr><th><input id="code-check-all" type="checkbox" /></th><th>激活码</th><th>类型</th><th>奖池</th><th>批次</th><th>状态</th></tr>${rows}</table>`;
    const all = el('code-check-all');
    if (all) {
        all.onchange = () => document.querySelectorAll('.code-check').forEach(cb => { cb.checked = all.checked; });
    }
}

// ---- 玩家 ----
el('refresh-players').onclick = loadPlayers;
el('reset-selected-players').onclick = () => resetBattlePassProgress(false);
el('reset-all-players').onclick = () => resetBattlePassProgress(true);
let allPlayers = [];
const selectedPlayerIdSet = new Set();
async function loadPlayers() {
    const r = await api('/players');
    if (!r.success) return;
    allPlayers = r.players || [];
    const validIds = new Set(allPlayers.map(p => String(p.profileId || '')));
    for (const id of selectedPlayerIdSet) if (!validIds.has(id)) selectedPlayerIdSet.delete(id);
    renderPlayers();
}

function renderPlayers() {
    const query = el('player-search').value.trim().toLowerCase();
    const players = allPlayers.filter(p => !query || [p.username, p.nickname, p.profileId]
        .some(value => String(value || '').toLowerCase().includes(query)));
    const rows = players.map(p => {
        const pid = String(p.profileId || '');
        return `<tr><td><input class="player-check" type="checkbox" value="${esc(pid)}"${selectedPlayerIdSet.has(pid) ? ' checked' : ''} /></td>
         <td>${esc(p.username || '-')}</td><td>${esc(p.nickname || '-')}</td><td>${p.level}</td><td>${p.xp}</td>
         <td>${p.premiumUnlocked ? '✓' : '-'}</td><td>${esc(p.seasonId || '-')}</td><td class="meta">${esc(pid.slice(0, 8))}</td></tr>`;
    }).join('');
    el('player-rows').innerHTML = `<table class="grid"><tr><th><input id="player-check-all" type="checkbox" /></th><th>账号</th><th>角色昵称</th><th>等级</th><th>经验</th><th>付费</th><th>赛季</th><th>ID</th></tr>${rows || '<tr><td colspan="8" class="muted">没有匹配的玩家</td></tr>'}</table>`;
    el('player-filter-hint').textContent = `显示 ${players.length} / ${allPlayers.length}`;
    const all = el('player-check-all');
    document.querySelectorAll('.player-check').forEach(cb => {
        cb.onchange = () => cb.checked ? selectedPlayerIdSet.add(cb.value) : selectedPlayerIdSet.delete(cb.value);
    });
    if (all) {
        const visible = Array.from(document.querySelectorAll('.player-check'));
        all.checked = visible.length > 0 && visible.every(cb => cb.checked);
        all.onchange = () => visible.forEach(cb => {
            cb.checked = all.checked;
            if (all.checked) selectedPlayerIdSet.add(cb.value); else selectedPlayerIdSet.delete(cb.value);
        });
    }
}
el('player-search').addEventListener('input', renderPlayers);

function selectedPlayerIds() {
    return Array.from(selectedPlayerIdSet).filter(Boolean);
}

async function resetBattlePassProgress(all) {
    if (isCollaborator()) return toast('玩家进度重置仅管理员可用', false);
    const preservePremium = el('bp-reset-preserve-premium').checked;
    const ids = all ? [] : selectedPlayerIds();
    if (!all && ids.length === 0) {
        toast('请先勾选要重置的玩家', false);
        return;
    }

    const scopeText = all ? '所有玩家' : `${ids.length} 名选中玩家`;
    const premiumText = preservePremium ? '保留付费轨解锁' : '同时清除付费轨解锁';
    if (!confirm(`确认重置${scopeText}的完整通行证进度？\n\n等级、经验、领取记录、循环进度、任务和补偿账本会回到当前赛季初始状态，并收回通行证商人购买权与配方；永久称号保留；${premiumText}。`)) {
        return;
    }

    const r = await api('/players/reset-progress', 'POST', all
        ? { all: true, preservePremium }
        : { profileIds: ids, preservePremium });
    toast(r.success ? (r.message || '已重置') : (r.message || '重置失败'), r.success);
    if (r.success) {
        selectedPlayerIdSet.clear();
        await loadPlayers();
        el('player-action-hint').textContent = `上次操作：${r.message || '已重置'}`;
    }
}

function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

el('admin-login-btn').onclick = doAdminPasswordLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doAdminPasswordLogin(); });
el('admin-logout').onclick = logout;

// 入口：管理员使用已存会话或 Portal #sso=；协管携带交换令牌进入并按能力分流。
(async function () {
    const ok = await ensureAdminSession();
    if (ok) {
        ADMIN_TOKEN = getAdminToken();
        let edit = null;
        try { edit = await loadPendingChangeEdit(); }
        catch (error) { showCollaboratorGate(error.message || '无法加载待审内容'); return; }
        await enterConsole();
        applyPendingTracksEdit(edit);
        return;
    }
    // 会话失败：协管走无密码 gate（铁律：绝不向协管展示管理员密码框），管理员回落密码登录。
    if (handleSessionFailure()) return;
    if (getAdminSessionError()) {
        el('admin-login-msg').textContent = getAdminSessionError();
    }
})();
