// ---- 审核管理页 ----
'use strict';

const ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';
const API_BASE = '/battlepass/api/admin/reviews';
const MODULE_NAMES = { shop: '商店', tasks: '任务', tracks: '奖励轨', lottery: '抽奖', trader: '商人', recipes: '配方', items: '物品管控', quests: '商人任务', flea: '跳蚤黑名单' };

let currentModule = '';  // 空=全部
let reviewItems = [];
let selectedIds = new Set();
let itemNameMap = {};    // tpl → 中文名（由详情接口 itemNames 提供，用于显示物品名而非裸 MongoId）
let activeDetailId = '';
let activeDetailVersion = 0;
const mobileReviewLayout = window.matchMedia('(max-width: 900px)');

function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
function el(id) { return document.getElementById(id); }

async function api(path, opts = {}) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN };
    const res = await fetch(API_BASE + path, { headers, ...opts });
    return res.json();
}

// ---- 加载与渲染 ----

async function loadSummary() {
    const r = await api('/summary');
    if (!r.success) return {};
    return r.byModule || {};
}

async function loadReviews() {
    const params = new URLSearchParams();
    if (currentModule) params.set('module', currentModule);
    params.set('status', 'pending');
    params.set('limit', '100');
    const r = await api('?' + params.toString());
    reviewItems = r.success ? (r.items || []) : [];
    selectedIds.clear();
    renderList();
    renderBatch();
}

async function init() {
    const summary = await loadSummary();
    renderTabs(summary);
    await loadReviews();
}

function renderTabs(summary) {
    const nav = el('review-tabs');
    const total = Object.values(summary).reduce((a, b) => a + b, 0);
    let html = `<button class="review-tab${currentModule === '' ? ' active' : ''}" data-mod="">全部${total ? ` <span class="badge">${total}</span>` : ''}</button>`;
    for (const [mod, name] of Object.entries(MODULE_NAMES)) {
        const count = summary[mod] || 0;
        html += `<button class="review-tab${currentModule === mod ? ' active' : ''}" data-mod="${mod}">${name}${count ? ` <span class="badge">${count}</span>` : ''}</button>`;
    }
    nav.innerHTML = html;
    nav.querySelectorAll('.review-tab').forEach(btn => {
        btn.onclick = () => { currentModule = btn.dataset.mod; init(); };
    });
}

function renderList() {
    const box = el('review-list');
    const detail = el('review-detail');
    if (box.contains(detail)) el('review-detail-host').appendChild(detail);
    if (!reviewItems.length) { box.innerHTML = '<div class="empty-msg">暂无待审核变更</div>'; return; }

    box.innerHTML = reviewItems.map(item => {
        const mod = MODULE_NAMES[item.module] || item.module;
        const opClass = item.operation === 'delete' ? ' delete' : '';
        const opLabel = { create: '新增', update: '编辑', delete: '删除' }[item.operation] || item.operation;
        const time = new Date(item.createdUtc * 1000).toLocaleString('zh-CN');
        const checked = selectedIds.has(item.id) ? ' checked' : '';
        const selectedClass = item.id === activeDetailId ? ' selected' : '';
        return `<div class="review-item${selectedClass}" data-id="${esc(item.id)}">
            <div class="ri-head">
                <input type="checkbox" class="ri-check"${checked} />
                <span class="ri-module">${esc(mod)}</span>
                <span class="ri-op${opClass}">${esc(opLabel)}</span>
                <strong>${esc(item.targetDisplayName || item.targetId || '')}</strong>
            </div>
            <div class="ri-summary">${esc(item.summary || '')}</div>
            <div class="ri-meta">由 ${esc(item.actor?.displayName || '')} 提交 · ${time}</div>
        </div>`;
    }).join('');

    box.querySelectorAll('.review-item').forEach(card => {
        const id = card.dataset.id;
        card.querySelector('.ri-check').onclick = (e) => {
            e.stopPropagation();
            if (e.target.checked) selectedIds.add(id); else selectedIds.delete(id);
            renderBatch();
        };
        card.onclick = (e) => { if (e.target.type !== 'checkbox') showDetail(id); };
    });
    if (activeDetailId && detail.style.display !== 'none') positionDetail(activeDetailId);
}

function renderBatch() {
    const batchEl = el('review-batch');
    batchEl.style.display = reviewItems.length ? '' : 'none';
    const all = reviewItems.length > 0 && selectedIds.size === reviewItems.length;
    const selectAll = el('select-all');
    selectAll.checked = all;
    selectAll.indeterminate = selectedIds.size > 0 && !all;
    el('selected-count').textContent = `已选 ${selectedIds.size} 条`;
    el('batch-approve').disabled = selectedIds.size === 0;
    el('batch-reject').disabled = selectedIds.size === 0;
}

