'use strict';
/*
 * 通行证统一图形化选择器（约定组件）
 * ------------------------------------------------------------------
 * 所有管理模块（任务编辑、奖励轨、商人、物品管控…）检索物品/任务都用这里，
 * 统一走后端统一查询接口，统一响应形状，避免各页各写一套、键名分歧再次出现。
 *   物品： GET /battlepass/api/admin/query/items?q=&source=&limit=  → { success, items:[{tpl,name,shortName,...}] }
 *   任务： GET /battlepass/api/admin/query/tasks?q=&limit=          → { success, tasks:[{id,title,scope,conditionType,...}] }
 *   服装： GET /battlepass/api/admin/query/clothing?q=&limit=       → { success, clothing:[{suitId,name,offerId,traderId,...}] }
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

    const RESULT_KEYS = { items: 'items', tasks: 'tasks', offers: 'offers', recipes: 'recipes', titles: 'titles', clothing: 'clothing' };

    async function query(kind, q, opts) {
        opts = opts || {};
        let url = QUERY_API + '/' + kind + '?q=' + encodeURIComponent(q) + '&limit=' + (opts.limit || 8);
        if (kind === 'items' && opts.source) url += '&source=' + encodeURIComponent(opts.source);
        if (kind === 'items' && opts.category) url += '&category=' + encodeURIComponent(opts.category);
        if (kind === 'recipes' && opts.questUnlockOnly) url += '&questUnlockOnly=true';
        const res = await fetch(url, { headers: { 'X-Admin-Token': token() } });
        let r;
        try { r = await res.json(); }
        catch { throw new Error(`搜索接口返回异常（HTTP ${res.status}）`); }
        if (!res.ok || !r || !r.success) throw new Error(r?.message || `搜索失败（HTTP ${res.status}）`);
        return r[RESULT_KEYS[kind] || 'items'] || [];
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
    function offerRow(o) {
        const name = esc(o.name || o.id);
        return `<div class="sr-item" data-id="${esc(o.id)}" data-tpl="${esc(o.tpl)}" data-name="${name}">
            <img src="${ICON_API}${esc(o.tpl)}" class="sr-icon" alt="" onerror="this.remove()" />
            <span class="sr-name">${name}</span><span class="sr-tpl">${esc(o.id)}</span>
        </div>`;
    }
    function recipeRow(r) {
        const name = esc(r.name || r.id);
        const cnt = r.count > 1 ? ` ×${r.count}` : '';
        return `<div class="sr-item" data-id="${esc(r.id)}" data-tpl="${esc(r.endProduct)}" data-name="${name}">
            <img src="${ICON_API}${esc(r.endProduct)}" class="sr-icon" alt="" onerror="this.remove()" />
            <span class="sr-name">${name}${cnt}</span><span class="sr-tpl">${esc(r.areaType || '')}·${esc(r.id).slice(0, 8)}…</span>
        </div>`;
    }
    function titleRow(t) {
        const name = esc(t.name || t.text || t.id);
        return `<div class="sr-item" data-id="${esc(t.id)}" data-name="${name}">
            <span class="sr-name">${name}</span><span class="sr-tpl">${esc(t.type || '')}·${esc(t.id)}</span>
        </div>`;
    }
    function clothingRow(c) {
        const suitId = c.suitId || c.id;
        const name = esc(c.name || c.shortName || suitId);
        const meta = [c.traderId ? `商人 ${c.traderId.slice(0, 8)}…` : '', c.offerId ? `offer ${c.offerId.slice(0, 8)}…` : '', (c.side || []).join('/')].filter(Boolean).join(' · ');
        return `<div class="sr-item" data-id="${esc(suitId)}" data-suit-id="${esc(suitId)}" data-name="${name}">
            <span class="sr-name">${name}</span><span class="sr-tpl">${esc(meta || suitId)}</span>
        </div>`;
    }

    // 通用绑定：input 输入 → 防抖检索 → 渲染下拉 → 点选回调（收到选中项的 dataset）
    const ROW_FNS = { items: itemRow, tasks: taskRow, offers: offerRow, recipes: recipeRow, titles: titleRow, clothing: clothingRow };
    function attach(kind, input, results, onPick, opts) {
        opts = opts || {};
        let timer = null;
        const rowFn = ROW_FNS[kind] || itemRow;
        // minChars=0 时聚焦即列出（货架/称号等少量数据源不需要先打字）
        const minChars = opts.minChars != null ? opts.minChars : 2;
        input.setAttribute('autocomplete', 'off');
        async function run(q) {
            try {
                const rows = await query(kind, q, opts);
                if (!rows.length) { results.innerHTML = '<div class="sr-none">无结果</div>'; results.style.display = ''; return; }
                results.innerHTML = rows.map(rowFn).join('');
                results.style.display = '';
                results.querySelectorAll('.sr-item').forEach(item => {
                    item.onmousedown = e => { e.preventDefault(); results.style.display = 'none'; onPick(item.dataset); };
                });
            } catch (error) {
                results.innerHTML = `<div class="sr-none error">${esc(error.message || '搜索失败')}</div>`;
                results.style.display = '';
            }
        }
        input.addEventListener('input', () => {
            clearTimeout(timer);
            const q = input.value.trim();
            // 中文单字即有明确语义；拉丁字母仍默认至少 2 个字符，避免无意义的大范围查询。
            const requiredChars = /[\u3400-\u9fff\uf900-\ufaff]/.test(q) ? 1 : minChars;
            if (q.length < requiredChars) { results.style.display = 'none'; results.innerHTML = ''; return; }
            timer = setTimeout(() => run(q), 250);
        });
        input.addEventListener('focus', () => {
            if (results.children.length) { results.style.display = ''; return; }
            if (minChars === 0) run(input.value.trim());
        });
        input.addEventListener('blur', () => { setTimeout(() => { results.style.display = 'none'; }, 200); });
    }

    global.BpPicker = {
        ICON_API,
        isTpl,
        queryItems,
        queryTasks,
        attachItem: (input, results, onPick, opts) => attach('items', input, results, onPick, opts),
        attachTask: (input, results, onPick, opts) => attach('tasks', input, results, onPick, opts),
        attachOffer: (input, results, onPick, opts) => attach('offers', input, results, onPick, opts),
        attachRecipe: (input, results, onPick, opts) => attach('recipes', input, results, onPick, opts),
        attachTitle: (input, results, onPick, opts) => attach('titles', input, results, onPick, opts),
        attachClothing: (input, results, onPick, opts) => attach('clothing', input, results, onPick, opts),
    };
})(window);
