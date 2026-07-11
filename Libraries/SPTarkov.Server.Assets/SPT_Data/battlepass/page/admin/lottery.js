'use strict';

const ADMIN_API = '/battlepass/api/admin/lottery';
const ICON_API = '/battlepass/api/icons/';

function el(id) { return document.getElementById(id); }
function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test(String(s || '').trim()); }
function newId(prefix) { return prefix + '_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 7); }
function toLocalDatetimeStr(unixSec) {
    if (!unixSec || unixSec <= 0) return '';
    return new Date(unixSec * 1000).toISOString().slice(0, 16);
}
function toUnixSec(value) {
    if (!value) return 0;
    return Math.floor(new Date(value).getTime() / 1000);
}
function fmtTime(unixSec) {
    if (!unixSec || unixSec <= 0) return '-';
    return new Date(unixSec * 1000).toLocaleString();
}

function toast(msg, ok) {
    const t = el('toast');
    t.textContent = msg || '';
    t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() };
    const res = await fetch(ADMIN_API + path, {
        method: method || 'GET',
        headers,
        body: body ? JSON.stringify(body) : undefined,
    });
    return res.json();
}

async function uploadAsset(kind, file) {
    const form = new FormData();
    form.append('file', file);
    form.append('kind', kind);
    const res = await fetch(ADMIN_API + '/assets/upload?kind=' + encodeURIComponent(kind), {
        method: 'POST',
        headers: { 'X-Admin-Token': getAdminToken() },
        body: form,
    });
    return res.json();
}

function renderAssetPreview(inputId, previewId) {
    const input = el(inputId);
    const img = el(previewId);
    if (!input || !img) return;
    const box = img.closest('.asset-preview');
    const url = input.value.trim();
    if (!url) {
        img.removeAttribute('src');
        box?.classList.remove('has-image');
        return;
    }

    img.src = url;
    box?.classList.add('has-image');
}

function renderPoolAssetPreviews() {
    renderAssetPreview('p-icon', 'p-icon-preview');
    renderAssetPreview('p-cover', 'p-cover-preview');
}

async function handlePoolAssetUpload(kind) {
    const fileInput = el(kind === 'cover' ? 'p-cover-file' : 'p-icon-file');
    const targetInput = el(kind === 'cover' ? 'p-cover' : 'p-icon');
    const file = fileInput?.files?.[0];
    if (!file || !targetInput) return;

    const r = await uploadAsset(kind, file);
    fileInput.value = '';
    if (!r.success) {
        toast(r.message || '上传失败', false);
        return;
    }

    targetInput.value = r.url || '';
    renderPoolAssetPreviews();
    toast(kind === 'cover' ? '展示图已上传' : '图标已上传', true);
}

async function doAdminLogin() {
    const password = el('admin-pass').value;
    const r = await adminLogin(password);
    if (!r.success) {
        el('admin-login-msg').textContent = r.message || '登录失败';
        return;
    }
    enterConsole();
}

function logout() {
    clearAdminToken();
    clearActorType();
    el('lottery-admin-view').classList.add('hidden');
    el('login-view').classList.remove('hidden');
}

let pools = [];
let currentPool = null;
let shopItems = [];
let currentShopItem = null;
let singleStashEditor = null;

function enterConsole() {
    el('login-view').classList.add('hidden');
    el('lottery-admin-view').classList.remove('hidden');
    applyCollaboratorUi();
    if (!singleStashEditor) singleStashEditor = BpPrice.createEditor(el('p-stash-cost'));
    newShopItem();
    return loadAll();
}

// 协管态：仅“配置类”变更走审核提交，运营类动作直接隐藏，避免协管点了报错。
function applyCollaboratorUi() {
    const collab = isCollaborator();
    el('collab-hint')?.classList.toggle('hidden', !collab);
    if (!collab) return;
    // 奖池运营动作（暂存/发布/暂停/结束/归档/复制草稿/重置进度）+ 商店运营（发布/暂存）
    [
        'save-pool-draft', 'publish-pool', 'pause-pool', 'end-pool', 'archive-pool',
        'copy-pool', 'reset-one-pool', 'reset-all-pool', 'reset-profile-id',
        'publish-shop-item', 'save-shop-draft',
    ].forEach(id => el(id)?.classList.add('hidden'));
    // 记录 / 运维（发币、审计日志、事务）读端点未对协管放开，隐藏对应 Tab 与面板
    document.querySelectorAll('.tabs .tab[data-section="records"], .tabs .tab[data-section="ops"]')
        .forEach(t => t.classList.add('hidden'));
    document.querySelectorAll('[data-section-panel="records"], [data-section-panel="ops"]')
        .forEach(p => p.classList.add('hidden'));
}

function switchSection(section) {
    document.querySelectorAll('.tabs .tab[data-section]').forEach(btn => btn.classList.toggle('active', btn.dataset.section === section));
    document.querySelectorAll('[data-section-panel]').forEach(panel => panel.classList.toggle('hidden', panel.dataset.sectionPanel !== section));
}

async function loadAll() {
    const tasks = [loadSettings(), loadPools(), loadShop()];
    // 记录/审计/事务读端点仅管理员，协管跳过以免 403 噪音
    if (!isCollaborator()) tasks.push(loadRecords(), loadLogs(), loadTransactions());
    await Promise.all(tasks);
}

async function loadSettings() {
    const r = await api('/settings');
    if (!r.success) return toast(r.message || '读取设置失败', false);
    fillSettings(r.settings || {});
}

function fillSettings(s) {
    el('set-enabled').checked = s.enabled !== false;
    el('set-entry').checked = s.playerEntryEnabled !== false;
    el('set-shop').checked = s.exchangeShopEnabled !== false;
    el('set-broadcast').checked = s.broadcastEnabled !== false;
    el('set-record-limit').value = s.playerRecordDisplayLimit || 50;
    el('set-name-mode').value = s.broadcastNameMode || 'full';
    el('set-timezone').value = s.timeZoneId || '';
}

async function saveSettings() {
    const payload = {
        enabled: el('set-enabled').checked,
        playerEntryEnabled: el('set-entry').checked,
        exchangeShopEnabled: el('set-shop').checked,
        broadcastEnabled: el('set-broadcast').checked,
        playerRecordDisplayLimit: Math.max(1, +el('set-record-limit').value || 50),
        broadcastNameMode: el('set-name-mode').value,
        timeZoneId: el('set-timezone').value.trim() || null,
    };
    if (await submitAsCollaborator('lottery.settings', payload)) return;
    const r = await api('/settings', 'POST', payload);
    toast(r.success ? '设置已保存' : (r.message || '保存失败'), r.success);
}

// 协管保存分流：白名单配置类改走审核队列。返回 true 表示已处理（调用方应 return）。
async function submitAsCollaborator(commandType, input) {
    if (!isCollaborator()) return false;
    const r = await submitChange('lottery', commandType, input);
    toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
    return true;
}