function positionDetail(id) {
    const detail = el('review-detail');
    const cards = Array.from(document.querySelectorAll('.review-item'));
    const card = cards.find(node => node.dataset.id === id);
    cards.forEach(node => node.classList.toggle('selected', node.dataset.id === id));
    if (mobileReviewLayout.matches && card) card.after(detail);
    else el('review-detail-host').appendChild(detail);
}

function closeDetail() {
    activeDetailId = '';
    activeDetailVersion = 0;
    const detail = el('review-detail');
    detail.style.display = 'none';
    el('review-detail-host').appendChild(detail);
    document.querySelectorAll('.review-item').forEach(node => node.classList.remove('selected'));
}

// ---- 易读渲染 ----
const TYPE_LABELS = { item: '物品', purchaseRight: '商人权益', recipe: '配方', title: '称号', clothing: '服装', lotteryGlobalTickets: '通用抽奖券', lotteryPoolTickets: '奖池券', lotteryExchangeCoins: '兑换币' };
const STATUS_LABELS = { pending: '待审核', approved: '已批准', rejected: '已驳回', applied: '已生效' };
const FIELD_LABELS = {
    id: 'ID', seasonId: '赛季', name: '名称', displayName: '名称', title: '标题', label: '标签',
    desc: '描述', description: '描述', note: '备注', text: '文本',
    tpl: '物品模板', count: '数量', amount: '数量', qty: '数量', quantity: '数量',
    price: '价格', cost: '花费', currency: '货币', currencyTpl: '货币',
    type: '类型', kind: '种类', enabled: '启用', disabled: '禁用', active: '启用',
    level: '等级', minLevel: '最低等级', maxLevel: '等级上限', xp: '经验', exp: '经验', cycleXp: '循环经验',
    free: '免费轨', premium: '付费轨', reward: '奖励', rewards: '奖励',
    offerId: '货架ID', recipeId: '配方ID', titleId: '称号ID', suitId: '服装ID', poolId: '奖池',
    featured: '核心大奖', foundInRaid: '战局内找到',
    target: '目标', targetId: '目标ID', condition: '条件', conditions: '条件',
    weight: '权重', chance: '概率', tier: '档位', category: '分类', group: '分组',
    tasks: '任务', items: '物品', pools: '奖池', offers: '货架', requirements: '需求',
    startTime: '开始时间', endTime: '结束时间', period: '周期', refreshPeriod: '刷新周期',
};

function statusLabel(s) { return STATUS_LABELS[s] || s; }
function fmtScalar(v) {
    if (v === true) return '是';
    if (v === false) return '否';
    return esc(String(v));
}
function isEmpty(v) {
    return v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0);
}
function isReward(v) {
    return v && typeof v === 'object' && !Array.isArray(v) && typeof v.type === 'string' && v.type in TYPE_LABELS && ('tpl' in v || 'count' in v || 'offerId' in v || 'titleId' in v || 'suitId' in v || 'recipeId' in v || 'poolId' in v);
}

// 单个奖励 → 图标 + 名称(名 || 中文名 || 各类ID，绝不显示裸 tpl) + 数量 + 类型标签
function rewardChip(rw) {
    if (!rw || typeof rw !== 'object') return '';
    const type = rw.type || 'item';
    const label = TYPE_LABELS[type] || type;
    const resolved = rw.tpl ? itemNameMap[rw.tpl] : '';
    // 无法解析时回退「未知物品」，原始 tpl 仅保留在底部「原始数据（调试用）」
    const title = rw.name || resolved || rw.titleId || rw.suitId || rw.recipeId || rw.offerId || rw.poolId || (rw.tpl ? '未知物品' : '(未命名)');
    const icon = rw.tpl ? `<img class="rwc-icon" src="/battlepass/api/icons/${esc(rw.tpl)}" loading="lazy" onerror="this.remove()">` : '';
    const cnt = (rw.count && +rw.count > 1) ? `<span class="rwc-cnt">×${esc(rw.count)}</span>` : '';
    return `<span class="rwc"><span class="rwc-type">${esc(label)}</span>${icon}<span class="rwc-name">${esc(title)}</span>${cnt}</span>`;
}

