'use strict';
/*
 * 通行证统一图形化选择器（约定组件）
 * ------------------------------------------------------------------
 * 所有管理模块（任务编辑、奖励轨、商人、物品管控…）检索物品/任务都用这里，
 * 统一走后端统一查询接口，统一响应形状，避免各页各写一套、键名分歧再次出现。
 *   物品： GET /battlepass/api/admin/query/items?q=&source=&limit=  → { success, items:[{tpl,name,shortName,...}] }
 *   任务： GET /battlepass/api/admin/query/tasks?q=&limit=          → { success, tasks:[{id,title,scope,conditionType,...}] }
 * 图标统一： /battlepass/api/icons/{tpl}
 *
 * 用法：
 *   BpPicker.attachItem(inputEl, resultsEl, ds => { ... ds.tpl, ds.name ... });
 *   BpPicker.attachTask(inputEl, resultsEl, ds => { ... ds.id, ds.title ... });
 *   const items = await BpPicker.queryItems('盐', {limit:8, source:'all'});
 */
(function (global) {
    const QUERY_API = '/battlepass/api/admin/query';
    const ICON_API = '/battlepass/api/icons/';

    function token() { return sessionStorage.getItem('bp_admin_token') || ''; }
    function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
    function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }

    async function query(kind, q, opts) {
        opts = opts || {};
        let url = QUERY_API + '/' + kind + '?q=' + encodeURIComponent(q) + '&limit=' + (opts.limit || 8);
        if (kind === 'items' && opts.source) url += '&source=' + encodeURIComponent(opts.source);
        try {
            const res = await fetch(url, { headers: { 'X-Admin-Token': token() } });
            const r = await res.json();
            if (!r || !r.success) return [];
            return (kind === 'tasks' ? r.tasks : r.items) || [];
        } catch (e) {
            return [];
        }
    }

    const queryItems = (q, opts) => query('items', q, opts);
    const queryTasks = (q, opts) => query('tasks', q, opts);

    function itemRow(h) {
        const name = esc(h.name || h.shortName || h.tpl);
        return `<div class="sr-item" data-tpl="${esc(h.tpl)}" data-name="${name}">
            <img src="${ICON_API}${esc(h.tpl)}" class="sr-icon" alt="" onerror="this.remove()" />
            <span class="sr-name">${name}</span><span class="sr-tpl">${esc(h.tpl).slice(0, 8)}…</span>
        </div>`;
    }
    function taskRow(t) {
        const title = esc(t.title || t.id);
        return `<div class="sr-item" data-id="${esc(t.id)}" data-title="${title}">
            <span class="sr-name">${title}</span><span class="sr-tpl">${esc(t.scope || '')}·${esc(t.conditionType || '')}</span>
        </div>`;
    }

    // 通用绑定：input 输入 → 防抖检索 → 渲染下拉 → 点选回调（收到选中项的 dataset）
    function attach(kind, input, results, onPick, opts) {
        let timer = null;
        const rowFn = kind === 'tasks' ? taskRow : itemRow;
        input.setAttribute('autocomplete', 'off');
        input.addEventListener('input', () => {
            clearTimeout(timer);
            const q = input.value.trim();
            if (q.length < 2) { results.style.display = 'none'; results.innerHTML = ''; return; }
            timer = setTimeout(async () => {
                const rows = await query(kind, q, opts);
                if (!rows.length) { results.innerHTML = '<div class="sr-none">无结果</div>'; results.style.display = ''; return; }
                results.innerHTML = rows.map(rowFn).join('');
                results.style.display = '';
                results.querySelectorAll('.sr-item').forEach(item => {
                    item.onmousedown = e => { e.preventDefault(); results.style.display = 'none'; onPick(item.dataset); };
                });
            }, 250);
        });
        input.addEventListener('focus', () => { if (results.children.length) results.style.display = ''; });
        input.addEventListener('blur', () => { setTimeout(() => { results.style.display = 'none'; }, 200); });
    }

    global.BpPicker = {
        ICON_API,
        isTpl,
        queryItems,
        queryTasks,
        attachItem: (input, results, onPick, opts) => attach('items', input, results, onPick, opts),
        attachTask: (input, results, onPick, opts) => attach('tasks', input, results, onPick, opts),
    };
})(window);