function defaultPool() {
    return {
        id: '',
        name: '',
        description: '',
        iconUrl: '',
        coverUrl: '',
        sortOrder: 0,
        enabled: true,
        visibleToPlayers: true,
        status: 'draft',
        pauseVisibleToPlayers: true,
        followSeason: false,
        startUtc: 0,
        endUtc: 0,
        poolType: 'repeatable',
        probabilityMode: 'equal',
        costType: 'lotteryTickets',
        singleCost: { costType: 'lotteryTickets', ticketAmount: 1, stashItems: [] },
        tenDrawCostOverride: null,
        stepCosts: [],
        pityEnabled: false,
        pityCount: 0,
        prizes: [],
    };
}

async function loadPools() {
    const r = await api('/pools');
    if (!r.success) return toast(r.message || '读取奖池失败', false);
    pools = r.pools || [];
    renderPoolList();
    renderGrantPoolSelect();
    if (!currentPool && pools.length) fillPool(pools[0]);
    if (!currentPool) fillPool(defaultPool());
}

function renderPoolList() {
    const box = el('pool-list');
    if (!pools.length) {
        box.innerHTML = '<div class="row-item muted">暂无奖池</div>';
        return;
    }
    box.innerHTML = pools.map(p => `<div class="row-item lottery-admin-row${currentPool?.id === p.id ? ' active' : ''}" data-id="${esc(p.id)}">
        <div>
            <div class="offer-title">${esc(p.name || p.id)} <span class="badge">${esc(p.status || 'draft')}</span></div>
            <div class="meta">${esc(p.id)} · ${p.poolType === 'nonRepeatable' ? '不可重复' : '可重复'} · 奖项 ${p.prizes?.length || 0}</div>
        </div>
        <button class="mini">编辑</button>
    </div>`).join('');
    box.querySelectorAll('.lottery-admin-row').forEach(row => {
        row.onclick = () => {
            const pool = pools.find(p => p.id === row.dataset.id);
            if (pool) fillPool(pool);
        };
    });
}

function fillPool(pool) {
    currentPool = JSON.parse(JSON.stringify(pool || defaultPool()));
    el('pool-editor-title').textContent = currentPool.id ? '编辑奖池' : '新增奖池草稿';
    el('p-id').value = currentPool.id || '';
    el('p-name').value = currentPool.name || '';
    el('p-desc').value = currentPool.description || '';
    el('p-icon').value = currentPool.iconUrl || '';
    el('p-cover').value = currentPool.coverUrl || '';
    el('p-sort').value = currentPool.sortOrder || 0;
    el('p-status').value = currentPool.status || 'draft';
    el('p-type').value = currentPool.poolType || 'repeatable';
    el('p-prob').value = currentPool.probabilityMode || 'equal';
    el('p-cost-type').value = currentPool.costType || 'lotteryTickets';
    el('p-enabled').checked = currentPool.enabled !== false;
    el('p-visible').checked = currentPool.visibleToPlayers !== false;
    el('p-pause-visible').checked = currentPool.pauseVisibleToPlayers !== false;
    el('p-follow-season').checked = currentPool.followSeason === true;
    el('p-start').value = toLocalDatetimeStr(currentPool.startUtc);
    el('p-end').value = toLocalDatetimeStr(currentPool.endUtc);
    el('p-ticket-single').value = currentPool.singleCost?.ticketAmount || 1;
    el('p-ticket-ten').value = currentPool.tenDrawCostOverride?.ticketAmount || 0;
    singleStashEditor.setCost(currentPool.singleCost?.stashItems || []);
    el('p-pity-enabled').checked = currentPool.pityEnabled === true;
    el('p-pity-count').value = currentPool.pityCount || 0;
    renderStepCosts(currentPool.stepCosts || []);
    renderPrizeRows(currentPool.prizes || []);
    updateCostVisibility();
    renderPoolAssetPreviews();
    renderPoolList();
    el('pool-validation').textContent = '';
}

function updateCostVisibility() {
    const type = el('p-cost-type').value;
    const poolType = el('p-type').value;
    const isNonRepeatable = poolType === 'nonRepeatable';
    el('ticket-cost-fields').classList.toggle('hidden', type !== 'lotteryTickets');
    el('stash-cost-fields').classList.toggle('hidden', type !== 'stashItems');
    el('ticket-ten-col')?.classList.toggle('disabled-field', type !== 'lotteryTickets' || isNonRepeatable);
    el('step-ticket-tools')?.classList.toggle('hidden', type !== 'lotteryTickets');
    el('step-pricing-panel')?.classList.toggle('inactive', !isNonRepeatable);
    document.querySelectorAll('#step-costs .step-ticket').forEach(x => x.classList.toggle('hidden', type !== 'lotteryTickets'));
    document.querySelectorAll('#step-costs .step-stash').forEach(x => x.classList.toggle('hidden', type !== 'stashItems'));
    el('cost-mode-help').textContent = isNonRepeatable
        ? '不可重复池只允许单抽，并按下方“每抽价格表”扣费。'
        : '可重复池使用基础单抽价；十连可设置折扣券数，留 0 时按单抽价 ×10。';
    updateStepCostSummary();
}

function renderStepCosts(costs) {
    const box = el('step-costs');
    box.innerHTML = '';
    (costs || []).forEach(c => addStepCostRow(c));
    sortStepCostRows();
    updateStepCostSummary();
}

function addStepCostRow(step) {
    step = step || defaultStepCost(nextStepDrawNumber());
    const row = document.createElement('div');
    row.className = 'step-cost-row';
    row.innerHTML = `<div class="step-draw-cell"><span>第</span><input class="step-draw" type="number" min="1" value="${step.drawNumber || 1}" title="第几抽" /><span>抽</span></div>
        <label class="step-ticket step-price-line"><span>消耗抽奖券</span><input class="step-ticket-amount" type="number" min="1" value="${step.cost?.ticketAmount || 1}" title="券数" /><span>张</span></label>
        <div class="step-stash hidden">
            <div class="step-stash-caption">本抽扣除仓库物品</div>
            <div class="step-stash-editor"></div>
        </div>
        <div class="step-row-actions">
            <button class="mini step-copy-prev" type="button" title="复制上一抽价格">复制上抽</button>
            <button class="mini del" type="button" title="删除这一抽价格">×</button>
        </div>`;
    el('step-costs').appendChild(row);
    row._stashEditor = BpPrice.createEditor(row.querySelector('.step-stash-editor'));
    row._stashEditor.setCost(step.cost?.stashItems || []);
    row.querySelector('.step-copy-prev').onclick = () => copyPreviousStepCost(row);
    row.querySelector('.del').onclick = () => { row.remove(); updateStepCostSummary(); };
    row.querySelector('.step-draw').addEventListener('change', () => { sortStepCostRows(); updateStepCostSummary(); });
    row.querySelector('.step-ticket-amount').addEventListener('input', updateStepCostSummary);
    updateCostVisibility();
    return row;
}

