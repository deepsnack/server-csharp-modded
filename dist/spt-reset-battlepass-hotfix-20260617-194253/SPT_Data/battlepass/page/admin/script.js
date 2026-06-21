'use strict';

const ADMIN_API = '/battlepass/api/admin';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

// 支持 Portal SSO：URL 片段 #sso=token
(function () {
    const m = location.hash.match(/sso=([a-zA-Z0-9]+)/);
    if (m) { ADMIN_TOKEN = m[1]; sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN); history.replaceState(null, '', location.pathname); }
})();

function el(id) { return document.getElementById(id); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN };
    const res = await fetch(ADMIN_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// ---- 登录（复用注册后台的 admin/login 取 token）----
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

function logout() { ADMIN_TOKEN = ''; sessionStorage.removeItem('bp_admin_token'); el('admin-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }

function enterConsole() {
    el('login-view').classList.add('hidden');
    el('admin-view').classList.remove('hidden');
    loadSeason(); loadTracks(); loadCodes(); loadPlayers();
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

async function postTracksAndSeason(tracksPayload) {
    const r = await api('/tracks', 'POST', tracksPayload);
    if (!r.success) return toast(r.message || '保存失败', false);
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
    loadTracks();
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

// 按类型收集与校验：item 需要 tpl；purchaseRight/recipe/title 需要对应引用 id（tpl 可空）。
// 修复旧逻辑 if(!tpl) return 的静默丢卡——非物品奖励没填 tpl 会在保存时直接消失。
// 完全空白的卡（新加未填）跳过；填了内容但缺关键字段的卡保留并标红提示，不再无声丢失。
function collectRewardsIn(block, track) {
    const items = [];
    block.querySelectorAll(`.reward-cards[data-track="${track}"] .rw-card`).forEach(card => {
        const type = card.querySelector('.rw-type')?.value || 'item';
        const tpl = card.querySelector('.rw-tpl')?.value?.trim() || '';
        const name = card.querySelector('.rw-name')?.value?.trim() || null;
        const offerId = card.querySelector('.rw-offerid')?.value?.trim() || null;
        const recipeId = card.querySelector('.rw-recipeid')?.value?.trim() || null;
        const titleId = card.querySelector('.rw-titleid')?.value?.trim() || null;

        const keyField = { item: tpl, purchaseRight: offerId, recipe: recipeId, title: titleId }[type];
        const isBlank = !tpl && !name && !offerId && !recipeId && !titleId;
        if (isBlank) return; // 全空卡 = 未填的新卡，不入库

        if (!keyField) {
            card.classList.add('rw-invalid');
            rwInvalidCount++;
        } else {
            card.classList.remove('rw-invalid');
        }

        items.push({
            tpl: type === 'item' ? tpl : (tpl || null),
            count: type === 'item' ? +(card.querySelector('.rw-count')?.value || 1) : 1,
            name,
            featured: card.querySelector('.rw-featured')?.checked || false,
            foundInRaid: type === 'item' ? (card.querySelector('.rw-fir')?.checked || false) : false,
            type,
            offerId: type === 'purchaseRight' ? offerId : null,
            recipeId: type === 'recipe' ? recipeId : null,
            titleId: type === 'title' ? titleId : null,
        });
    });
    return items;
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
function levelBlockHtml(level, isCycle) {
    const title = isCycle ? '♾️ 循环奖励（满级后每轮 · 逐轮可领）' : `Lv ${level}`;
    const copyBtn = (!isCycle && level > 1) ? `<button class="mini ghost copy-prev-btn" title="从上一级复制">⧉ 复制 Lv${level - 1}</button>` : '';
    return `<div class="level-block${isCycle ? ' cycle-block' : ''}" data-level="${isCycle ? CYCLE_KEY : level}">
        <div class="level-head"><span class="lv-title">${title}</span>${copyBtn}</div>
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
    </div>`;
}

function renderLevelBlock(level, isCycle) {
    const key = isCycle ? CYCLE_KEY : String(level);
    const data = allTracks[key] || { free: [], premium: [] };
    const wrap = document.createElement('div');
    wrap.innerHTML = levelBlockHtml(level, isCycle);
    const block = wrap.firstElementChild;
    el('tracks-list').appendChild(block);
    fillRewardCards(block, 'free', data.free || []);
    fillRewardCards(block, 'premium', data.premium || []);
    wireBlock(block);
}

function fillRewardCards(block, track, items) {
    const box = block.querySelector(`.reward-cards[data-track="${track}"]`);
    if (!items.length) { box.innerHTML = '<div class="muted rw-empty">暂无，点「＋ 添加」</div>'; return; }
    box.innerHTML = items.map((r, i) => rewardCardHtml(track, r, i)).join('');
    box.querySelectorAll('.rw-tpl').forEach(inp => setupRwItemPicker(inp));
    box.querySelectorAll('.rw-offerid').forEach(inp => setupRwRefPicker(inp, 'offer'));
    box.querySelectorAll('.rw-recipeid').forEach(inp => setupRwRefPicker(inp, 'recipe'));
    box.querySelectorAll('.rw-titleid').forEach(inp => setupRwRefPicker(inp, 'title'));
    box.querySelectorAll('.rw-type').forEach(sel => sel.addEventListener('change', () => onRwTypeChange(sel)));
}

function onRwTypeChange(sel) {
    const card = sel.closest('.rw-card');
    const itemRow = card.querySelector('.rw-item-row');
    if (itemRow) itemRow.style.display = sel.value === 'item' ? '' : 'none';
    card.querySelector('.rw-extra').style.display = sel.value === 'item' ? 'none' : '';
    ['.rw-offerid-row', '.rw-recipeid-row', '.rw-titleid-row'].forEach(s => { const e = card.querySelector(s); if (e) e.style.display = 'none'; });
    const map = { purchaseRight: '.rw-offerid-row', recipe: '.rw-recipeid-row', title: '.rw-titleid-row' };
    if (map[sel.value]) { const e = card.querySelector(map[sel.value]); if (e) e.style.display = ''; }
}

function wireBlock(block) {
    block.querySelectorAll('.add-rw-btn').forEach(btn => btn.onclick = () => {
        const track = btn.dataset.track;
        const lvl = block.dataset.level;
        allTracks[lvl] = { free: collectRewardsIn(block, 'free'), premium: collectRewardsIn(block, 'premium') };
        allTracks[lvl][track].push({ tpl: '', count: 1, name: null, featured: false, type: 'item' });
        fillRewardCards(block, track, allTracks[lvl][track]);
        syncTracksJson();
    });
    const copyBtn = block.querySelector('.copy-prev-btn');
    if (copyBtn) copyBtn.onclick = () => {
        const level = +block.dataset.level;
        const prev = allTracks[String(level - 1)];
        if (!prev) return toast('上一级无数据', false);
        allTracks[String(level)] = { free: deepClone(prev.free || []), premium: deepClone(prev.premium || []) };
        fillRewardCards(block, 'free', allTracks[String(level)].free);
        fillRewardCards(block, 'premium', allTracks[String(level)].premium);
        syncTracksJson();
        toast('已从 Lv' + (level - 1) + ' 复制', true);
    };
}

function rewardCardHtml(track, r, i) {
    const type = r.type || 'item';
    const iconUrl = isTpl(r.tpl) ? ICON_API + r.tpl : '';
    const iconStyle = iconUrl ? '' : 'display:none';
    // 类型决定表单形态：item 显示物品搜索+数量；其余显示对应引用选择器，tpl/数量不再要求
    const itemStyle = type === 'item' ? '' : 'display:none';
    const extraStyle = type !== 'item' ? '' : 'display:none';
    const offerStyle = type === 'purchaseRight' ? '' : 'display:none';
    const recipeStyle = type === 'recipe' ? '' : 'display:none';
    const titleStyle = type === 'title' ? '' : 'display:none';
    return `<div class="rw-card">
        <div class="rw-card-main">
            <div class="rw-icon-wrap"><img src="${iconUrl}" class="rw-icon" style="${iconStyle}" alt="" onerror="this.style.display='none'" /></div>
            <div class="rw-fields">
                <div class="rw-row">
                    <select class="rw-type" style="width:90px">
                        <option value="item" ${type==='item'?'selected':''}>物品</option>
                        <option value="purchaseRight" ${type==='purchaseRight'?'selected':''}>购买权</option>
                        <option value="recipe" ${type==='recipe'?'selected':''}>配方</option>
                        <option value="title" ${type==='title'?'selected':''}>称号</option>
                    </select>
                    <input class="rw-name" value="${esc(r.name || '')}" placeholder="展示名（可空）" style="flex:1" />
                    <label class="with-cb" style="white-space:nowrap"><input class="rw-featured" type="checkbox" ${r.featured?'checked':''} /> 大奖</label>
                    <button class="mini del rw-del" title="删除" onclick="this.closest('.rw-card').remove()">×</button>
                </div>
                <div class="rw-row rw-item-row" style="${itemStyle}">
                    <input class="rw-tpl" value="${esc(r.tpl || '')}" placeholder="搜索物品名称或 tpl…" autocomplete="off" />
                    <input class="rw-count" type="number" value="${r.count || 1}" min="1" style="width:58px" title="数量" />
                    <label class="with-cb" style="white-space:nowrap" title="发放物品标记为战局内找到（SpawnedInSession）"><input class="rw-fir" type="checkbox" ${r.foundInRaid?'checked':''} /> FIR</label>
                    <div class="rw-tpl-results" style="display:none"></div>
                </div>
                <div class="rw-extra" style="${extraStyle}">
                    <div class="rw-row rw-offerid-row" style="${offerStyle}">
                        <label style="font-size:10px">购买权</label>
                        <input class="rw-offerid" value="${esc(r.offerId||'')}" placeholder="聚焦列出 / 搜索货架项…" style="flex:1" autocomplete="off" />
                        <div class="rw-offerid-results rw-tpl-results" style="display:none"></div>
                    </div>
                    <div class="rw-row rw-recipeid-row" style="${recipeStyle}">
                        <label style="font-size:10px">配方</label>
                        <input class="rw-recipeid" value="${esc(r.recipeId||'')}" placeholder="搜索配方产物名称或 id…" style="flex:1" autocomplete="off" />
                        <div class="rw-recipeid-results rw-tpl-results" style="display:none"></div>
                    </div>
                    <div class="rw-row rw-titleid-row" style="${titleStyle}">
                        <label style="font-size:10px">称号</label>
                        <input class="rw-titleid" value="${esc(r.titleId||'')}" placeholder="聚焦列出 / 搜索称号…" style="flex:1" autocomplete="off" />
                        <div class="rw-titleid-results rw-tpl-results" style="display:none"></div>
                    </div>
                </div>
            </div>
        </div>
    </div>`;
}

// 奖励项引用选择器（购买权→货架项 / 配方→藏身处配方 / 称号；统一组件 BpPicker）。
// 货架/称号数据量少：minChars=0 聚焦即列出全部；配方多：输 2 字后按产物名检索。
// 选中后回填 id；物品类引用（货架商品/配方产物）顺带把卡片图标换成对应物品。
function setupRwRefPicker(input, kind) {
    const card = input.closest('.rw-card');
    const results = input.parentElement.querySelector('.rw-tpl-results');
    if (!results) return;
    const icon = card.querySelector('.rw-icon');
    const attach = { offer: BpPicker.attachOffer, recipe: BpPicker.attachRecipe, title: BpPicker.attachTitle }[kind];
    const minChars = kind === 'recipe' ? 2 : 0;
    attach(input, results, ds => {
        input.value = ds.id;
        card.classList.remove('rw-invalid');
        if (ds.tpl && BpPicker.isTpl(ds.tpl)) {
            icon.src = BpPicker.ICON_API + ds.tpl; icon.style.display = '';
            icon.onerror = () => { icon.style.display = 'none'; };
        }
    }, { limit: 8, minChars });
    // 配方下拉底部固定一条「＋ 新建自定义配方」直达配方管理页
    if (kind === 'recipe') {
        input.addEventListener('focus', () => {
            setTimeout(() => {
                if (results.querySelector('.sr-new-link')) return;
                const a = document.createElement('a');
                a.className = 'sr-new-link'; a.textContent = '＋ 新建自定义配方（配方管理页）'; a.href = 'recipes.html'; a.target = '_blank';
                results.appendChild(a);
            }, 350);
        });
    }
}

// 奖励项物品选择器（统一组件 BpPicker，见 picker.js；走统一查询接口）
function setupRwItemPicker(input) {
    const card = input.closest('.rw-card');
    const results = card.querySelector('.rw-tpl-results');
    const icon = card.querySelector('.rw-icon');
    BpPicker.attachItem(input, results, ds => {
        input.value = ds.tpl;
        icon.src = BpPicker.ICON_API + ds.tpl; icon.style.display = '';
        icon.onerror = () => { icon.style.display = 'none'; };
    }, { limit: 6 });
    input.addEventListener('input', () => {
        const q = input.value.trim();
        if (BpPicker.isTpl(q)) { icon.src = BpPicker.ICON_API + q; icon.style.display = ''; }
    });
}

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
}

// ---- 激活码 ----
el('gen-codes').onclick = async () => {
    const payload = { type: el('c-type').value, value: +el('c-value').value, count: +el('c-count').value, batchTag: el('c-batch').value.trim() || null };
    const r = await api('/codes/generate', 'POST', payload);
    if (r.success) { el('gen-out').value = r.codes.join('\n'); toast('已生成 ' + r.codes.length + ' 个', true); loadCodes(); }
    else toast(r.message || '失败', false);
};
el('refresh-codes').onclick = loadCodes;
async function loadCodes() {
    const r = await api('/codes');
    if (!r.success) return;
    const rows = r.codes.slice().reverse().map(c =>
        `<tr><td>${c.code}</td><td>${c.type === 'levels' ? '直升' + c.value + '级' : '付费轨'}</td>
         <td>${c.batchTag || '-'}</td><td class="${c.redeemedBy ? 'badge-used' : 'badge-free'}">${c.redeemedBy ? '已用' : '未用'}</td></tr>`).join('');
    el('code-rows').innerHTML = `<table class="grid"><tr><th>激活码</th><th>类型</th><th>批次</th><th>状态</th></tr>${rows}</table>`;
}

// ---- 玩家 ----
el('refresh-players').onclick = loadPlayers;
el('reset-selected-players').onclick = () => resetBattlePassProgress(false);
el('reset-all-players').onclick = () => resetBattlePassProgress(true);
async function loadPlayers() {
    const r = await api('/players');
    if (!r.success) return;
    const rows = (r.players || []).map(p => {
        const pid = String(p.profileId || '');
        return `<tr><td><input class="player-check" type="checkbox" value="${esc(pid)}" /></td>
         <td>${esc(p.username || '-')}</td><td>${p.level}</td><td>${p.xp}</td>
         <td>${p.premiumUnlocked ? '✓' : '-'}</td><td>${esc(p.seasonId || '-')}</td><td class="meta">${esc(pid.slice(0, 8))}</td></tr>`;
    }).join('');
    el('player-rows').innerHTML = `<table class="grid"><tr><th><input id="player-check-all" type="checkbox" /></th><th>账号</th><th>等级</th><th>经验</th><th>付费</th><th>赛季</th><th>ID</th></tr>${rows}</table>`;
    const all = el('player-check-all');
    if (all) {
        all.onchange = () => document.querySelectorAll('.player-check').forEach(cb => { cb.checked = all.checked; });
    }
}

function selectedPlayerIds() {
    return Array.from(document.querySelectorAll('.player-check:checked')).map(cb => cb.value).filter(Boolean);
}

async function resetBattlePassProgress(all) {
    const preservePremium = el('bp-reset-preserve-premium').checked;
    const ids = all ? [] : selectedPlayerIds();
    if (!all && ids.length === 0) {
        toast('请先勾选要重置的玩家', false);
        return;
    }

    const scopeText = all ? '所有玩家' : `${ids.length} 名选中玩家`;
    const premiumText = preservePremium ? '保留付费轨解锁' : '同时清除付费轨解锁';
    if (!confirm(`确认重置${scopeText}的通行证进度？\n\n等级、经验、领取记录和任务进度会回到当前赛季初始状态；${premiumText}。`)) {
        return;
    }

    const r = await api('/players/reset-progress', 'POST', all
        ? { all: true, preservePremium }
        : { profileIds: ids, preservePremium });
    toast(r.success ? (r.message || '已重置') : (r.message || '重置失败'), r.success);
    if (r.success) {
        await loadPlayers();
        el('player-action-hint').textContent = `上次操作：${r.message || '已重置'}`;
    }
}

function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;

if (ADMIN_TOKEN) enterConsole();
