// ---- 我的提交页 ----
'use strict';

const API_BASE = '/battlepass/api/admin/reviews';
const MODULE_NAMES = { shop: '商店', tasks: '任务', tracks: '奖励轨', lottery: '抽奖', trader: '商人', recipes: '配方', items: '物品管控', quests: '商人任务', flea: '跳蚤黑名单' };
const STATUS_NAMES = { pending: '待审核', applied: '已通过', rejected: '已驳回', conflict: '冲突', failed: '失败', withdrawn: '已撤回', applying: '应用中' };
const MODULE_PAGES = { shop: 'shop.html', tasks: 'tasks.html', tracks: 'index.html', lottery: 'lottery.html', trader: 'trader.html', recipes: 'recipes.html', items: 'items.html', quests: 'quests.html', flea: 'flea.html' };

function esc(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
function el(id) { return document.getElementById(id); }

async function api(path, opts = {}) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() };
    const res = await fetch(API_BASE + path, { headers, ...opts });
    return res.json();
}

async function load() {
    const r = await api('?limit=100');
    const items = r.success ? (r.items || []) : [];
    render(items);
}

function render(items) {
    const box = el('change-list');
    if (!items.length) { box.innerHTML = '<div class="empty-msg">暂无提交记录</div>'; return; }

    box.innerHTML = items.map(c => {
        const mod = MODULE_NAMES[c.module] || c.module;
        const status = STATUS_NAMES[c.status] || c.status;
        const time = new Date(c.createdUtc * 1000).toLocaleString('zh-CN');
        const reason = c.reviewReason ? `<div class="ci-reason">驳回理由：${esc(c.reviewReason)}</div>` : '';
        const pendingActions = c.status === 'pending' ? `<div class="ci-actions">
            <button class="btn primary small" onclick="editSubmitted('${esc(c.id)}','${esc(c.module)}')">编辑</button>
            <button class="btn ghost small" onclick="withdraw('${esc(c.id)}',${Number(c.version || 1)})">撤回</button>
        </div>` : '';
        return `<div class="change-item">
            <div class="ci-head">
                <span class="ci-module">${esc(mod)}</span>
                <strong>${esc(c.targetDisplayName || c.targetId || '')}</strong>
                <span class="ci-status ${c.status}">${esc(status)}</span>
            </div>
            <div class="ci-summary">${esc(c.summary || '')}</div>
            ${reason}
            <div class="ci-meta">${time}</div>
            ${pendingActions}
        </div>`;
    }).join('');
}

function editSubmitted(id, module) {
    const page = MODULE_PAGES[module];
    if (!page) { alert('该模块暂不支持可视化编辑'); return; }
    location.href = `${page}?editChange=${encodeURIComponent(id)}`;
}

async function withdraw(id, version) {
    if (!confirm('确认撤回此提交？')) return;
    const r = await api('/' + id + '/withdraw?expectedVersion=' + encodeURIComponent(version || 1), { method: 'POST' });
    alert(r.success ? '已撤回' : (r.message || '操作失败'));
    if (r.success) load();
}

// 协管可从玩家页带 #bpsso= 直达查看自己的提交；管理员用已有会话。
(async function () {
    const ok = await ensureAdminSession();
    if (!ok) {
        // 协管失效走无密码 gate（铁律）；管理员回落主后台登录。
        if (handleSessionFailure()) return;
        location.href = 'index.html';
        return;
    }
    load();
})();