function getStepRows() {
    return Array.from(document.querySelectorAll('#step-costs .step-cost-row'));
}

function getPrizeCount() {
    return el('prize-list').querySelectorAll('.prize-card').length;
}

function defaultStepCost(drawNumber) {
    const costType = el('p-cost-type').value;
    if (costType === 'stashItems') {
        const collected = singleStashEditor?.collect();
        return {
            drawNumber,
            cost: {
                costType,
                ticketAmount: 0,
                stashItems: collected?.cost || [],
            },
        };
    }

    return {
        drawNumber,
        cost: {
            costType,
            ticketAmount: Math.max(1, +el('p-ticket-single').value || 1),
            stashItems: [],
        },
    };
}

function nextStepDrawNumber() {
    const used = new Set(getStepRows().map(row => +row.querySelector('.step-draw').value || 0));
    const prizeCount = Math.max(1, getPrizeCount());
    for (let i = 1; i <= prizeCount; i++) {
        if (!used.has(i)) return i;
    }
    return Math.max(0, ...Array.from(used)) + 1;
}

function sortStepCostRows() {
    const box = el('step-costs');
    getStepRows()
        .sort((a, b) => (+a.querySelector('.step-draw').value || 0) - (+b.querySelector('.step-draw').value || 0))
        .forEach(row => box.appendChild(row));
}

function collectStepCostFromRow(row) {
    const costType = el('p-cost-type').value;
    const drawNumber = +row.querySelector('.step-draw').value || 0;
    if (costType === 'stashItems') {
        const collected = row._stashEditor?.collect();
        return {
            drawNumber,
            invalid: !collected || collected.invalid || collected.cost.length === 0,
            cost: { costType, ticketAmount: 0, stashItems: collected?.cost || [] },
        };
    }

    const ticketAmount = +row.querySelector('.step-ticket-amount').value || 0;
    return {
        drawNumber,
        invalid: ticketAmount <= 0,
        cost: { costType, ticketAmount, stashItems: [] },
    };
}

function copyPreviousStepCost(row) {
    const rows = getStepRows();
    const index = rows.indexOf(row);
    const source = rows[index - 1];
    if (!source) {
        toast('没有上一抽价格可复制', false);
        return;
    }

    const costType = el('p-cost-type').value;
    if (costType === 'stashItems') {
        const collected = source._stashEditor?.collect();
        if (!collected || collected.invalid || collected.cost.length === 0) {
            toast('上一抽仓库物品价格无效', false);
            return;
        }
        row._stashEditor.setCost(JSON.parse(JSON.stringify(collected.cost)));
    } else {
        row.querySelector('.step-ticket-amount').value = source.querySelector('.step-ticket-amount').value || 1;
    }
    updateStepCostSummary();
    toast('已复制上一抽价格', true);
}

function generateTicketStepCosts() {
    if (el('p-cost-type').value !== 'lotteryTickets') {
        toast('当前代价类型不是抽奖券', false);
        return;
    }

    const count = getPrizeCount();
    if (count <= 0) {
        toast('请先添加奖项，再生成每抽价格', false);
        return;
    }

    const start = Math.max(1, +el('step-ticket-start').value || 1);
    const inc = Math.max(0, +el('step-ticket-increment').value || 0);
    const costs = [];
    for (let i = 1; i <= count; i++) {
        costs.push({
            drawNumber: i,
            cost: {
                costType: 'lotteryTickets',
                ticketAmount: start + (i - 1) * inc,
                stashItems: [],
            },
        });
    }
    renderStepCosts(costs);
    toast('已按奖项数生成每抽券价', true);
}

function fillStepCostsFromSingle() {
    const count = getPrizeCount();
    if (count <= 0) {
        toast('请先添加奖项，再填充每抽价格', false);
        return;
    }

    const costType = el('p-cost-type').value;
    let baseCost;
    if (costType === 'stashItems') {
        const collected = singleStashEditor.collect();
        if (collected.invalid || collected.cost.length === 0) {
            toast('基础单抽仓库物品代价无效', false);
            return;
        }
        baseCost = { costType, ticketAmount: 0, stashItems: collected.cost };
    } else {
        baseCost = {
            costType,
            ticketAmount: Math.max(1, +el('p-ticket-single').value || 1),
            stashItems: [],
        };
    }

    const costs = [];
    for (let i = 1; i <= count; i++) {
        costs.push({ drawNumber: i, cost: JSON.parse(JSON.stringify(baseCost)) });
    }
    renderStepCosts(costs);
    toast('已用基础单抽价填满每抽价格表', true);
}

function syncStepCostsToPrizeCount() {
    const count = getPrizeCount();
    if (count <= 0) {
        toast('请先添加奖项，再补齐价格表', false);
        return;
    }

    const existing = new Map();
    getStepRows().forEach(row => {
        const item = collectStepCostFromRow(row);
        if (item.drawNumber > 0 && item.drawNumber <= count && !existing.has(item.drawNumber)) {
            existing.set(item.drawNumber, item.cost);
        }
    });

    let previous = null;
    const costs = [];
    for (let i = 1; i <= count; i++) {
        const found = existing.get(i);
        const cost = found || previous || defaultStepCost(i).cost;
        costs.push({ drawNumber: i, cost: JSON.parse(JSON.stringify(cost)) });
        previous = cost;
    }
    renderStepCosts(costs);
    toast('已补齐到当前奖项数量', true);
}

function clearStepCosts() {
    el('step-costs').innerHTML = '';
    updateStepCostSummary();
    toast('已清空每抽价格表', true);
}

function updateStepCostSummary() {
    const summary = el('step-cost-summary');
    if (!summary) return;

    const poolType = el('p-type').value;
    const count = getPrizeCount();
    const rows = getStepRows();
    const numbers = rows.map(row => +row.querySelector('.step-draw').value || 0).filter(n => n > 0);
    const unique = new Set(numbers);
    const invalidLines = rows
        .map(row => collectStepCostFromRow(row))
        .filter(item => item.drawNumber <= 0 || item.invalid)
        .map(item => item.drawNumber || '?');
    const missing = [];
    for (let i = 1; i <= count; i++) {
        if (!unique.has(i)) missing.push(i);
    }
    const duplicates = numbers.filter((n, index) => numbers.indexOf(n) !== index);
    const overflow = numbers.filter(n => count > 0 && n > count);

    summary.classList.remove('ok', 'warn', 'err');
    if (poolType !== 'nonRepeatable') {
        summary.classList.add('warn');
        summary.textContent = `当前是可重复池：每抽价格表不会参与扣费，保存时将忽略这些配置。`;
        return;
    }

    if (count <= 0) {
        summary.classList.add('err');
        summary.textContent = '不可重复池需要先添加奖项，然后配置同等数量的每抽价格。';
        return;
    }

    if (missing.length === 0 && duplicates.length === 0 && overflow.length === 0 && invalidLines.length === 0 && rows.length === count) {
        summary.classList.add('ok');
        summary.textContent = `配置完整：当前 ${count} 个奖项，对应第 1-${count} 抽价格。`;
        return;
    }

    summary.classList.add('err');
    const parts = [`当前 ${count} 个奖项，需要 ${count} 行连续价格，已配置 ${rows.length} 行。`];
    if (missing.length) parts.push(`缺少第 ${missing.join('、')} 抽。`);
    if (duplicates.length) parts.push(`重复配置第 ${Array.from(new Set(duplicates)).join('、')} 抽。`);
    if (overflow.length) parts.push(`超出奖项数的抽次：第 ${Array.from(new Set(overflow)).join('、')} 抽。`);
    if (invalidLines.length) parts.push(`价格无效：第 ${Array.from(new Set(invalidLines)).join('、')} 抽。`);
    summary.textContent = parts.join(' ');
}