function renderValue(key, v) {
    // 物品/货币模板字段：显示中文名而非裸 MongoId（解析不到回退「未知物品」，原始值见底部调试区）
    if ((key === 'tpl' || key === 'currencyTpl') && typeof v === 'string' && v) {
        return esc(itemNameMap[v] || '未知物品');
    }
    if ((key === 'free' || key === 'premium' || key === 'rewards') && Array.isArray(v)) {
        return v.length ? `<div class="rwc-list">${v.map(rewardChip).join('')}</div>` : '<span class="kv-none">（无）</span>';
    }
    if (isReward(v)) return `<div class="rwc-list">${rewardChip(v)}</div>`;
    if (Array.isArray(v)) {
        if (!v.length) return '<span class="kv-none">（无）</span>';
        if (typeof v[0] === 'object') return v.map(x => `<div class="kv-sub">${renderKV(x)}</div>`).join('');
        return esc(v.join('、'));
    }
    if (v && typeof v === 'object') return `<div class="kv-sub">${renderKV(v)}</div>`;
    return fmtScalar(v);
}

function renderKV(obj) {
    if (obj === null || obj === undefined) return '<div class="kv-none">（空）</div>';
    if (typeof obj !== 'object') return `<div class="kv-scalar">${fmtScalar(obj)}</div>`;
    if (Array.isArray(obj)) return renderValue('', obj);
    const rows = Object.entries(obj).filter(([, v]) => !isEmpty(v)).map(([k, v]) =>
        `<div class="kv-row"><span class="kv-k">${esc(FIELD_LABELS[k] || k)}</span><span class="kv-v">${renderValue(k, v)}</span></div>`);
    return rows.length ? `<div class="kv">${rows.join('')}</div>` : '<div class="kv-none">（空）</div>';
}

// 奖励轨：仅列出改动的等级，逐轨对比「变更前 → 变更后」
function renderTracksDiff(before, diff) {
    before = before || {}; diff = diff || {};
    const keys = Object.keys(diff).sort((a, b) => a === '0' ? 1 : b === '0' ? -1 : (+a) - (+b));
    if (!keys.length) return '<div class="kv-none">（无等级改动）</div>';
    const trackRow = (label, bArr, aArr) => {
        bArr = bArr || []; aArr = aArr || [];
        if (!bArr.length && !aArr.length) return '';
        const chips = arr => arr.length ? arr.map(rewardChip).join('') : '<span class="rw-none">（无）</span>';
        return `<div class="td-row">
            <span class="td-track">${label}</span>
            <div class="td-cmp">
                <div class="td-before"><span class="td-tag before">前</span>${chips(bArr)}</div>
                <div class="td-after"><span class="td-tag after">后</span>${chips(aArr)}</div>
            </div>
        </div>`;
    };
    const blocks = keys.map(k => {
        const lvName = k === '0' ? '循环奖励（满级后）' : `等级 ${esc(k)}`;
        const b = before[k] || {}, a = diff[k] || {};
        return `<div class="track-diff"><div class="td-lv">${lvName}</div>${trackRow('免费轨', b.free, a.free)}${trackRow('付费轨', b.premium, a.premium)}</div>`;
    });
    return `<div class="track-diff-list"><div class="td-hint">共 ${keys.length} 个等级改动</div>${blocks.join('')}</div>`;
}

function renderReadable(c) {
    const after = c.finalPayload ?? c.proposedPayload;
    if (c.module === 'tracks') return renderTracksDiff(c.beforePayload, after);
    if (c.operation === 'delete') {
        const body = c.beforePayload ? renderKV(c.beforePayload) : '';
        return `<div class="rd-del">将删除：<strong>${esc(c.targetDisplayName || c.targetId || '')}</strong></div>${body}`;
    }
    if (c.operation === 'update' && c.beforePayload) {
        return `<div class="rv-sec"><div class="rv-h">变更前</div>${renderKV(c.beforePayload)}</div>`
            + `<div class="rv-sec"><div class="rv-h">变更后</div>${renderKV(after)}</div>`;
    }
    return `<div class="rv-sec"><div class="rv-h">${c.operation === 'create' ? '新增内容' : '提交内容'}</div>${renderKV(after)}</div>`;
}

function rawDump(c) {
    const part = (label, v) => v ? `<div class="raw-block"><div class="raw-h">${label}</div><pre>${esc(JSON.stringify(v, null, 2))}</pre></div>` : '';
    const body = part('变更前', c.beforePayload) + part('提交内容', c.proposedPayload) + part('管理员编辑后', c.finalPayload);
    if (!body) return '';
    return `<details class="raw-details"><summary>原始数据（调试用）</summary>${body}</details>`;
}

