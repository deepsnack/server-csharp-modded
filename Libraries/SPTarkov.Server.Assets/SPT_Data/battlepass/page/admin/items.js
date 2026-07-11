'use strict';

// 物品管控管理页：复用通行证后台 admin token / SSO（同 trader.js / titles.js）。
// REGISTER_ADMIN_LOGIN / token 存取 / adminLogin / ensureAdminSession / submitChange 均由 auth.js 提供。
const ADMIN_API = '/battlepass/api/admin/items';
const ICON_API = '/battlepass/api/icons/';
let ADMIN_TOKEN = getAdminToken();
let SELECTED = null; // 当前选中的 tpl
let costEd = null;   // 当前新增表单的统一价格编辑器（BpPrice），无价格字段时为 null

function el(id) { return document.getElementById(id); }
function esc(s) { return (s == null ? '' : String(s)).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
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
async function doAdminLogin() {
    const password = el('admin-pass').value;
    const r = await adminLogin(password);
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    setActorType(r.actorType || 'admin');
    ADMIN_TOKEN = getAdminToken();
    enterConsole();
}
function logout() { clearAdminToken(); clearActorType(); el('items-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
function enterConsole() {
    el('login-view').classList.add('hidden'); el('items-view').classList.remove('hidden');
    if (isCollaborator()) applyCollaboratorUi();
}

// ---- 协管 UI 适配：顶部提示条 ----
function applyCollaboratorUi() {
    if (el('collab-banner')) return;
    const bar = document.createElement('p');
    bar.id = 'collab-banner';
    bar.className = 'hint';
    bar.style.cssText = 'border-left:4px solid #e0a030;margin:0 0 10px';
    bar.textContent = '协管模式：增删获取途径 / 跳蚤黑名单将提交审核，等待管理员批准后生效。';
    const view = el('items-view');
    if (view) view.insertBefore(bar, view.firstChild);
}

// ---- 搜索 ----
async function doSearch() {
    const q = el('q').value.trim();
    const source = el('q-source').value;
    const r = await api(`/search?q=${encodeURIComponent(q)}&source=${source}`);
    const box = el('search-rows'); box.innerHTML = '';
    if (!r.success) { toast(r.message || '搜索失败', false); return; }
    (r.items || []).forEach(it => {
        const div = document.createElement('div');
        div.className = 'search-item' + (it.tpl === SELECTED ? ' sel' : '');
        div.innerHTML = `<img class="rw-icon" src="${ICON_API}${it.tpl}" alt="" onerror="this.remove()" />
            <div class="si-text"><div class="si-name">${esc(it.name || it.tpl)} ${it.isMod ? '<span class="badge mod">MOD</span>' : '<span class="badge van">原版</span>'}</div>
            <div class="meta">${esc(it.tpl)}${it.canSellOnRagfair ? '' : ' · 不可售'}</div></div>`;
        div.onclick = () => selectItem(it.tpl);
        box.appendChild(div);
    });
    if (!r.items || r.items.length === 0) box.innerHTML = '<div class="search-item">无结果</div>';
}

// ---- 选中 → 获取图谱 ----
async function selectItem(tpl) {
    SELECTED = tpl;
    document.querySelectorAll('.search-item').forEach(e => e.classList.remove('sel'));
    const r = await api(`/acquisitions/${tpl}`);
    if (!r.success) { toast(r.message || '加载失败', false); return; }
    renderGraph(r.graph);
    // 同步标记搜索列表选中项
    doSearchHighlight();
}
function doSearchHighlight() {
    document.querySelectorAll('.search-item').forEach(e => {
        const m = e.querySelector('.meta'); if (m && m.textContent.startsWith(SELECTED)) e.classList.add('sel');
    });
}

const SOURCE_LABEL = { trader: '商人', quest: '任务奖励', hideout: '藏身处', startInv: '初始库存', loot: '战利品' };

function renderGraph(g) {
    el('graph-title').textContent = '获取图谱 · ' + (g.name || g.tpl);
    const body = el('graph-body'); body.innerHTML = '';

    // 头部元信息
    const head = document.createElement('div'); head.className = 'graph-head';
    head.innerHTML = `<img class="rw-icon big" src="${ICON_API}${g.tpl}" onerror="this.remove()" />
        <div><div class="gh-name">${esc(g.name || g.tpl)} ${g.isMod ? '<span class="badge mod">MOD</span>' : '<span class="badge van">原版</span>'}</div>
        <div class="meta">${esc(g.tpl)} · 父类 ${esc(g.parent)}</div>
        <div class="meta">跳蚤：${g.fleaBlacklisted ? '<span class="badge bl">已拉黑</span>' : (g.canSellOnRagfair ? '可售' : '不可售(BSG)')}</div></div>`;
    // 快捷加入/移出跳蚤黑名单（无需切到跳蚤黑名单页重复搜索）
    const flBtn = document.createElement('button');
    flBtn.className = 'btn ghost small'; flBtn.style.marginTop = '6px';
    flBtn.textContent = g.fleaBlacklisted ? '移出跳蚤黑名单' : '加入跳蚤黑名单';
    flBtn.onclick = () => toggleFleaBlacklist(g.tpl, !g.fleaBlacklisted);
    head.lastElementChild.appendChild(flBtn);
    body.appendChild(head);

    // 按来源分组
    const bySource = {};
    (g.entries || []).forEach(e => { (bySource[e.source] = bySource[e.source] || []).push(e); });
    ['trader', 'quest', 'hideout', 'startInv', 'loot'].forEach(src => {
        const list = bySource[src] || [];
        const sec = document.createElement('div'); sec.className = 'graph-sec';
        sec.innerHTML = `<div class="gs-head">${SOURCE_LABEL[src]}（${list.length}）</div>`;
        list.forEach(e => {
            const row = document.createElement('div'); row.className = 'gs-row';
            const lbl = document.createElement('span'); lbl.textContent = e.label; row.appendChild(lbl);
            const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '移除';
            del.onclick = () => removeEntry(src, e.ref); row.appendChild(del);
            sec.appendChild(row);
        });
        body.appendChild(sec);
    });

    // 任务前置路径
    if (g.questPaths && g.questPaths.length) {
        const sec = document.createElement('div'); sec.className = 'graph-sec';
        sec.innerHTML = '<div class="gs-head">任务前置路径</div>';
        g.questPaths.forEach(n => {
            const row = document.createElement('div'); row.className = 'gs-row';
            row.innerHTML = `<span>${esc(n.name)} <span class="meta">${esc(n.questId)}</span>${n.prerequisites && n.prerequisites.length ? ' ← 前置 ' + n.prerequisites.map(esc).join(', ') : ''}</span>`;
            sec.appendChild(row);
        });
        body.appendChild(sec);
    }

    body.appendChild(buildAddForm(g.tpl));
    renderOverrides(body, g.tpl);
}

// ---- 快捷跳蚤黑名单（增量端点，复用 flea 控制器）----
async function toggleFleaBlacklist(tpl, add) {
    if (isCollaborator()) {
        const sr = await submitChange('flea', 'flea.blacklist.toggle', { tpl, add });
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        return;
    }
    const r = await fetch('/battlepass/api/admin/flea/blacklist/toggle', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() },
        body: JSON.stringify({ tpl, add })
    }).then(x => x.json()).catch(() => ({ success: false }));
    toast(r.success ? (add ? '已加入跳蚤黑名单' : '已移出跳蚤黑名单') : (r.message || '操作失败'), r.success);
    if (r.success) selectItem(tpl);
}

// ---- 移除现有途径 ----
async function removeEntry(source, ref) {
    if (!confirm('移除该获取途径？管控会持久保存（重启后依然生效），可随时在「已应用编辑」中撤销恢复（即时生效）。')) return;
    const ov = { op: 'remove', source, tpl: SELECTED };
    Object.assign(ov, ref || {});
    if (isCollaborator()) {
        const sr = await submitChange('items', 'item.edit', ov);
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        return;
    }
    const r = await api('/edit', 'POST', ov);
    toast(r.success ? '已移除' : (r.message || '失败'), r.success);
    if (r.success) selectItem(SELECTED);
}

// ---- 新增途径表单 ----
function buildAddForm(tpl) {
    const wrap = document.createElement('div'); wrap.className = 'graph-sec add-form';
    wrap.innerHTML = `<div class="gs-head">新增获取途径</div>
        <label>来源</label>
        <select id="add-source">
            <option value="trader">商人货架</option>
            <option value="quest">任务奖励</option>
            <option value="hideout">藏身处配方</option>
            <option value="loot">战利品</option>
        </select>
        <div id="add-fields"></div>
        <button id="add-btn" class="btn primary small">新增</button>`;
    setTimeout(() => {
        el('add-source').onchange = renderAddFields;
        renderAddFields();
        el('add-btn').onclick = () => submitAdd(tpl);
    }, 0);
    return wrap;
}
function renderAddFields() {
    const src = el('add-source').value;
    const f = el('add-fields');
    costEd = null;
    if (src === 'trader') {
        f.innerHTML = `<label>商人 ID</label><input id="f-traderId" placeholder="如 54cb50c76803fa8b248b4571（Prapor）" />
            <label>忠诚等级</label><input id="f-loyalty" type="number" value="1" />
            <label>价格（搜索支付物品或货币并填写数量；可加多项；留空=无限免费）</label><div id="f-cost-editor"></div>`;
    } else if (src === 'quest') {
        f.innerHTML = `<label>任务 ID</label><input id="f-questId" />
            <label>奖励组</label><select id="f-group"><option>Success</option><option>Started</option><option>AvailableForFinish</option><option>Fail</option></select>
            <label>数量</label><input id="f-count" type="number" value="1" />`;
    } else if (src === 'hideout') {
        f.innerHTML = `<label>角色</label><select id="f-role"><option value="output">产物（新建简易配方）</option><option value="ingredient">原料（加入现有配方）</option></select>
            <label>配方 ID（原料模式必填）</label><input id="f-recipeId" />
            <label>数量</label><input id="f-count" type="number" value="1" />
            <label>原料（产物模式：搜索物品或货币并填写数量）</label><div id="f-cost-editor"></div>`;
    } else if (src === 'loot') {
        f.innerHTML = `<label>地图（Bigmap / Interchange / Woods / Shoreline / Customs=Bigmap …）</label><input id="f-locationId" placeholder="Bigmap" />
            <label>种类</label><select id="f-kind"><option value="static">静态容器分布</option></select>
            <label>容器 tpl（留空=该图全部容器）</label><input id="f-container" />
            <label>相对权重</label><input id="f-weight" type="number" value="1" />`;
    }
    if (el('f-cost-editor')) costEd = BpPrice.createEditor(el('f-cost-editor'));
}
async function submitAdd(tpl) {
    const src = el('add-source').value;
    const ov = { op: 'add', source: src, tpl };
    if (src === 'trader') {
        ov.traderId = el('f-traderId').value.trim();
        ov.loyalty = +el('f-loyalty').value || 1;
        const t = costEd.collect();
        if (t.invalid) return toast('价格项有误：物品需选有效物品、数量需为正整数', false);
        ov.cost = t.cost;
        if (!isTpl(ov.traderId)) return toast('商人 ID 非法', false);
    } else if (src === 'quest') {
        ov.questId = el('f-questId').value.trim();
        ov.rewardGroup = el('f-group').value;
        ov.count = +el('f-count').value || 1;
        if (!isTpl(ov.questId)) return toast('任务 ID 非法', false);
    } else if (src === 'hideout') {
        ov.role = el('f-role').value;
        ov.recipeId = el('f-recipeId').value.trim() || null;
        ov.count = +el('f-count').value || 1;
        const t = costEd.collect();
        if (t.invalid) return toast('原料项有误：物品需选有效物品、数量需为正整数', false);
        ov.cost = t.cost;
        if (ov.role === 'ingredient' && !ov.recipeId) return toast('原料模式需填配方 ID', false);
    } else if (src === 'loot') {
        ov.locationId = el('f-locationId').value.trim();
        ov.lootKind = el('f-kind').value;
        ov.containerOrSpawn = el('f-container').value.trim() || null;
        ov.weight = +el('f-weight').value || 1;
        if (!ov.locationId) return toast('请填地图', false);
    }
    if (isCollaborator()) {
        const sr = await submitChange('items', 'item.edit', ov);
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        return;
    }
    const r = await api('/edit', 'POST', ov);
    toast(r.success ? '已新增' : (r.message || '失败'), r.success);
    if (r.success) selectItem(tpl);
}

// ---- 已应用编辑（override）列表 ----
async function renderOverrides(body, tpl) {
    const r = await api('/overrides');
    if (!r.success) return;
    const mine = (r.overrides || []).filter(o => o.tpl === tpl);
    if (mine.length === 0) return;
    const sec = document.createElement('div'); sec.className = 'graph-sec';
    sec.innerHTML = '<div class="gs-head">已应用编辑（可撤销）</div>';
    mine.forEach(o => {
        const row = document.createElement('div'); row.className = 'gs-row';
        const lbl = document.createElement('span');
        lbl.textContent = `${o.op === 'add' ? '增' : '删'} · ${SOURCE_LABEL[o.source] || o.source} · ${o.traderId || o.questId || o.recipeId || o.locationId || o.side || ''}`;
        row.appendChild(lbl);
        const undo = document.createElement('button'); undo.className = 'mini'; undo.textContent = '撤销';
        undo.onclick = async () => {
            if (isCollaborator()) {
                const sr = await submitChange('items', 'item.override.delete', { id: o.id });
                toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
                return;
            }
            const x = await api('/overrides', 'DELETE', { id: o.id }); toast(x.success ? '已撤销' : '失败', x.success); if (x.success) selectItem(tpl);
        };
        row.appendChild(undo); sec.appendChild(row);
    });
    body.appendChild(sec);
}

async function applyPendingItemsEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    const payload = change.proposedPayload || {};
    if (change.commandType === 'item.override.delete') {
        toast('请选择新的已应用编辑并点击撤销，以更新原删除审核单', true);
        return;
    }
    if (change.commandType !== 'item.edit' || !payload.tpl) return;

    await selectItem(payload.tpl);
    if (payload.op !== 'add') {
        toast('请选择新的获取途径操作，以更新原审核单', true);
        return;
    }

    el('add-source').value = payload.source || 'trader';
    renderAddFields();
    if (payload.source === 'trader') {
        el('f-traderId').value = payload.traderId || '';
        el('f-loyalty').value = payload.loyalty || 1;
        costEd?.setCost(payload.cost || []);
    } else if (payload.source === 'quest') {
        el('f-questId').value = payload.questId || '';
        el('f-group').value = payload.rewardGroup || 'Success';
        el('f-count').value = payload.count || 1;
    } else if (payload.source === 'hideout') {
        el('f-role').value = payload.role || 'output';
        el('f-recipeId').value = payload.recipeId || '';
        el('f-count').value = payload.count || 1;
        costEd?.setCost(payload.cost || []);
    } else if (payload.source === 'loot') {
        el('f-locationId').value = payload.locationId || '';
        el('f-kind').value = payload.lootKind || 'static';
        el('f-container').value = payload.containerOrSpawn || '';
        el('f-weight').value = payload.weight || 1;
    }
}

el('admin-login-btn').onclick = doAdminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doAdminLogin(); });
el('admin-logout').onclick = logout;
el('search-btn').onclick = doSearch;
el('q').addEventListener('keydown', e => { if (e.key === 'Enter') doSearch(); });

bootstrapAdminPage({ moduleCap: 'items.read', onReady: async edit => { ADMIN_TOKEN = getAdminToken(); enterConsole(); await applyPendingItemsEdit(edit); } });