function collectStepCosts() {
    if (el('p-type').value !== 'nonRepeatable') {
        return { steps: [], invalid: false, message: '' };
    }

    const costType = el('p-cost-type').value;
    const steps = [];
    let invalid = false;
    let message = '';
    getStepRows().forEach(row => {
        const item = collectStepCostFromRow(row);
        if (item.drawNumber <= 0 || item.invalid) { invalid = true; return; }
        steps.push({ drawNumber: item.drawNumber, cost: item.cost });
    });
    const prizeCount = getPrizeCount();
    const numbers = steps.map(s => s.drawNumber);
    const unique = new Set(numbers);
    const missing = [];
    for (let i = 1; i <= prizeCount; i++) {
        if (!unique.has(i)) missing.push(i);
    }
    if (prizeCount <= 0) {
        invalid = true;
        message = '不可重复池需要先配置奖项';
    } else if (steps.length !== prizeCount || unique.size !== steps.length || missing.length || numbers.some(n => n > prizeCount)) {
        invalid = true;
        message = '不可重复池每抽价格必须从第 1 抽连续配置到奖项数量';
    }

    steps.sort((a, b) => a.drawNumber - b.drawNumber);
    return { steps, invalid, message };
}

function prizeRowHtml(prize) {
    prize = prize || { id: '', reward: { type: 'item', count: 1 }, weight: 1, convertDuplicateToExchangeCoin: true };
    const r = prize.reward || {};
    const type = r.type || 'item';
    return `<div class="prize-card">
        <div class="prize-head">
            <input class="pr-id" value="${esc(prize.id || '')}" placeholder="奖项 ID，留空自动生成" />
            <input class="pr-name" value="${esc(r.name || '')}" placeholder="展示名" />
            <select class="pr-type">
                <option value="item" ${type === 'item' ? 'selected' : ''}>物品</option>
                <option value="purchaseRight" ${type === 'purchaseRight' ? 'selected' : ''}>购买权</option>
                <option value="recipe" ${type === 'recipe' ? 'selected' : ''}>配方</option>
                <option value="title" ${type === 'title' ? 'selected' : ''}>称号</option>
                <option value="clothing" ${type === 'clothing' ? 'selected' : ''}>服装</option>
            </select>
            <button class="mini del pr-del" type="button">×</button>
        </div>
        <div class="pr-fields">
            <div class="pr-row pr-item-row">
                <input class="pr-tpl" value="${esc(r.tpl || '')}" placeholder="搜索物品名称或 tpl…" autocomplete="off" />
                <input class="pr-count" type="number" min="1" value="${r.count || 1}" />
                <label class="with-cb"><input class="pr-fir" type="checkbox" ${r.foundInRaid ? 'checked' : ''} /> FIR</label>
                <div class="pr-tpl-results rw-tpl-results" style="display:none"></div>
            </div>
            <div class="pr-row pr-offer-row"><input class="pr-offer" value="${esc(r.offerId || '')}" placeholder="搜索购买权 offer…" autocomplete="off" /><div class="pr-offer-results rw-tpl-results" style="display:none"></div></div>
            <div class="pr-row pr-recipe-row"><input class="pr-recipe" value="${esc(r.recipeId || '')}" placeholder="搜索配方…" autocomplete="off" /><div class="pr-recipe-results rw-tpl-results" style="display:none"></div></div>
            <div class="pr-row pr-title-row"><input class="pr-title" value="${esc(r.titleId || '')}" placeholder="搜索称号…" autocomplete="off" /><div class="pr-title-results rw-tpl-results" style="display:none"></div></div>
            <div class="pr-row pr-clothing-row"><input class="pr-suit" value="${esc(r.suitId || '')}" placeholder="搜索服装、suiteId、offerId…" autocomplete="off" /><div class="pr-suit-results rw-tpl-results" style="display:none"></div></div>
        </div>
        <div class="pr-meta">
            <label>权重<input class="pr-weight" type="number" min="1" value="${prize.weight || 1}" /></label>
            <label>稀有度<select class="pr-rarity">
                <option value="common" ${(prize.rarity || 'common') === 'common' ? 'selected' : ''}>普通</option>
                <option value="rare" ${prize.rarity === 'rare' ? 'selected' : ''}>稀有</option>
                <option value="epic" ${prize.rarity === 'epic' ? 'selected' : ''}>史诗</option>
                <option value="legendary" ${prize.rarity === 'legendary' ? 'selected' : ''}>传说</option>
            </select></label>
            <label class="with-cb"><input class="pr-grand" type="checkbox" ${prize.isGrandPrize ? 'checked' : ''} /> 大奖</label>
            <label class="with-cb"><input class="pr-broadcast" type="checkbox" ${prize.broadcastWhenWon ? 'checked' : ''} /> 轮播</label>
            <label class="with-cb"><input class="pr-convert" type="checkbox" ${prize.convertDuplicateToExchangeCoin !== false ? 'checked' : ''} /> 重复转兑换币</label>
            <label>转换币数<input class="pr-coin" type="number" min="0" value="${prize.duplicateExchangeCoinAmount || 0}" /></label>
        </div>
    </div>`;
}

function renderPrizeRows(prizes) {
    const box = el('prize-list');
    box.innerHTML = (prizes || []).map(prizeRowHtml).join('');
    box.querySelectorAll('.prize-card').forEach(wirePrizeCard);
    el('p-prize-count').value = box.querySelectorAll('.prize-card').length;
    updateStepCostSummary();
}