async function showDetail(id) {
    const r = await api('/' + id);
    if (!r.success) return;
    const c = r.change;
    activeDetailId = c.id;
    activeDetailVersion = c.version || 1;
    itemNameMap = r.itemNames || {};   // 供 rewardChip / renderValue 显示物品中文名
    const detail = el('review-detail');
    detail.style.display = '';
    positionDetail(c.id);
    const time = new Date(c.createdUtc * 1000).toLocaleString('zh-CN');
    const opLabel = { create: '新增', update: '编辑', delete: '删除' }[c.operation] || c.operation;
    detail.innerHTML = `
        <h3>${esc(c.summary || c.commandType)}</h3>
        <p class="rd-meta"><strong>模块：</strong>${esc(MODULE_NAMES[c.module] || c.module)} · <strong>操作：</strong>${esc(opLabel)} · <strong>状态：</strong>${esc(statusLabel(c.status))}</p>
        <p class="rd-meta"><strong>操作者：</strong>${esc(c.actor?.displayName || '')} · <strong>时间：</strong>${time}</p>
        <div class="review-readable">${renderReadable(c)}</div>
        ${rawDump(c)}
        <div class="review-actions">
            <button class="btn primary small" onclick="approveOne('${esc(c.id)}')">批准</button>
            <button class="btn ghost small" onclick="rejectOne('${esc(c.id)}')">驳回</button>
            <button class="btn ghost small" onclick="closeDetail()">关闭</button>
        </div>
    `;
}

async function approveOne(id) {
    if (!confirm('确认批准此变更？批准后将即时生效。')) return;
    const r = await api('/' + id + '/approve?expectedVersion=' + encodeURIComponent(activeDetailVersion), { method: 'POST' });
    alert(r.success ? '批准成功，变更已即时生效' : (r.message || '批准失败'));
    if (r.success) { closeDetail(); init(); }
}

async function rejectOne(id) {
    const reason = prompt('请输入驳回理由（1-500字）：');
    if (!reason || reason.length < 1) return;
    const r = await api('/' + id + '/reject', { method: 'POST', body: JSON.stringify({ reason, expectedVersion: activeDetailVersion }) });
    alert(r.success ? '已驳回' : (r.message || '操作失败'));
    if (r.success) { closeDetail(); init(); }
}

// ---- 批量操作 ----
el('select-all').onchange = (e) => {
    if (e.target.checked) reviewItems.forEach(i => selectedIds.add(i.id));
    else selectedIds.clear();
    renderList(); renderBatch();
};

el('batch-approve').onclick = async () => {
    if (!selectedIds.size) return;
    if (!confirm(`确认批量批准 ${selectedIds.size} 条变更？`)) return;
    const items = reviewItems.filter(item => selectedIds.has(item.id)).map(item => ({ id: item.id, expectedVersion: item.version || 1 }));
    const r = await api('/batch/approve', { method: 'POST', body: JSON.stringify({ items }) });
    if (r.success) {
        alert(batchSummary(r.results || [], 'applied', '已生效'));
        closeDetail();
        init();
    } else { alert(r.message || '批量操作失败'); }
};

el('batch-reject').onclick = async () => {
    if (!selectedIds.size) return;
    const reason = prompt('请输入统一驳回理由：');
    if (!reason) return;
    const items = reviewItems.filter(item => selectedIds.has(item.id)).map(item => ({ id: item.id, expectedVersion: item.version || 1 }));
    const r = await api('/batch/reject', { method: 'POST', body: JSON.stringify({ items, reason }) });
    alert(r.success ? batchSummary(r.results || [], 'rejected', '已驳回') : (r.message || '操作失败'));
    if (r.success) { closeDetail(); init(); }
};

function batchSummary(results, successOutcome, successLabel) {
    const succeeded = results.filter(item => item.outcome === successOutcome).length;
    const skipped = results.filter(item => item.outcome === 'skipped').length;
    const failed = results.length - succeeded - skipped;
    const failureLines = results.filter(item => item.outcome !== successOutcome)
        .slice(0, 8)
        .map(item => `${item.id}: ${item.message || item.outcome}`);
    return [`批量处理完成：${succeeded} 条${successLabel}，${failed} 条失败，${skipped} 条跳过`, ...failureLines].join('\n');
}

mobileReviewLayout.addEventListener('change', () => {
    if (activeDetailId && el('review-detail').style.display !== 'none') positionDetail(activeDetailId);
});

// ---- 启动 ----
if (!ADMIN_TOKEN) { location.href = 'index.html'; }
else { init(); }
