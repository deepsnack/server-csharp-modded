'use strict';

const LOTTERY_API = '/battlepass/api/lottery';
const BP_API = '/battlepass/api';
let TOKEN = sessionStorage.getItem('bp_token') || '';

let overview = null;
let selectedPoolId = '';
let selectedPool = null;
let selectedDetail = null;
let drawing = false;

function el(id) { return document.getElementById(id); }
function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test(String(s || '').trim()); }
function wait(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

function toast(msg, ok) {
    const t = el('toast');
    t.textContent = msg || '';
    t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json' };
    if (TOKEN) headers['X-BP-Token'] = TOKEN;
    const res = await fetch(LOTTERY_API + path, {
        method: method || 'GET',
        headers,
        body: body ? JSON.stringify(body) : undefined,
    });
    return res.json();
}

function requestId() {
    if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
    return 'lottery-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
}

function rewardTypeLabel(type) {
    switch (String(type || 'item').toLowerCase()) {
        case 'purchaseright': return '购买权';
        case 'recipe': return '配方';
        case 'title': return '称号';
        case 'clothing': return '服装';
        default: return '物品';
    }
}

function formatTime(unix) {
    if (!unix || unix <= 0) return '不限时';
    return new Date(unix * 1000).toLocaleString();
}

function formatWindow(pool) {
    if (!pool) return '';
    const now = Math.floor(Date.now() / 1000);
    if (pool.startUtc > now) return '未开始 · ' + formatTime(pool.startUtc);
    if (pool.endUtc > 0 && pool.endUtc <= now) return '已结束';
    if (pool.endUtc > 0) {
        const left = pool.endUtc - now;
        const d = Math.floor(left / 86400);
        const h = Math.floor((left % 86400) / 3600);
        return `剩余 ${d} 天 ${h} 小时`;
    }
    return '开放中';
}

function costLabel(cost, fallbackType) {
    if (!cost) return '未配置';
    const type = cost.costType || fallbackType;
    if (type === 'stashItems') {
        const items = cost.stashItems || [];
        if (!items.length) return '仓库物品未配置';
        return items.map(x => `${x.name || x.tpl || '物品'} ×${x.count || 1}`).join(' + ');
    }
    return `抽奖券 ×${cost.ticketAmount || 1}`;
}

function nextSingleCost(pool) {
    if (!pool) return null;
    if (pool.poolType === 'nonRepeatable') {
        const nextDraw = (pool.successfulDrawCount || 0) + 1;
        const step = (pool.stepCosts || []).find(x => Number(x.drawNumber) === nextDraw);
        return step?.cost || pool.singleCost;
    }
    return pool.singleCost;
}

function tenCost(pool) {
    if (!pool) return null;
    if (pool.costType === 'lotteryTickets' && pool.tenDrawCostOverride) return pool.tenDrawCostOverride;
    if (pool.costType === 'lotteryTickets') {
        return { costType: 'lotteryTickets', ticketAmount: Math.max(1, pool.singleCost?.ticketAmount || 1) * 10, stashItems: [] };
    }
    return { costType: 'stashItems', ticketAmount: 0, stashItems: (pool.singleCost?.stashItems || []).map(x => ({ ...x, count: (x.count || 1) * 10 })) };
}

function rewardIcon(reward, fallbackIcon) {
    if (fallbackIcon) return fallbackIcon;
    if (reward?.iconUrl) return reward.iconUrl;
    if (isTpl(reward?.tpl)) return BP_API + '/icons/' + reward.tpl;
    return '';
}

function rewardTitle(reward, fallback) {
    return reward?.name || fallback || reward?.titleId || reward?.recipeId || reward?.offerId || reward?.suitId || reward?.tpl || rewardTypeLabel(reward?.type);
}

function isMobileLayout() {
    return window.matchMedia && window.matchMedia('(max-width: 720px)').matches;
}

function scrollIntoViewOnMobile(selector) {
    if (!isMobileLayout()) return;
    const target = document.querySelector(selector);
    if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

function showBlocked(message) {
    el('lottery-main').classList.add('hidden');
    el('lottery-alert').classList.remove('hidden');
    el('lottery-alert-text').textContent = message || '抽奖模块暂不可用';
}

function showMain() {
    el('lottery-alert').classList.add('hidden');
    el('lottery-main').classList.remove('hidden');
}

function renderWallet(wallet) {
    wallet = wallet || {};
    const poolTickets = selectedPoolId ? (wallet.poolTickets || {})[selectedPoolId] || 0 : 0;
    el('wallet-global').textContent = wallet.globalTickets || 0;
    el('wallet-coins').textContent = wallet.exchangeCoins || 0;
    el('wallet-pool').textContent = poolTickets;
}

const RARITY_RANK = { common: 0, rare: 1, epic: 2, legendary: 3 };
function rarityOf(r) { return (r && RARITY_RANK[r] != null) ? r : 'common'; }

function broadcastPill(x, extra) {
    const r = rarityOf(x.raritySnapshot);
    return `<span class="broadcast-pill r-${r}${extra ? ' ' + extra : ''}">
        <b>${esc(x.nickname)}</b><i>${esc(x.prizeNameSnapshot)}</i><small>${esc(x.poolNameSnapshot)}</small>
    </span>`;
}

function renderBroadcasts(items) {
    const box = el('lottery-broadcast');
    if (!items || !items.length) {
        box.innerHTML = '<span class="lottery-empty">暂无全服大奖记录</span>';
        return;
    }
    const rows = items.map(x => broadcastPill(x)).join('');
    box.innerHTML = `<div class="broadcast-track">${rows}${rows}</div>`;
}

function injectSelfBroadcast(records) {
    const win = (records || []).find(r => r.broadcastWhenWon);
    if (!win) return;
    const track = el('lottery-broadcast').querySelector('.broadcast-track');
    if (!track) return;
    const pill = document.createElement('span');
    pill.className = 'broadcast-pill r-' + rarityOf(win.raritySnapshot) + ' broadcast-self';
    pill.innerHTML = `<b>你</b><i>${esc(win.prizeNameSnapshot || '')}</i><small>${esc(win.poolNameSnapshot || '')}</small>`;
    track.insertBefore(pill, track.firstChild);
    setTimeout(() => pill.remove(), 6000);
}

function renderPools() {
    const box = el('lottery-pool-list');
    const pools = overview?.pools || [];
    if (!pools.length) {
        box.innerHTML = '<div class="lottery-empty">当前没有开放奖池</div>';
        return;
    }

    box.innerHTML = pools.map(pool => {
        const active = pool.id === selectedPoolId ? ' active' : '';
        const type = pool.poolType === 'nonRepeatable' ? '不可重复' : '可重复';
        const pity = pool.pityEnabled ? `保底 ${pool.pityCounter || 0}/${pool.pityCount || 0}` : '无保底';
        const icon = pool.iconUrl ? `<span class="lottery-pool-icon"><img src="${esc(pool.iconUrl)}" alt="" onerror="this.remove()" /></span>` : '<span class="lottery-pool-icon empty"></span>';
        return `<button class="lottery-pool-card${active}" type="button" data-pool="${esc(pool.id)}">
            <span class="lottery-pool-top">${icon}<span class="lottery-pool-name">${esc(pool.name || pool.id)}</span></span>
            <span class="lottery-pool-meta">${type} · ${esc(formatWindow(pool))}</span>
            <span class="lottery-pool-foot">${pool.remainingPrizes}/${pool.totalPrizes} 奖项 · ${pity}</span>
        </button>`;
    }).join('');

    box.querySelectorAll('.lottery-pool-card').forEach(btn => {
        btn.onclick = () => selectPool(btn.dataset.pool);
    });
}

function renderDetail() {
    const pool = selectedPool;
    renderWallet(selectedDetail?.wallet || overview?.wallet);
    if (!pool) {
        el('lottery-pool-title').textContent = '选择奖池';
        el('lottery-pool-desc').textContent = '当前没有可抽取的奖池。';
        el('lottery-prizes').innerHTML = '';
        return;
    }

    el('lottery-pool-title').textContent = pool.name || pool.id;
    el('lottery-pool-desc').textContent = pool.description || '查看概率、消耗与抽取记录。';
    el('pool-detail-title').textContent = pool.name || '奖池详情';
    el('pool-detail-meta').textContent = `${pool.poolType === 'nonRepeatable' ? '不可重复' : '可重复'} · ${formatWindow(pool)}`;
    el('pool-cover').style.backgroundImage = pool.coverUrl ? `url("${pool.coverUrl}")` : '';
    el('pool-cover').classList.toggle('empty', !pool.coverUrl);

    const onceCost = costLabel(nextSingleCost(pool), pool.costType);
    const ten = pool.poolType === 'repeatable' ? costLabel(tenCost(pool), pool.costType) : '不可重复池不支持十连';
    el('pool-cost').innerHTML = `<span>单抽：${esc(onceCost)}</span><span>十连：${esc(ten)}</span>`;

    const progressText = pool.poolType === 'nonRepeatable'
        ? `已抽 ${pool.successfulDrawCount || 0}/${pool.totalPrizes || 0}`
        : (pool.pityEnabled ? `保底计数 ${pool.pityCounter || 0}/${pool.pityCount || 0}` : `已抽 ${pool.successfulDrawCount || 0} 次`);
    el('pool-progress').textContent = progressText;
    el('pool-message').textContent = pool.canDraw ? '' : (pool.drawMessage || '当前不可抽取');
    el('probability-mode').textContent = pool.probabilityMode === 'weight' ? '按权重概率' : '等概率';

    const canDraw = pool.canDraw && !drawing && (pool.remainingPrizes || 0) > 0;
    el('draw-once').disabled = !canDraw;
    el('draw-ten').disabled = !canDraw || pool.poolType !== 'repeatable';

    renderPrizes();
}

function renderPrizes() {
    const box = el('lottery-prizes');
    const prizes = selectedDetail?.prizes || [];
    if (!prizes.length) {
        box.innerHTML = '<div class="lottery-empty">暂无奖项</div>';
        return;
    }

    box.innerHTML = prizes.map(prize => {
        const reward = prize.reward || {};
        const icon = rewardIcon(reward, reward.iconUrl);
        const drawn = prize.drawn ? ' drawn' : '';
        const grand = prize.isGrandPrize ? ' grand' : '';
        const probability = Number(prize.probability || 0) * 100;
        return `<div class="lottery-prize${drawn}${grand}">
            <div class="lottery-prize-icon">${icon ? `<img src="${esc(icon)}" alt="" onerror="this.remove()" />` : ''}</div>
            <div class="lottery-prize-body">
                <strong>${esc(prize.name || rewardTitle(reward))}</strong>
                <span>${esc(rewardTypeLabel(reward.type))}${prize.isGrandPrize ? ' · 大奖' : ''}${prize.drawn ? ' · 已抽取' : ''}</span>
            </div>
            <div class="lottery-prob">${probability.toFixed(2)}%</div>
        </div>`;
    }).join('');
}

function renderRecords(records) {
    const box = el('lottery-records');
    records = records || [];
    if (!records.length) {
        box.innerHTML = '<div class="lottery-empty">暂无抽取记录</div>';
        return;
    }

    box.innerHTML = records.map(r => `<div class="lottery-record${r.isGrandPrize ? ' grand' : ''}">
        <span>${esc(r.prizeNameSnapshot || r.prizeId)}</span>
        <small>${esc(r.poolNameSnapshot || r.poolId)} · ${formatTime(r.createdUtc)}${r.convertedToExchangeCoin ? ` · 转换 ${r.exchangeCoinAmount || 0} 兑换币` : ''}</small>
    </div>`).join('');
}

function renderShop(items) {
    const box = el('lottery-shop');
    items = items || [];
    if (!items.length) {
        box.innerHTML = '<div class="lottery-empty">兑换商店暂未上架商品</div>';
        return;
    }

    box.innerHTML = items.map(item => {
        const reward = item.reward || {};
        const icon = rewardIcon(reward, item.iconUrl);
        const remaining = item.remaining < 0 ? '不限购' : `剩余 ${item.remaining}`;
        return `<div class="lottery-shop-item">
            <div class="lottery-shop-icon">${icon ? `<img src="${esc(icon)}" alt="" onerror="this.remove()" />` : ''}</div>
            <div class="lottery-shop-info">
                <strong>${esc(item.name || rewardTitle(reward))}</strong>
                <span>${esc(item.description || rewardTypeLabel(reward.type))}</span>
                <small>兑换币 ×${item.price || 0} · ${esc(remaining)}</small>
            </div>
            <button class="btn ghost small lottery-buy" data-item="${esc(item.id)}" ${item.canBuy ? '' : 'disabled'}>${item.canBuy ? '兑换' : esc(item.limitMessage || '不可兑换')}</button>
        </div>`;
    }).join('');

    box.querySelectorAll('.lottery-buy').forEach(btn => {
        btn.onclick = () => purchaseShop(btn.dataset.item);
    });
}

function renderOverview() {
    renderWallet(overview?.wallet);
    renderBroadcasts(overview?.broadcasts || []);
    renderPools();
    renderRecords(overview?.records || []);
    renderShop(overview?.shop || []);
}

async function loadOverview(keepSelection) {
    if (!TOKEN) {
        showBlocked('请先返回通行证页面登录。');
        return;
    }

    const r = await api('/overview');
    if (!r.success) {
        showBlocked(r.message || '抽奖模块暂不可用');
        return;
    }

    showMain();
    overview = r;
    const pools = overview.pools || [];
    if (!keepSelection || !pools.some(p => p.id === selectedPoolId)) {
        selectedPoolId = pools[0]?.id || '';
    }
    renderOverview();
    if (selectedPoolId) await selectPool(selectedPoolId, true);
}

async function selectPool(poolId, skipOverviewRender) {
    selectedPoolId = poolId || '';
    if (!skipOverviewRender) renderPools();
    if (!selectedPoolId) {
        selectedPool = null;
        selectedDetail = null;
        renderDetail();
        return;
    }

    const r = await api('/pools/' + encodeURIComponent(selectedPoolId));
    if (!r.success) {
        toast(r.message || '加载奖池失败', false);
        return;
    }

    selectedDetail = r;
    selectedPool = r.pool;
    renderDetail();
    if (!skipOverviewRender) scrollIntoViewOnMobile('.lottery-detail');
}

function effectiveRarity(records) {
    let best = 'common';
    for (const r of records || []) {
        if ((RARITY_RANK[r.raritySnapshot] || 0) > RARITY_RANK[best]) best = rarityOf(r.raritySnapshot);
    }
    return best;
}

function prefersReducedMotion() {
    return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

const PARTICLE_COUNT = { common: 0, rare: 8, epic: 16, legendary: 28 };

function spawnParticles(rarity) {
    const layer = el('lottery-openbox').querySelector('.openbox-particles');
    layer.innerHTML = '';
    const n = PARTICLE_COUNT[rarity] || 0;
    for (let i = 0; i < n; i++) {
        const s = document.createElement('span');
        const ang = Math.random() * Math.PI * 2;
        const dist = 40 + Math.random() * 90;
        s.style.setProperty('--dx', (Math.cos(ang) * dist).toFixed(1) + 'px');
        s.style.setProperty('--dy', (Math.sin(ang) * dist - 40).toFixed(1) + 'px');
        s.style.animationDelay = (Math.random() * 0.12).toFixed(2) + 's';
        layer.appendChild(s);
    }
}

function triggerFlash() {
    const f = el('lottery-flash');
    f.classList.remove('show');
    void f.offsetWidth;
    f.classList.add('show');
    setTimeout(() => f.classList.remove('show'), 700);
}

// 开箱分镜：~2s 蓄力—爆开—光柱—定格，点击任意处或 SKIP 立即跳到定格。
function playOpenBox(records) {
    const box = el('lottery-openbox');
    const rarity = effectiveRarity(records);
    box.classList.remove('hidden', 'arming', 'skip', 'playing');
    box.dataset.rarity = rarity;
    el('result-sub').textContent = '开箱中';

    if (prefersReducedMotion()) {
        renderDrawResult(records);
        return Promise.resolve();
    }

    void box.offsetWidth; // 重排以重启动画
    box.classList.add('playing');

    return new Promise(resolve => {
        let done = false;
        const timers = [];
        const finish = () => {
            if (done) return;
            done = true;
            timers.forEach(clearTimeout);
            box.removeEventListener('click', onSkip);
            renderDrawResult(records);
            resolve();
        };
        const onSkip = () => { box.classList.add('skip'); finish(); };
        box.addEventListener('click', onSkip);
        timers.push(setTimeout(() => {
            spawnParticles(rarity);
            if (rarity === 'legendary') triggerFlash();
        }, 620));
        timers.push(setTimeout(finish, 2000));
    });
}

function showOpenboxLoading() {
    const box = el('lottery-openbox');
    el('lottery-result').innerHTML = '';
    box.classList.remove('hidden', 'playing', 'skip');
    box.dataset.rarity = 'common';
    el('result-sub').textContent = '抽取中';
    void box.offsetWidth;
    box.classList.add('arming');
}

function renderDrawResult(records) {
    const box = el('lottery-result');
    records = records || [];
    if (!records.length) {
        box.innerHTML = '<div class="lottery-empty">没有返回奖励结果</div>';
        return;
    }

    box.innerHTML = records.map(r => {
        const icon = rewardIcon(null, r.prizeIconSnapshot);
        return `<div class="lottery-result-card r-${rarityOf(r.raritySnapshot)}${r.isGrandPrize ? ' grand' : ''}${r.convertedToExchangeCoin ? ' converted' : ''}">
            <div class="lottery-result-icon">${icon ? `<img src="${esc(icon)}" alt="" onerror="this.remove()" />` : ''}</div>
            <strong>${esc(r.prizeNameSnapshot || r.prizeId)}</strong>
            <span>${r.isGrandPrize ? '大奖 · ' : ''}${esc(rewardTypeLabel(r.prizeType))}</span>
            ${r.convertedToExchangeCoin ? `<small>重复奖励已转换为 ${r.exchangeCoinAmount || 0} 兑换币</small>` : ''}
        </div>`;
    }).join('');
    el('result-sub').textContent = records.length > 1 ? `本次获得 ${records.length} 项奖励` : '本次获得';
    scrollIntoViewOnMobile('.lottery-result-panel');
}

async function draw(mode) {
    if (!selectedPoolId || drawing) return;
    drawing = true;
    renderDetail();
    showOpenboxLoading();
    const endpoint = mode === 'ten' ? '/draw-ten' : '/draw-once';
    const drawRequest = api('/pools/' + encodeURIComponent(selectedPoolId) + endpoint, 'POST', { requestId: requestId() });
    const [r] = await Promise.all([drawRequest, wait(500)]);
    drawing = false;

    if (!r.success) {
        el('lottery-openbox').classList.add('hidden');
        el('result-sub').textContent = '抽取失败';
        toast(r.message || '抽取失败', false);
        renderDetail();
        scrollIntoViewOnMobile('.lottery-result-panel');
        return;
    }

    await playOpenBox(r.records || []);
    toast(r.message || '抽取成功', true);
    if (r.wallet) renderWallet(r.wallet);
    await loadOverview(true);
    injectSelfBroadcast(r.records || []);
}

async function purchaseShop(itemId) {
    if (!itemId) return;
    const r = await api('/shop/' + encodeURIComponent(itemId) + '/purchase', 'POST');
    toast(r.message || (r.success ? '兑换成功' : '兑换失败'), r.success);
    if (r.wallet) renderWallet(r.wallet);
    await loadOverview(true);
}

async function refreshRecords() {
    const r = await api('/records');
    if (r.success) renderRecords(r.records || []);
}

function logout() {
    TOKEN = '';
    sessionStorage.removeItem('bp_token');
    location.href = 'index.html';
}

el('lottery-logout').onclick = logout;
el('lottery-refresh').onclick = () => loadOverview(true);
el('record-refresh').onclick = refreshRecords;
el('draw-once').onclick = () => draw('once');
el('draw-ten').onclick = () => draw('ten');

loadOverview(false);