function wirePrizeCard(card) {
    const typeSel = card.querySelector('.pr-type');
    const apply = () => {
        const type = typeSel.value;
        ['.pr-item-row', '.pr-offer-row', '.pr-recipe-row', '.pr-title-row', '.pr-clothing-row'].forEach(s => card.querySelector(s).classList.add('hidden'));
        const map = { item: '.pr-item-row', purchaseRight: '.pr-offer-row', recipe: '.pr-recipe-row', title: '.pr-title-row', clothing: '.pr-clothing-row' };
        card.querySelector(map[type]).classList.remove('hidden');
    };
    typeSel.onchange = apply;
    card.querySelector('.pr-del').onclick = () => {
        card.remove();
        el('p-prize-count').value = el('prize-list').querySelectorAll('.prize-card').length;
        updateStepCostSummary();
    };
    BpPicker.attachItem(card.querySelector('.pr-tpl'), card.querySelector('.pr-tpl-results'), ds => card.querySelector('.pr-tpl').value = ds.tpl);
    BpPicker.attachOffer(card.querySelector('.pr-offer'), card.querySelector('.pr-offer-results'), ds => card.querySelector('.pr-offer').value = ds.id, { limit: 8, minChars: 0 });
    BpPicker.attachRecipe(card.querySelector('.pr-recipe'), card.querySelector('.pr-recipe-results'), ds => card.querySelector('.pr-recipe').value = ds.id, { limit: 8, minChars: 2 });
    BpPicker.attachTitle(card.querySelector('.pr-title'), card.querySelector('.pr-title-results'), ds => card.querySelector('.pr-title').value = ds.id, { limit: 8, minChars: 0 });
    BpPicker.attachClothing(card.querySelector('.pr-suit'), card.querySelector('.pr-suit-results'), ds => {
        card.querySelector('.pr-suit').value = ds.suitId || ds.id;
        const nameInput = card.querySelector('.pr-name');
        if (ds.name && nameInput && !nameInput.value.trim()) nameInput.value = ds.name;
    }, { limit: 8, minChars: 0 });
    apply();
}

function collectPrizes() {
    const prizes = [];
    document.querySelectorAll('#prize-list .prize-card').forEach(card => {
        const type = card.querySelector('.pr-type').value;
        const id = card.querySelector('.pr-id').value.trim() || newId('prize');
        const reward = {
            type,
            name: card.querySelector('.pr-name').value.trim() || null,
            count: type === 'item' ? Math.max(1, +card.querySelector('.pr-count').value || 1) : 1,
            tpl: type === 'item' ? card.querySelector('.pr-tpl').value.trim() : null,
            foundInRaid: type === 'item' ? card.querySelector('.pr-fir').checked : false,
            offerId: type === 'purchaseRight' ? card.querySelector('.pr-offer').value.trim() : null,
            recipeId: type === 'recipe' ? card.querySelector('.pr-recipe').value.trim() : null,
            titleId: type === 'title' ? card.querySelector('.pr-title').value.trim() : null,
            suitId: type === 'clothing' ? card.querySelector('.pr-suit').value.trim() : null,
        };
        prizes.push({
            id,
            reward,
            weight: Math.max(1, +card.querySelector('.pr-weight').value || 1),
            rarity: card.querySelector('.pr-rarity').value || 'common',
            isGrandPrize: card.querySelector('.pr-grand').checked,
            broadcastWhenWon: card.querySelector('.pr-broadcast').checked,
            convertDuplicateToExchangeCoin: card.querySelector('.pr-convert').checked,
            duplicateExchangeCoinAmount: Math.max(0, +card.querySelector('.pr-coin').value || 0),
        });
    });
    return prizes;
}

function collectPool(forceDraft) {
    const costType = el('p-cost-type').value;
    const singleCost = { costType, ticketAmount: 0, stashItems: [] };
    if (costType === 'stashItems') {
        const collected = singleStashEditor.collect();
        if (collected.invalid) return { invalid: true, message: '单抽仓库物品代价无效' };
        singleCost.stashItems = collected.cost;
    } else {
        singleCost.ticketAmount = Math.max(1, +el('p-ticket-single').value || 1);
    }
    const stepCollected = collectStepCosts();
    if (stepCollected.invalid) return { invalid: true, message: stepCollected.message || '每抽价格表存在无效配置' };

    const tenAmount = Math.max(0, +el('p-ticket-ten').value || 0);
    const pool = {
        ...(currentPool || defaultPool()),
        id: el('p-id').value.trim(),
        name: el('p-name').value.trim(),
        description: el('p-desc').value.trim() || null,
        iconUrl: el('p-icon').value.trim() || null,
        coverUrl: el('p-cover').value.trim() || null,
        sortOrder: +el('p-sort').value || 0,
        enabled: el('p-enabled').checked,
        visibleToPlayers: el('p-visible').checked,
        status: forceDraft ? 'draft' : el('p-status').value,
        pauseVisibleToPlayers: el('p-pause-visible').checked,
        followSeason: el('p-follow-season').checked,
        startUtc: toUnixSec(el('p-start').value),
        endUtc: toUnixSec(el('p-end').value),
        poolType: el('p-type').value,
        probabilityMode: el('p-prob').value,
        costType,
        singleCost,
        tenDrawCostOverride: costType === 'lotteryTickets' && tenAmount > 0
            ? { costType: 'lotteryTickets', ticketAmount: tenAmount, stashItems: [] }
            : null,
        stepCosts: stepCollected.steps,
        pityEnabled: el('p-pity-enabled').checked,
        pityCount: Math.max(0, +el('p-pity-count').value || 0),
        prizes: collectPrizes(),
    };
    return { pool };
}

async function savePool(forceDraft) {
    const collected = collectPool(forceDraft);
    if (collected.invalid) return toast(collected.message, false);
    if (await submitAsCollaborator('lottery.pool.upsert', collected.pool)) return;
    const r = await api('/pools', 'POST', collected.pool);
    if (!r.success) return toast(r.message || '保存失败', false);
    currentPool = r.pool;
    el('pool-validation').textContent = (r.validation && r.validation.length) ? '校验：' + r.validation.join('；') : '校验通过';
    toast(forceDraft ? '已暂存草稿' : '奖池已保存', true);
    await loadPools();
    fillPool(r.pool);
}

async function poolAction(action, body) {
    if (isCollaborator()) return toast('奖池发布 / 暂停 / 结束 / 归档仅管理员可用', false);
    if (!currentPool?.id) return toast('请先保存奖池', false);
    const r = await api('/pools/' + encodeURIComponent(currentPool.id) + '/' + action, 'POST', body);
    toast(r.success ? '操作成功' : (r.message || '操作失败'), r.success);
    if (r.errors) el('pool-validation').textContent = '校验：' + r.errors.join('；');
    if (r.success) { await loadPools(); if (r.pool) fillPool(r.pool); }
}

async function deletePool() {
    if (!currentPool?.id) return toast('请先选择奖池', false);
    const name = currentPool.name || currentPool.id;
    if (isCollaborator()) {
        if (!confirm(`确定提交删除奖池「${name}」的审核申请？批准后生效。`)) return;
        await submitAsCollaborator('lottery.pool.delete', { id: currentPool.id });
        return;
    }
    if (!confirm(`确定永久删除奖池「${name}」？\n\n该操作会删除奖池配置，并清理该奖池独占的本地上传图标/展示图。抽奖历史记录不会删除。`)) {
        return;
    }

    const r = await api('/pools/' + encodeURIComponent(currentPool.id), 'DELETE');
    if (!r.success) {
        toast(r.message || '删除失败', false);
        return;
    }

    toast(`已删除奖池，清理图片 ${r.deletedImages || 0} 个`, true);
    await loadPools();
    fillPool(pools[0] || defaultPool());
}

async function resetPoolProgress(all) {
    if (isCollaborator()) return toast('抽奖进度重置仅管理员可用', false);
    if (!currentPool?.id) return toast('请先保存奖池', false);
    const body = all ? { all: true } : { profileId: el('reset-profile-id').value.trim() };
    if (!all && !body.profileId) return toast('请填写 profileId', false);
    if (all && !confirm('确定重置所有玩家在该奖池的抽取进度？')) return;
    const r = await api('/pools/' + encodeURIComponent(currentPool.id) + '/reset-progress', 'POST', body);
    toast(r.success ? `已重置 ${r.reset || 0} 份进度` : (r.message || '重置失败'), r.success);
}

async function copyPool() {
    if (isCollaborator()) return toast('复制奖池草稿仅管理员可用', false);
    if (!currentPool?.id) return toast('请先保存奖池', false);
    const r = await api('/pools/' + encodeURIComponent(currentPool.id) + '/copy-draft', 'POST');
    toast(r.success ? '已复制为草稿' : (r.message || '复制失败'), r.success);
    if (r.success) { await loadPools(); fillPool(r.pool); }
}

function createRewardMiniEditor(container, reward) {
    reward = reward || { type: 'item', count: 1 };
    container.innerHTML = `<div class="reward-mini">
        <div class="form-row three-col">
            <div class="form-col"><label>奖励类型</label><select class="rm-type"><option value="item">物品</option><option value="purchaseRight">购买权</option><option value="recipe">配方</option><option value="title">称号</option><option value="clothing">服装</option></select></div>
            <div class="form-col grow"><label>展示名</label><input class="rm-name" value="${esc(reward.name || '')}" /></div>
            <div class="form-col"><label>数量</label><input class="rm-count" type="number" min="1" value="${reward.count || 1}" /></div>
        </div>
        <div class="rm-row rm-item tpl-row"><input class="rm-tpl" value="${esc(reward.tpl || '')}" placeholder="搜索物品名称或 tpl…" autocomplete="off" /><div class="rm-tpl-results rw-tpl-results" style="display:none"></div></div>
        <div class="rm-row rm-offer"><input class="rm-offer-id" value="${esc(reward.offerId || '')}" placeholder="搜索购买权 offer…" autocomplete="off" /><div class="rm-offer-results rw-tpl-results" style="display:none"></div></div>
        <div class="rm-row rm-recipe"><input class="rm-recipe-id" value="${esc(reward.recipeId || '')}" placeholder="搜索配方…" autocomplete="off" /><div class="rm-recipe-results rw-tpl-results" style="display:none"></div></div>
        <div class="rm-row rm-title"><input class="rm-title-id" value="${esc(reward.titleId || '')}" placeholder="搜索称号…" autocomplete="off" /><div class="rm-title-results rw-tpl-results" style="display:none"></div></div>
        <div class="rm-row rm-clothing"><input class="rm-suit-id" value="${esc(reward.suitId || '')}" placeholder="搜索服装、suiteId、offerId…" autocomplete="off" /><div class="rm-suit-results rw-tpl-results" style="display:none"></div></div>
    </div>`;
    const typeSel = container.querySelector('.rm-type');
    typeSel.value = reward.type || 'item';
    const apply = () => {
        const type = typeSel.value;
        ['.rm-item', '.rm-offer', '.rm-recipe', '.rm-title', '.rm-clothing'].forEach(s => container.querySelector(s).classList.add('hidden'));
        const map = { item: '.rm-item', purchaseRight: '.rm-offer', recipe: '.rm-recipe', title: '.rm-title', clothing: '.rm-clothing' };
        container.querySelector(map[type]).classList.remove('hidden');
        container.querySelector('.rm-count').disabled = type !== 'item';
    };
    typeSel.onchange = apply;
    BpPicker.attachItem(container.querySelector('.rm-tpl'), container.querySelector('.rm-tpl-results'), ds => container.querySelector('.rm-tpl').value = ds.tpl);
    BpPicker.attachOffer(container.querySelector('.rm-offer-id'), container.querySelector('.rm-offer-results'), ds => container.querySelector('.rm-offer-id').value = ds.id, { limit: 8, minChars: 0 });
    BpPicker.attachRecipe(container.querySelector('.rm-recipe-id'), container.querySelector('.rm-recipe-results'), ds => container.querySelector('.rm-recipe-id').value = ds.id, { limit: 8, minChars: 2 });
    BpPicker.attachTitle(container.querySelector('.rm-title-id'), container.querySelector('.rm-title-results'), ds => container.querySelector('.rm-title-id').value = ds.id, { limit: 8, minChars: 0 });
    BpPicker.attachClothing(container.querySelector('.rm-suit-id'), container.querySelector('.rm-suit-results'), ds => {
        container.querySelector('.rm-suit-id').value = ds.suitId || ds.id;
        const nameInput = container.querySelector('.rm-name');
        if (ds.name && nameInput && !nameInput.value.trim()) nameInput.value = ds.name;
    }, { limit: 8, minChars: 0 });
    apply();
}

function readRewardMini(container) {
    const type = container.querySelector('.rm-type').value;
    return {
        type,
        name: container.querySelector('.rm-name').value.trim() || null,
        count: type === 'item' ? Math.max(1, +container.querySelector('.rm-count').value || 1) : 1,
        tpl: type === 'item' ? container.querySelector('.rm-tpl').value.trim() : null,
        offerId: type === 'purchaseRight' ? container.querySelector('.rm-offer-id').value.trim() : null,
        recipeId: type === 'recipe' ? container.querySelector('.rm-recipe-id').value.trim() : null,
        titleId: type === 'title' ? container.querySelector('.rm-title-id').value.trim() : null,
        suitId: type === 'clothing' ? container.querySelector('.rm-suit-id').value.trim() : null,
    };
}

function defaultShopItem() {
    return {
        id: '',
        name: '',
        description: '',
        iconUrl: '',
        status: 'draft',
        enabled: true,
        visibleToPlayers: true,
        sortOrder: 0,
        startUtc: 0,
        endUtc: 0,
        price: 1,
        limitType: 'none',
        limitCount: 0,
        reward: { type: 'item', count: 1 },
    };
}

async function loadShop() {
    const r = await api('/shop');
    if (!r.success) return;
    shopItems = r.items || [];
    renderShopList();
}

function renderShopList() {
    const box = el('shop-list');
    if (!shopItems.length) {
        box.innerHTML = '<div class="row-item muted">暂无兑换商品</div>';
        return;
    }
    box.innerHTML = shopItems.map(item => `<div class="row-item lottery-shop-admin-row" data-id="${esc(item.id)}">
        <div><div class="offer-title">${esc(item.name || item.id)} <span class="badge">${esc(item.status || 'draft')}</span></div>
        <div class="meta">${esc(item.id)} · 兑换币 ${item.price || 0} · ${esc(item.limitType || 'none')}</div></div>
        <button class="mini">编辑</button>
    </div>`).join('');
    box.querySelectorAll('.lottery-shop-admin-row').forEach(row => {
        row.onclick = () => fillShopItem(shopItems.find(x => x.id === row.dataset.id));
    });
}

function fillShopItem(item) {
    currentShopItem = JSON.parse(JSON.stringify(item || defaultShopItem()));
    el('shop-editor-title').textContent = currentShopItem.id ? '编辑兑换商品' : '新增兑换商品';
    el('s-id').value = currentShopItem.id || '';
    el('s-name').value = currentShopItem.name || '';
    el('s-desc').value = currentShopItem.description || '';
    el('s-icon').value = currentShopItem.iconUrl || '';
    el('s-sort').value = currentShopItem.sortOrder || 0;
    el('s-status').value = currentShopItem.status || 'draft';
    el('s-enabled').checked = currentShopItem.enabled !== false;
    el('s-visible').checked = currentShopItem.visibleToPlayers !== false;
    el('s-price').value = currentShopItem.price || 1;
    el('s-limit-type').value = currentShopItem.limitType || 'none';
    el('s-limit-count').value = currentShopItem.limitCount || 0;
    el('s-start').value = toLocalDatetimeStr(currentShopItem.startUtc);
    el('s-end').value = toLocalDatetimeStr(currentShopItem.endUtc);
    createRewardMiniEditor(el('shop-reward-editor'), currentShopItem.reward);
}

function newShopItem() { fillShopItem(defaultShopItem()); }

async function saveShopItem(forceDraft) {
    const item = {
        ...(currentShopItem || defaultShopItem()),
        id: el('s-id').value.trim(),
        name: el('s-name').value.trim(),
        description: el('s-desc').value.trim() || null,
        iconUrl: el('s-icon').value.trim() || null,
        sortOrder: +el('s-sort').value || 0,
        status: forceDraft ? 'draft' : el('s-status').value,
        enabled: el('s-enabled').checked,
        visibleToPlayers: el('s-visible').checked,
        price: Math.max(1, +el('s-price').value || 1),
        limitType: el('s-limit-type').value,
        limitCount: Math.max(0, +el('s-limit-count').value || 0),
        startUtc: toUnixSec(el('s-start').value),
        endUtc: toUnixSec(el('s-end').value),
        reward: readRewardMini(el('shop-reward-editor')),
    };
    if (isCollaborator()) { await submitAsCollaborator('lottery.shop.upsert', item); return null; }
    const r = await api('/shop', 'POST', item);
    toast(r.success ? (forceDraft ? '商品已暂存' : '商品已保存') : (r.message || '保存失败'), r.success);
    if (r.success) {
        currentShopItem = r.item;
        await loadShop();
        fillShopItem(r.item);
        return r.item;
    }
    return null;
}

function applyPendingLotteryEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    const payload = change.proposedPayload || {};
    if (change.commandType === 'lottery.settings') {
        switchSection('config');
        fillSettings(payload);
    } else if (change.commandType === 'lottery.pool.upsert') {
        switchSection('pools');
        fillPool(payload);
    } else if (change.commandType === 'lottery.shop.upsert') {
        switchSection('shop');
        fillShopItem(payload);
    } else if (change.commandType.includes('.delete')) {
        toast('请选择新的目标并执行删除，以更新原删除审核单', true);
    }
}

async function publishShopItem() {
    if (isCollaborator()) return toast('商品发布仅管理员可用（可提交上架申请待审核）', false);
    const item = await saveShopItem(false);
    if (!item?.id) return;
    const r = await api('/shop/' + encodeURIComponent(item.id) + '/publish', 'POST');
    toast(r.success ? '商品已发布' : (r.message || '发布失败'), r.success);
    if (r.success) { await loadShop(); fillShopItem(r.item); }
}

async function loadRecords() {
    const r = await api('/records');
    if (!r.success) return;
    const records = (r.records || []).slice(0, 160);
    const box = el('record-rows');
    box.innerHTML = records.length ? records.map(x => `<div class="row-item">
        <div><div class="offer-title">${esc(x.prizeNameSnapshot || x.prizeId)} ${x.isGrandPrize ? '<span class="badge">大奖</span>' : ''}</div>
        <div class="meta">${esc(x.nicknameSnapshot || x.profileId)} · ${esc(x.poolNameSnapshot || x.poolId)} · ${fmtTime(x.createdUtc)}${x.convertedToExchangeCoin ? ` · 转换 ${x.exchangeCoinAmount || 0} 币` : ''}</div></div>
    </div>`).join('') : '<div class="row-item muted">暂无记录</div>';
}

async function exportRecords() {
    const res = await fetch(ADMIN_API + '/records/export', { headers: { 'X-Admin-Token': getAdminToken() } });
    if (!res.ok) return toast('导出失败', false);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'lottery-records.csv';
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
}

// ---- 发放：玩家搜索 + 多选 ----
const grantSelected = new Map(); // profileId -> { nickname, username }

function renderGrantPoolSelect() {
    const sel = el('g-pool-id');
    if (!sel) return;
    const current = sel.value || '';
    const ticketPools = (pools || [])
        .filter(p => p.costType === 'lotteryTickets')
        .sort((a, b) => (a.sortOrder || 0) - (b.sortOrder || 0));
    sel.innerHTML = '<option value="">请选择奖池</option>'
        + ticketPools.map(p => `<option value="${esc(p.id)}">${esc(p.name || p.id)} (${esc(p.id)})</option>`).join('');
    if (current && ticketPools.some(p => p.id === current)) sel.value = current;
}

function renderGrantSelected() {
    el('g-selected-count').textContent = grantSelected.size;
    const box = el('g-selected');
    box.innerHTML = grantSelected.size
        ? [...grantSelected].map(([id, p]) => `<span class="g-chip" data-id="${esc(id)}">${esc(p.nickname || id)}<button type="button" class="g-chip-x">×</button></span>`).join('')
        : '<span class="muted">未选择玩家</span>';
    box.querySelectorAll('.g-chip-x').forEach(btn => {
        btn.onclick = () => { grantSelected.delete(btn.parentElement.dataset.id); renderGrantSelected(); };
    });
}

async function searchGrantPlayers() {
    const q = el('g-search').value.trim();
    const res = await fetch(ADMIN_API + '/players/search' + (q ? '?q=' + encodeURIComponent(q) : ''), { headers: { 'X-Admin-Token': getAdminToken() } });
    const r = await res.json();
    const box = el('g-search-results');
    if (!r.success) { box.innerHTML = `<div class="row-item muted">${esc(r.message || '搜索失败')}</div>`; return; }
    const players = (r.players || []).slice(0, 60);
    box.innerHTML = players.length
        ? players.map(p => `<div class="row-item g-search-row" data-id="${esc(p.profileId)}">
            <div><div class="offer-title">${esc(p.nickname || '(无昵称)')}</div>
            <div class="meta">${esc(p.username || '')} · ${esc(p.profileId)}</div></div>
            <button class="mini" type="button">${grantSelected.has(p.profileId) ? '已选' : '选择'}</button>
        </div>`).join('')
        : '<div class="row-item muted">无匹配玩家</div>';
    box.querySelectorAll('.g-search-row').forEach(row => {
        const id = row.dataset.id;
        const p = players.find(x => x.profileId === id);
        row.querySelector('button').onclick = () => {
            if (grantSelected.has(id)) grantSelected.delete(id);
            else grantSelected.set(id, { nickname: p.nickname, username: p.username });
            renderGrantSelected();
            row.querySelector('button').textContent = grantSelected.has(id) ? '已选' : '选择';
        };
    });
}

function toggleGrantSearch() {
    el('g-search-wrap').classList.toggle('hidden', el('g-all').checked);
}

async function grantCurrency() {
    if (isCollaborator()) return toast('发放抽奖券 / 代币仅管理员可用', false);
    const body = {
        all: el('g-all').checked,
        profileIds: [...grantSelected.keys()],
        globalTickets: Math.max(0, +el('g-global').value || 0),
        poolId: el('g-pool-id').value || null,
        poolTickets: Math.max(0, +el('g-pool').value || 0),
        exchangeCoins: Math.max(0, +el('g-coins').value || 0),
    };
    if (!body.all && body.profileIds.length === 0) { toast('请先搜索并选择玩家', false); return; }
    const r = await api('/grants', 'POST', body);
    toast(r.success ? `已发放给 ${r.count || 0} 个玩家` : (r.message || '发放失败'), r.success);
}

async function loadLogs() {
    const category = el('log-category')?.value || '';
    const r = await api('/audit-logs' + (category ? '?category=' + encodeURIComponent(category) : ''));
    if (!r.success) return;
    const logs = (r.logs || []).slice(0, 120);
    el('audit-rows').innerHTML = logs.length ? logs.map(x => `<div class="row-item">
        <div><div class="offer-title">${esc(x.category)} / ${esc(x.action)}</div>
        <div class="meta">${esc(x.summary || '')} · ${fmtTime(x.createdUtc)}</div></div>
    </div>`).join('') : '<div class="row-item muted">暂无日志</div>';
}

async function loadTransactions() {
    const r = await api('/transactions');
    if (!r.success) return;
    const txs = (r.transactions || []).slice(0, 120);
    el('transaction-rows').innerHTML = txs.length ? txs.map(x => `<div class="row-item">
        <div><div class="offer-title">${esc(x.status)} · ${esc(x.action)} <span class="badge">${esc(x.poolId)}</span></div>
        <div class="meta">${esc(x.id)} · ${esc(x.profileId)} · ${esc(x.message || '')}</div></div>
        <button class="mini mark-tx" data-id="${esc(x.id)}">标记处理</button>
    </div>`).join('') : '<div class="row-item muted">暂无事务</div>';
    el('transaction-rows').querySelectorAll('.mark-tx').forEach(btn => {
        btn.onclick = async e => {
            e.stopPropagation();
            const r2 = await api('/transactions/' + encodeURIComponent(btn.dataset.id) + '/mark-handled', 'POST');
            toast(r2.success ? '已标记' : (r2.message || '失败'), r2.success);
            loadTransactions();
        };
    });
}

document.querySelectorAll('.tabs .tab[data-section]').forEach(btn => btn.onclick = () => switchSection(btn.dataset.section));
el('admin-login-btn').onclick = doAdminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doAdminLogin(); });
el('admin-logout').onclick = logout;
el('save-settings').onclick = saveSettings;
el('new-pool').onclick = () => fillPool(defaultPool());
el('p-cost-type').onchange = updateCostVisibility;
el('p-type').onchange = updateCostVisibility;
el('p-icon').addEventListener('input', renderPoolAssetPreviews);
el('p-cover').addEventListener('input', renderPoolAssetPreviews);
el('p-icon-file').onchange = () => handlePoolAssetUpload('icon');
el('p-cover-file').onchange = () => handlePoolAssetUpload('cover');
el('add-step-cost').onclick = () => addStepCostRow();
el('generate-step-ticket-costs').onclick = generateTicketStepCosts;
el('fill-step-from-single').onclick = fillStepCostsFromSingle;
el('sync-step-count').onclick = syncStepCostsToPrizeCount;
el('clear-step-costs').onclick = clearStepCosts;
el('add-prize').onclick = () => {
    const div = document.createElement('div');
    div.innerHTML = prizeRowHtml({ id: '', reward: { type: 'item', count: 1 }, weight: 1, convertDuplicateToExchangeCoin: true });
    const card = div.firstElementChild;
    el('prize-list').appendChild(card);
    wirePrizeCard(card);
    el('p-prize-count').value = el('prize-list').querySelectorAll('.prize-card').length;
    updateStepCostSummary();
};
el('save-pool').onclick = () => savePool(false);
el('save-pool-draft').onclick = () => savePool(true);
el('publish-pool').onclick = () => poolAction('publish');
el('pause-pool').onclick = () => poolAction('pause', { visible: el('p-pause-visible').checked });
el('end-pool').onclick = () => poolAction('end');
el('archive-pool').onclick = () => poolAction('archive');
el('delete-pool').onclick = deletePool;
el('copy-pool').onclick = copyPool;
el('reset-one-pool').onclick = () => resetPoolProgress(false);
el('reset-all-pool').onclick = () => resetPoolProgress(true);
el('new-shop-item').onclick = newShopItem;
el('save-shop-item').onclick = () => saveShopItem(false);
el('save-shop-draft').onclick = () => saveShopItem(true);
el('publish-shop-item').onclick = publishShopItem;
el('refresh-records').onclick = loadRecords;
el('export-records').onclick = exportRecords;
el('grant-currency').onclick = grantCurrency;
el('g-search-btn').onclick = searchGrantPlayers;
el('g-search').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); searchGrantPlayers(); } });
el('g-all').onchange = toggleGrantSearch;
el('refresh-logs').onclick = loadLogs;
el('log-category').onchange = loadLogs;
el('refresh-transactions').onclick = loadTransactions;

// ---- 启动：协管 #bpsso= 免密落地换会话，或复用已有管理会话；否则显示登录卡/协管 gate ----
bootstrapAdminPage({ moduleCap: 'lottery.read', onReady: async edit => { await enterConsole(); applyPendingLotteryEdit(edit); } });
