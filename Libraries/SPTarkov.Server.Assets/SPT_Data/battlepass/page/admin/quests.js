'use strict';

// 商人任务管理页：复用通行证后台 admin token / SSO（同 items.js / trader.js）。
// 四大能力：依赖图谱与详情查看 / 启用禁用原版任务 / 编辑现有任务奖励 / 新建自定义商人任务。
// 管理员直写即时生效；协管走审核队列（submitChange('quests', ...)）。
const ADMIN_API = '/battlepass/api/admin/quests';
const ICON_API = '/battlepass/api/icons/';
let ADMIN_TOKEN = getAdminToken();
let SELECTED = null;     // 当前选中的 questId
let TRADERS = [];        // 商人清单缓存
let CUR_TRADER = '';     // 当前筛选商人
let QUEST_CATALOG = { locations: [], killTargets: [] };

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
    await enterConsole();
}
function logout() { clearAdminToken(); clearActorType(); el('quests-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
async function enterConsole() {
    el('login-view').classList.add('hidden'); el('quests-view').classList.remove('hidden');
    if (isCollaborator()) applyCollaboratorUi();
    await loadTraders();
}

// ---- 协管 UI 适配：顶部提示条 ----
function applyCollaboratorUi() {
    if (el('collab-banner')) return;
    const bar = document.createElement('p');
    bar.id = 'collab-banner';
    bar.className = 'hint';
    bar.style.cssText = 'border-left:4px solid #e0a030;margin:0 0 10px';
    bar.textContent = '协管模式：禁用任务 / 编辑奖励 / 新建自定义任务将提交审核，等待管理员批准后生效。';
    const view = el('quests-view');
    if (view) view.insertBefore(bar, view.firstChild);
}

// ---- 商人清单 ----
async function loadTraders() {
    const r = await api('/traders');
    if (!r.success) { toast(r.message || '加载商人失败', false); return; }
    TRADERS = r.traders || [];
    const catalog = await api('/catalog');
    if (catalog.success) {
        QUEST_CATALOG = { locations: catalog.locations || [], killTargets: catalog.killTargets || [] };
    }
    const sel = el('q-trader');
    sel.innerHTML = '<option value="">全部商人</option>' +
        TRADERS.map(t => `<option value="${esc(t.traderId)}">${esc(t.name || t.traderId)}</option>`).join('');
    await doSearch();
}
function traderName(id) { const t = TRADERS.find(x => x.traderId === id); return t ? t.name : id; }

// ---- 任务清单搜索 ----
async function doSearch() {
    CUR_TRADER = el('q-trader').value;
    const q = el('q').value.trim();
    const r = await api(`/list?traderId=${encodeURIComponent(CUR_TRADER)}&q=${encodeURIComponent(q)}`);
    const box = el('quest-rows'); box.innerHTML = '';
    if (!r.success) { toast(r.message || '搜索失败', false); return; }
    const list = r.quests || [];
    if (!list.length) { box.innerHTML = '<div class="search-item">无结果</div>'; return; }
    list.forEach(it => {
        const div = document.createElement('div');
        div.className = 'search-item' + (it.questId === SELECTED ? ' sel' : '');
        const badges = [];
        if (it.isCustom) badges.push('<span class="badge mod">自定义</span>');
        else badges.push('<span class="badge van">原版</span>');
        if (it.disabled) badges.push('<span class="badge bl">已禁用</span>');
        if (it.hasRewardOverride) badges.push('<span class="badge">改奖</span>');
        const meta = `${esc(it.traderName || traderName(it.traderId))} · 前置${it.unlockCount} · 互斥${it.failCount}`;
        div.innerHTML = `<div class="si-text"><div class="si-name">${esc(it.name || it.questId)} ${badges.join(' ')}</div>
            <div class="meta">${meta}</div></div>`;
        div.onclick = () => selectQuest(it.questId);
        box.appendChild(div);
    });
}

// ---- 选中任务 → 详情 + 图谱 ----
async function selectQuest(questId) {
    SELECTED = questId;
    document.querySelectorAll('.search-item').forEach(e => e.classList.remove('sel'));
    const r = await api(`/detail/${questId}`);
    if (!r.success) { toast(r.message || '加载失败', false); return; }
    renderDetail(r.detail);
    doSearch();
}

const COND_LABEL = {
    HandoverItem: '上交物品', FindItem: '找到物品', CounterCreator: '计数(击杀/到访…)',
    WeaponAssembly: '指定改装枪械',
    LeaveItemAtLocation: '在指定地点留物', PlaceBeacon: '放置信标', Skill: '技能等级',
    TraderLoyalty: '商人忠诚', Quest: '完成任务', Level: '角色等级', VisitPlace: '到访地点',
};
const STATUS_LABEL = { 2: '已接取', 4: '已完成', 5: '已交付' };
const RTYPE_LABEL = { Item: '物品', Experience: '经验', TraderStanding: '商人好感', TraderUnlock: '解锁商人', Skill: '技能', AssortmentUnlock: '直购权', ProductionScheme: '藏身处配方' };

function renderDetail(d) {
    el('detail-title').textContent = '任务详情 · ' + (d.name || d.questId);
    const body = el('detail-body'); body.innerHTML = '';

    // 头部
    const head = document.createElement('div'); head.className = 'graph-head';
    const badges = [];
    badges.push(d.isCustom ? '<span class="badge mod">自定义</span>' : '<span class="badge van">原版</span>');
    if (d.disabled) badges.push('<span class="badge bl">已禁用</span>');
    if (d.hasRewardOverride) badges.push('<span class="badge">奖励已覆盖</span>');
    head.innerHTML = `<div><div class="gh-name">${esc(d.name || d.questId)} ${badges.join(' ')}</div>
        <div class="meta">${esc(d.questId)}</div>
        <div class="meta">商人：${esc(d.traderName)} · 阵营：${esc(d.side || 'Any')} · 地图：${esc(d.location || 'any')}</div>
        ${d.description ? `<div class="meta" style="margin-top:6px;white-space:pre-wrap">${esc(d.description)}</div>` : ''}</div>`;
    body.appendChild(head);

    // 操作条：禁用/启用切换（原版任务），自定义任务提供编辑/删除
    const ops = document.createElement('div'); ops.className = 'graph-sec';
    ops.innerHTML = '<div class="gs-head">操作</div>';
    const opRow = document.createElement('div'); opRow.className = 'gs-row';
    const toggleBtn = document.createElement('button');
    toggleBtn.className = 'btn ' + (d.disabled ? 'primary' : 'ghost') + ' small';
    toggleBtn.textContent = d.disabled ? '启用该任务' : '禁用该任务';
    toggleBtn.onclick = () => toggleDisabled(d);
    opRow.appendChild(toggleBtn);
    if (d.isCustom) {
        const editBtn = document.createElement('button'); editBtn.className = 'btn ghost small'; editBtn.style.marginLeft = '8px';
        editBtn.textContent = '编辑此自定义任务'; editBtn.onclick = () => openCustomForm(d.questId);
        opRow.appendChild(editBtn);
        const delBtn = document.createElement('button'); delBtn.className = 'btn ghost small'; delBtn.style.marginLeft = '8px';
        delBtn.textContent = '删除此自定义任务'; delBtn.onclick = () => deleteCustom(d.questId, d.name);
        opRow.appendChild(delBtn);
    }
    ops.appendChild(opRow);
    body.appendChild(ops);

    // 前置条件
    const preSec = document.createElement('div'); preSec.className = 'graph-sec';
    preSec.innerHTML = `<div class="gs-head">前置任务（${(d.prerequisites || []).length}）</div>`;
    if (!d.prerequisites || !d.prerequisites.length) {
        preSec.innerHTML += '<div class="gs-row"><span class="meta">无前置，直接可接</span></div>';
    } else {
        d.prerequisites.forEach(p => {
            const row = document.createElement('div'); row.className = 'gs-row';
            const st = (p.status || []).map(s => STATUS_LABEL[s] || s).join('/');
            row.innerHTML = `<span>${esc(p.name || p.questId)} <span class="meta">需${st}${p.availableAfter ? ` · 延迟${p.availableAfter}s` : ''}</span></span>`;
            const jump = document.createElement('button'); jump.className = 'mini'; jump.textContent = '查看';
            jump.onclick = () => selectQuest(p.questId); row.appendChild(jump);
            preSec.appendChild(row);
        });
    }
    body.appendChild(preSec);

    // 完成目标
    const objSec = document.createElement('div'); objSec.className = 'graph-sec';
    objSec.innerHTML = `<div class="gs-head">完成条件（${(d.objectives || []).length}）</div>`;
    (d.objectives || []).forEach(o => {
        const row = document.createElement('div'); row.className = 'gs-row';
        const label = COND_LABEL[o.conditionType] || o.conditionType;
        const val = o.value != null ? ` ×${o.value}` : '';
        const fir = o.onlyFoundInRaid ? ' · 需战局中找到' : '';
        const details = (o.details || []).join(' · ') || (o.targets || []).slice(0, 3).join(', ');
        row.innerHTML = `<span>${esc(label)}${val}${fir} <span class="meta">${esc(details)}</span></span>`;
        objSec.appendChild(row);
    });
    body.appendChild(objSec);

    // 奖励（按桶）
    renderRewardSection(body, d);

    // 图谱按钮
    const graphSec = document.createElement('div'); graphSec.className = 'graph-sec';
    graphSec.innerHTML = '<div class="gs-head">依赖图谱</div>';
    const gRow = document.createElement('div'); gRow.className = 'gs-row';
    const gBtn = document.createElement('button'); gBtn.className = 'btn ghost small';
    gBtn.textContent = '查看该商人任务依赖图谱'; gBtn.onclick = () => showGraph(d.traderId || CUR_TRADER);
    gRow.appendChild(gBtn); graphSec.appendChild(gRow);
    body.appendChild(graphSec);
}

// ---- 奖励区（展示当前 + 编辑覆盖）----
const REWARD_BUCKETS = ['Started', 'Success', 'Fail'];
const BUCKET_LABEL = { Started: '接取奖励(Started)', Success: '完成奖励(Success)', Fail: '失败惩罚(Fail)' };

function renderRewardSection(body, d) {
    const sec = document.createElement('div'); sec.className = 'graph-sec';
    sec.innerHTML = '<div class="gs-head">奖励' + (d.hasRewardOverride ? ' · <span class="badge">已覆盖</span>' : '') + '</div>';
    REWARD_BUCKETS.forEach(bucket => {
        const list = (d.rewards && d.rewards[bucket]) || [];
        if (!list.length) return;
        const sub = document.createElement('div'); sub.className = 'gs-row';
        const txt = list.map(r => rewardText(r)).join('、');
        sub.innerHTML = `<span><b>${BUCKET_LABEL[bucket]}</b>：${esc(txt)}</span>`;
        sec.appendChild(sub);
    });
    const editRow = document.createElement('div'); editRow.className = 'gs-row';
    const editBtn = document.createElement('button'); editBtn.className = 'btn ghost small';
    editBtn.textContent = d.hasRewardOverride ? '编辑奖励覆盖' : '覆盖/编辑奖励';
    editBtn.onclick = () => openRewardEditor(d);
    editRow.appendChild(editBtn);
    if (d.hasRewardOverride) {
        const clr = document.createElement('button'); clr.className = 'mini'; clr.style.marginLeft = '8px';
        clr.textContent = '还原原版奖励'; clr.onclick = () => clearRewardOverride(d.questId);
        editRow.appendChild(clr);
    }
    sec.appendChild(editRow);
    body.appendChild(sec);
}

function rewardText(r) {
    const type = (r.type || 'Item').toLowerCase();
    if (type === 'item') return `${r.name || r.tpl || '物品'} ×${r.count || 1}`;
    if (type === 'experience') return `经验 ${r.value || 0}`;
    if (type === 'traderstanding') return `${r.name || r.traderId} 好感 +${r.value || 0}`;
    if (type === 'traderunlock') return `解锁商人 ${r.name || r.traderId}`;
    if (type === 'assortmentunlock') return `直购权：${r.name || r.offerId || '货架商品'}`;
    if (type === 'productionscheme') return `配方：${r.name || r.recipeId || '藏身处制造'}`;
    return `${r.type} ${r.value || ''}`;
}

// ---- 奖励覆盖编辑弹层 ----
let REWARD_DRAFT = null; // { Started:[], Success:[], Fail:[] }
const QUEST_REWARD_TYPES = ['item', 'experience', 'traderStanding', 'traderUnlock', 'assortmentUnlock', 'productionScheme'];

function newQuestReward() {
    return { type: 'item', tpl: null, count: 1, value: 0, traderId: null, offerId: null, recipeId: null, name: null };
}

function normalizeQuestReward(r) {
    const rawType = r.type || 'item';
    const type = rawType.charAt(0).toLowerCase() + rawType.slice(1);
    return {
        type, tpl: r.tpl || null, count: r.count || 1, value: r.value || 0,
        traderId: r.traderId || null, offerId: r.offerId || null,
        recipeId: r.recipeId || null, name: r.name || null,
    };
}

function traderSelect(selected, onChange) {
    const select = document.createElement('select');
    select.innerHTML = '<option value="">选择商人</option>' + TRADERS.map(t =>
        `<option value="${esc(t.traderId)}"${t.traderId === selected ? ' selected' : ''}>${esc(t.name || t.traderId)}</option>`).join('');
    select.onchange = () => onChange(select.value);
    return select;
}

function attachAssortPicker(input, results, getTraderId, onPick) {
    let timer = null;
    async function run(q) {
        const traderId = getTraderId();
        if (!isTpl(traderId)) {
            results.innerHTML = '<div class="sr-none">请先选择商人</div>'; results.style.display = ''; return;
        }
        const r = await api(`/assorts?traderId=${encodeURIComponent(traderId)}&q=${encodeURIComponent(q)}&limit=20`);
        const rows = r.assorts || [];
        results.innerHTML = rows.length ? rows.map(a => `<div class="sr-item" data-offer-id="${esc(a.offerId)}" data-tpl="${esc(a.tpl)}" data-name="${esc(a.name || a.tpl)}">
            <img src="${ICON_API}${esc(a.tpl)}" class="sr-icon" alt="" onerror="this.remove()" />
            <span class="sr-name">${esc(a.name || a.tpl)}${a.unlockQuestId ? ' · 已有关联任务' : ''}</span>
            <span class="sr-tpl">LL${a.loyaltyLevel || 1} · ${esc(a.offerId).slice(0, 8)}…</span></div>`).join('') : '<div class="sr-none">无结果</div>';
        results.style.display = '';
        results.querySelectorAll('.sr-item').forEach(item => {
            item.onmousedown = e => { e.preventDefault(); results.style.display = 'none'; onPick(item.dataset); };
        });
    }
    input.setAttribute('autocomplete', 'off');
    input.oninput = () => { clearTimeout(timer); timer = setTimeout(() => run(input.value.trim()), 250); };
    input.onfocus = () => run(input.value.trim());
    input.onblur = () => setTimeout(() => { results.style.display = 'none'; }, 200);
}

function openRewardEditor(d) {
    // 以当前详情奖励为初始草稿（仅取受支持的类型字段）
    REWARD_DRAFT = { Started: [], Success: [], Fail: [] };
    REWARD_BUCKETS.forEach(b => {
        ((d.rewards && d.rewards[b]) || []).forEach(r => {
            const normalized = normalizeQuestReward(r);
            if (QUEST_REWARD_TYPES.includes(normalized.type)) {
                REWARD_DRAFT[b].push(normalized);
            }
        });
    });
    showRewardModal(d);
}

function showRewardModal(d) {
    closeModal();
    const overlay = document.createElement('div'); overlay.id = 'bp-modal'; overlay.className = 'bp-modal-overlay';
    overlay.innerHTML = `<div class="bp-modal-card">
        <div class="panel-head"><h2>编辑奖励覆盖 · ${esc(d.name || d.questId)}</h2></div>
        <div class="form-body" id="reward-editor-body"></div>
        <div class="gs-row" style="justify-content:flex-end;gap:8px">
            <button class="btn ghost small" id="reward-cancel">取消</button>
            <button class="btn primary small" id="reward-save">保存覆盖</button>
        </div></div>`;
    document.body.appendChild(overlay);
    el('reward-cancel').onclick = closeModal;
    el('reward-save').onclick = () => saveRewardOverride(d.questId);
    renderRewardEditor();
}

function renderRewardEditor() {
    const box = el('reward-editor-body'); box.innerHTML = '';
    REWARD_BUCKETS.forEach(bucket => {
        const sec = document.createElement('div'); sec.className = 'graph-sec';
        sec.innerHTML = `<div class="gs-head">${BUCKET_LABEL[bucket]}</div>`;
        REWARD_DRAFT[bucket].forEach((r, i) => sec.appendChild(rewardEditRow(bucket, r, i)));
        const add = document.createElement('button'); add.className = 'btn ghost small'; add.textContent = '+ 添加奖励';
        add.onclick = () => { REWARD_DRAFT[bucket].push(newQuestReward()); renderRewardEditor(); };
        sec.appendChild(add);
        box.appendChild(sec);
    });
}

function rewardEditRow(bucket, r, idx) {
    return questRewardRow(r, () => { REWARD_DRAFT[bucket].splice(idx, 1); renderRewardEditor(); }, renderRewardEditor);
}

function questRewardRow(r, onDelete, rerender) {
    const row = document.createElement('div'); row.className = 'gs-row'; row.style.flexWrap = 'wrap';
    const typeSel = document.createElement('select'); typeSel.innerHTML =
        `<option value="item">物品/货币</option><option value="experience">经验</option><option value="traderStanding">商人好感</option><option value="traderUnlock">解锁商人</option><option value="assortmentUnlock">商人直购权</option><option value="productionScheme">藏身处配方</option>`;
    typeSel.value = r.type || 'item';
    typeSel.onchange = () => { r.type = typeSel.value; rerender(); };
    row.appendChild(typeSel);

    if (r.type === 'item') {
        const wrap = document.createElement('span'); wrap.style.position = 'relative';
        const inp = document.createElement('input'); inp.placeholder = '搜索物品名/tpl'; inp.value = r.name || r.tpl || ''; inp.style.width = '150px';
        const res = document.createElement('div'); res.className = 'ip-results'; res.style.display = 'none';
        wrap.appendChild(inp); wrap.appendChild(res); row.appendChild(wrap);
        BpPicker.attachItem(inp, res, ds => { r.tpl = ds.tpl; r.name = ds.name; inp.value = ds.name || ds.tpl; });
        const cnt = document.createElement('input'); cnt.type = 'number'; cnt.value = r.count || 1; cnt.style.width = '70px'; cnt.title = '数量';
        cnt.oninput = () => r.count = +cnt.value || 1; row.appendChild(cnt);
    } else if (r.type === 'experience') {
        const v = document.createElement('input'); v.type = 'number'; v.value = r.value || 0; v.style.width = '110px'; v.title = '经验值';
        v.oninput = () => r.value = +v.value || 0; row.appendChild(v);
    } else if (r.type === 'traderStanding' || r.type === 'traderUnlock') {
        row.appendChild(traderSelect(r.traderId, value => { r.traderId = value || null; }));
        if (r.type === 'traderStanding') {
            const v = document.createElement('input'); v.type = 'number'; v.step = '0.01'; v.value = r.value || 0; v.style.width = '80px'; v.title = '好感增量';
            v.oninput = () => r.value = +v.value || 0; row.appendChild(v);
        }
    } else if (r.type === 'assortmentUnlock') {
        row.appendChild(traderSelect(r.traderId, value => { r.traderId = value || null; r.offerId = null; r.tpl = null; r.name = null; rerender(); }));
        const wrap = document.createElement('span'); wrap.style.position = 'relative';
        const inp = document.createElement('input'); inp.placeholder = '搜索该商人货架商品'; inp.value = r.name || r.offerId || ''; inp.style.width = '190px';
        const res = document.createElement('div'); res.className = 'ip-results'; res.style.display = 'none';
        wrap.appendChild(inp); wrap.appendChild(res); row.appendChild(wrap);
        attachAssortPicker(inp, res, () => r.traderId, ds => {
            r.offerId = ds.offerId; r.tpl = ds.tpl; r.name = ds.name; inp.value = ds.name || ds.offerId;
        });
    } else if (r.type === 'productionScheme') {
        const wrap = document.createElement('span'); wrap.style.position = 'relative';
        const inp = document.createElement('input'); inp.placeholder = '搜索藏身处配方'; inp.value = r.name || r.recipeId || ''; inp.style.width = '190px';
        const res = document.createElement('div'); res.className = 'ip-results'; res.style.display = 'none';
        wrap.appendChild(inp); wrap.appendChild(res); row.appendChild(wrap);
        BpPicker.attachRecipe(inp, res, ds => { r.recipeId = ds.id; r.tpl = ds.tpl; r.name = ds.name; inp.value = ds.name || ds.id; }, { minChars: 0, questUnlockOnly: true });
    }
    const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '删';
    del.onclick = onDelete;
    row.appendChild(del);
    return row;
}

async function saveRewardOverride(questId) {
    // 组装 override：仅带非空桶
    const rewards = {};
    let err = null;
    REWARD_BUCKETS.forEach(b => {
        const list = REWARD_DRAFT[b].filter(Boolean);
        list.forEach(r => {
            err = err || validateQuestReward(r);
        });
        if (list.length) rewards[b] = list;
    });
    if (err) { toast(err, false); return; }
    // 需要读取当前禁用状态一并写入（override 是整条覆盖）
    const cur = await api(`/detail/${questId}`);
    const disabled = cur.success ? !!cur.detail.disabled : false;
    const ov = { questId, disabled, rewards };
    if (isCollaborator()) {
        const sr = await submitChange('quests', 'quest.override', ov);
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        if (sr.success) closeModal();
        return;
    }
    const r = await api('/override', 'POST', ov);
    toast(r.success ? '奖励覆盖已保存' : (r.message || '失败'), r.success);
    if (r.success) { closeModal(); selectQuest(questId); }
}

async function clearRewardOverride(questId) {
    if (!confirm('还原该任务的原版奖励？（保留禁用状态）')) return;
    const cur = await api(`/detail/${questId}`);
    const disabled = cur.success ? !!cur.detail.disabled : false;
    const ov = { questId, disabled, rewards: {} };
    if (isCollaborator()) {
        const sr = await submitChange('quests', 'quest.override', ov);
        toast(sr.success ? '已提交审核' : (sr.message || '提交失败'), sr.success);
        return;
    }
    const r = await api('/override', 'POST', ov);
    toast(r.success ? '已还原原版奖励' : (r.message || '失败'), r.success);
    if (r.success) selectQuest(questId);
}

// ---- 启用/禁用切换 ----
async function toggleDisabled(d) {
    const next = !d.disabled;
    if (next && !confirm(`禁用「${d.name || d.questId}」？该任务将从玩家任务列表移除（可随时启用还原，即时生效）。`)) return;
    // 保留已有奖励覆盖
    const rewards = d.hasRewardOverride ? collectExistingOverride(d) : {};
    const ov = { questId: d.questId, disabled: next, rewards };
    if (isCollaborator()) {
        const sr = await submitChange('quests', 'quest.override', ov);
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        return;
    }
    const r = await api('/override', 'POST', ov);
    toast(r.success ? (next ? '已禁用' : '已启用') : (r.message || '失败'), r.success);
    if (r.success) selectQuest(d.questId);
}

function collectExistingOverride(d) {
    const rewards = {};
    REWARD_BUCKETS.forEach(b => {
        const list = ((d.rewards && d.rewards[b]) || []).map(r => {
            const type = (r.type || 'Item'); const t = type.charAt(0).toLowerCase() + type.slice(1);
            return normalizeQuestReward({ ...r, type: t });
        }).filter(r => QUEST_REWARD_TYPES.includes(r.type));
        if (list.length) rewards[b] = list;
    });
    return rewards;
}

function validateQuestReward(r) {
    if (r.type === 'item' && !isTpl(r.tpl)) return '物品奖励需选择有效物品';
    if ((r.type === 'traderStanding' || r.type === 'traderUnlock') && !isTpl(r.traderId)) return '商人奖励需选择有效商人';
    if (r.type === 'assortmentUnlock' && (!isTpl(r.traderId) || !isTpl(r.offerId))) return '直购权奖励需选择商人及其货架商品';
    if (r.type === 'productionScheme' && !isTpl(r.recipeId)) return '配方奖励需选择有效藏身处配方';
    return null;
}

// ---- 依赖图谱（SVG 简版）----
async function showGraph(traderId) {
    const r = await api(`/graph?traderId=${encodeURIComponent(traderId || '')}`);
    if (!r.success) { toast(r.message || '加载图谱失败', false); return; }
    renderGraphModal(r.graph, traderId);
}

function renderGraphModal(g, traderId) {
    closeModal();
    const overlay = document.createElement('div'); overlay.id = 'bp-modal'; overlay.className = 'bp-modal-overlay';
    const nodes = g.nodes || []; const edges = g.edges || [];
    // 简易分层：按入度（前置数）分列
    const indeg = {}; nodes.forEach(n => indeg[n.questId] = 0);
    edges.filter(e => e.kind === 'unlock').forEach(e => { if (indeg[e.to] != null) indeg[e.to]++; });
    const rows = nodes.map(n => {
        const cls = n.disabled ? 'q-disabled' : (n.isCustom ? 'q-custom' : (n.external ? 'q-external' : 'q-normal'));
        const outFail = edges.filter(e => e.kind === 'fail' && e.from === n.questId).map(e => e.to);
        const preList = edges.filter(e => e.kind === 'unlock' && e.to === n.questId).map(e => e.from);
        return `<div class="graph-node ${cls}" data-qid="${esc(n.questId)}">
            <div class="gn-name">${esc(n.name || n.questId)}${n.external ? ' <span class="badge">外部</span>' : ''}${n.disabled ? ' <span class="badge bl">禁</span>' : ''}</div>
            <div class="meta">前置 ${preList.length}${outFail.length ? ` · 互斥 ${outFail.length}` : ''}</div>
        </div>`;
    }).join('');
    overlay.innerHTML = `<div class="bp-modal-card wide">
        <div class="panel-head"><h2>任务依赖图谱 · ${esc(traderName(traderId) || '全部')}（${nodes.length} 节点 / ${edges.length} 边）</h2></div>
        <div class="form-body">
            <p class="hint">节点色：灰=已禁用，绿=自定义，蓝=外部商人前置。点击节点查看详情。unlock=解锁链，fail=完成使对方失败。</p>
            <div class="graph-nodes">${rows || '<p class="hint">无任务</p>'}</div>
        </div>
        <div class="gs-row" style="justify-content:flex-end"><button class="btn ghost small" id="graph-close">关闭</button></div>
    </div>`;
    document.body.appendChild(overlay);
    el('graph-close').onclick = closeModal;
    overlay.querySelectorAll('.graph-node').forEach(nd => {
        nd.onclick = () => { const qid = nd.dataset.qid; closeModal(); selectQuest(qid); };
    });
}

function closeModal() { const m = el('bp-modal'); if (m) m.remove(); }

// ---- 自定义任务新建 / 编辑 ----
let CUSTOM_DRAFT = null;

function newCustomDraft() {
    return {
        id: '', traderId: CUR_TRADER || (TRADERS[0] && TRADERS[0].traderId) || '',
        questName: '', nameZh: '', descriptionZh: '', side: 'Pmc', location: 'any',
        prerequisites: [], objectives: [newObjective('handoverItem')],
        startedRewards: [], rewards: [newQuestReward()],
    };
}

function newObjective(type) {
    return {
        type: type || 'handoverItem', tpl: null, count: 1, onlyFoundInRaid: false,
        target: 'Any', targets: [], locations: [], weapons: [], weaponMods: [], weaponCalibers: [],
        savageRoles: [], bodyParts: [], distance: null, daytime: null, oneLife: false,
        dependsOnPrevious: false, containsItems: [], hasItemFromCategory: [],
        baseAccuracy: null, durability: null, effectiveDistance: null, emptyTacticalSlot: null,
        ergonomics: null, height: null, magazineCapacity: null, muzzleVelocity: null,
        recoil: null, weight: null, width: null, textZh: null, textEn: null, note: null, name: null,
    };
}

function normalizeObjective(o) {
    const result = { ...newObjective(o.type || 'handoverItem'), ...o };
    ['targets', 'locations', 'weapons', 'weaponMods', 'weaponCalibers', 'savageRoles', 'bodyParts', 'containsItems', 'hasItemFromCategory']
        .forEach(key => result[key] = Array.isArray(o[key]) ? [...o[key]] : []);
    return result;
}

async function openCustomForm(questId) {
    if (questId) {
        // 编辑：从持久化的自定义任务列表取原始草稿
        const r = await api('/custom');
        const found = (r.quests || []).find(c => c.id === questId);
        if (!found) { toast('未找到该自定义任务的可编辑数据', false); return; }
        CUSTOM_DRAFT = normalizeDraft(found);
    } else {
        CUSTOM_DRAFT = newCustomDraft();
    }
    showCustomModal();
}

function normalizeDraft(c) {
    return {
        id: c.id || '', traderId: c.traderId || '', questName: c.questName || '', nameZh: c.nameZh || '',
        descriptionZh: c.descriptionZh || '', side: c.side || 'Pmc', location: c.location || 'any',
        prerequisites: (c.prerequisites || []).map(p => ({ questId: p.questId || '', status: p.status || [4], availableAfter: p.availableAfter || 0 })),
        objectives: (c.objectives || []).map(normalizeObjective),
        startedRewards: (c.startedRewards || []).map(normalizeQuestReward),
        rewards: (c.rewards || []).map(normalizeQuestReward),
    };
}

function showCustomModal() {
    closeModal();
    const overlay = document.createElement('div'); overlay.id = 'bp-modal'; overlay.className = 'bp-modal-overlay';
    overlay.innerHTML = `<div class="bp-modal-card wide">
        <div class="panel-head"><h2>${CUSTOM_DRAFT.id ? '编辑' : '新建'}自定义商人任务</h2></div>
        <div class="form-body" id="custom-body"></div>
        <div class="gs-row" style="justify-content:flex-end;gap:8px">
            <button class="btn ghost small" id="custom-cancel">取消</button>
            <button class="btn primary small" id="custom-save">保存</button>
        </div></div>`;
    document.body.appendChild(overlay);
    el('custom-cancel').onclick = closeModal;
    el('custom-save').onclick = saveCustom;
    renderCustomForm();
}

function renderCustomForm() {
    const d = CUSTOM_DRAFT; const box = el('custom-body'); box.innerHTML = '';
    const traderOpts = TRADERS.map(t => `<option value="${esc(t.traderId)}"${t.traderId === d.traderId ? ' selected' : ''}>${esc(t.name)}</option>`).join('');
    const basic = document.createElement('div'); basic.className = 'graph-sec';
    basic.innerHTML = `<div class="gs-head">基本信息</div>
        <label>归属商人</label><select id="c-trader">${traderOpts}</select>
        <label>中文任务名 *</label><input id="c-nameZh" value="${esc(d.nameZh)}" placeholder="如：军械库补给" />
        <label>中文任务描述</label><textarea id="c-descZh" rows="2" placeholder="任务背景描述">${esc(d.descriptionZh)}</textarea>
        <label>内部标识 QuestName（可留空）</label><input id="c-questName" value="${esc(d.questName)}" />
        <label>阵营</label><select id="c-side"><option value="Pmc"${d.side === 'Pmc' ? ' selected' : ''}>PMC(全部)</option><option value="Usec"${d.side === 'Usec' ? ' selected' : ''}>仅 USEC</option><option value="Bear"${d.side === 'Bear' ? ' selected' : ''}>仅 BEAR</option></select>`;
    box.appendChild(basic);

    // 前置任务
    const preSec = document.createElement('div'); preSec.className = 'graph-sec';
    preSec.innerHTML = '<div class="gs-head">前置任务（可选）</div>';
    d.prerequisites.forEach((p, i) => preSec.appendChild(prereqRow(p, i)));
    const addPre = document.createElement('button'); addPre.className = 'btn ghost small'; addPre.textContent = '+ 添加前置';
    addPre.onclick = () => { d.prerequisites.push({ questId: '', status: [4], availableAfter: 0 }); renderCustomForm(); };
    preSec.appendChild(addPre); box.appendChild(preSec);

    // 完成目标
    const objSec = document.createElement('div'); objSec.className = 'graph-sec';
    objSec.innerHTML = '<div class="gs-head">完成条件 *</div>';
    d.objectives.forEach((o, i) => objSec.appendChild(objectiveRow(o, i)));
    const addObj = document.createElement('button'); addObj.className = 'btn ghost small'; addObj.textContent = '+ 添加目标';
    addObj.onclick = () => { d.objectives.push(newObjective('handoverItem')); renderCustomForm(); };
    objSec.appendChild(addObj); box.appendChild(objSec);

    box.appendChild(customRewardSection('接取任务发放（Started）', 'startedRewards'));
    box.appendChild(customRewardSection('完成奖励（Success）', 'rewards'));

    el('c-trader').onchange = e => d.traderId = e.target.value;
    el('c-nameZh').oninput = e => d.nameZh = e.target.value;
    el('c-descZh').oninput = e => d.descriptionZh = e.target.value;
    el('c-questName').oninput = e => d.questName = e.target.value;
    el('c-side').onchange = e => d.side = e.target.value;
}

function customRewardSection(title, key) {
    const sec = document.createElement('div'); sec.className = 'graph-sec';
    sec.innerHTML = `<div class="gs-head">${esc(title)}</div>`;
    CUSTOM_DRAFT[key].forEach((reward, index) => sec.appendChild(questRewardRow(
        reward,
        () => { CUSTOM_DRAFT[key].splice(index, 1); renderCustomForm(); },
        renderCustomForm)));
    const add = document.createElement('button'); add.className = 'btn ghost small'; add.textContent = '+ 添加奖励';
    add.onclick = () => { CUSTOM_DRAFT[key].push(newQuestReward()); renderCustomForm(); };
    sec.appendChild(add);
    return sec;
}

function prereqRow(p, idx) {
    const row = document.createElement('div'); row.className = 'gs-row'; row.style.flexWrap = 'wrap';
    const wrap = document.createElement('span'); wrap.style.position = 'relative';
    const inp = document.createElement('input'); inp.placeholder = '搜索前置任务'; inp.value = p.questId || ''; inp.style.width = '180px';
    const res = document.createElement('div'); res.className = 'ip-results'; res.style.display = 'none';
    wrap.appendChild(inp); wrap.appendChild(res); row.appendChild(wrap);
    attachVanillaQuestPicker(inp, res, ds => { p.questId = ds.questId; inp.value = ds.name || ds.questId; });
    inp.oninput = () => { if (isTpl(inp.value.trim())) p.questId = inp.value.trim(); };
    const stSel = document.createElement('select'); stSel.title = '需达到状态';
    stSel.innerHTML = `<option value="4">需完成</option><option value="2">需已接取</option>`;
    stSel.value = String((p.status && p.status[0]) || 4);
    stSel.onchange = () => p.status = [+stSel.value]; row.appendChild(stSel);
    const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '删';
    del.onclick = () => { CUSTOM_DRAFT.prerequisites.splice(idx, 1); renderCustomForm(); };
    row.appendChild(del);
    return row;
}

// 原版任务自动补全（走本模块 /list，非 BP 任务库）
function attachVanillaQuestPicker(input, results, onPick) {
    let timer = null;
    input.setAttribute('autocomplete', 'off');
    async function run(q) {
        const r = await api(`/list?q=${encodeURIComponent(q)}`);
        const list = (r.quests || []).slice(0, 12);
        if (!list.length) { results.innerHTML = '<div class="sr-none">无结果</div>'; results.style.display = ''; return; }
        results.innerHTML = list.map(it =>
            `<div class="sr-item" data-qid="${esc(it.questId)}" data-name="${esc(it.name || it.questId)}">
                <span class="sr-name">${esc(it.name || it.questId)}</span>
                <span class="sr-tpl">${esc(it.traderName || '')}</span></div>`).join('');
        results.style.display = '';
        results.querySelectorAll('.sr-item').forEach(item => {
            item.onmousedown = e => { e.preventDefault(); results.style.display = 'none'; onPick({ questId: item.dataset.qid, name: item.dataset.name }); };
        });
    }
    input.addEventListener('input', () => {
        clearTimeout(timer);
        const q = input.value.trim();
        const need = /[㐀-鿿]/.test(q) ? 1 : 2;
        if (q.length < need) { results.style.display = 'none'; return; }
        timer = setTimeout(() => run(q), 250);
    });
    input.addEventListener('blur', () => setTimeout(() => { results.style.display = 'none'; }, 200));
}

function objectiveRow(o, idx) {
    const row = document.createElement('div'); row.className = 'quest-objective-card';
    const head = document.createElement('div'); head.className = 'gs-row'; head.style.flexWrap = 'wrap';
    const typeSel = document.createElement('select');
    typeSel.innerHTML = `<option value="handoverItem">上交物品</option><option value="weaponAssembly">上交指定改装枪械</option><option value="kills">条件击杀</option><option value="transit">地图转移</option>`;
    typeSel.value = o.type; typeSel.onchange = () => {
        CUSTOM_DRAFT.objectives[idx] = {
            ...newObjective(typeSel.value),
            count: o.count || 1,
            dependsOnPrevious: idx > 0 && !!o.dependsOnPrevious,
            textZh: o.textZh || null,
            textEn: o.textEn || null,
            note: o.note || null,
        };
        renderCustomForm();
    };
    head.appendChild(typeSel);
    const cnt = document.createElement('input'); cnt.type = 'number'; cnt.min = '1'; cnt.value = o.count || 1; cnt.style.width = '72px'; cnt.title = '要求数量';
    cnt.oninput = () => o.count = Math.max(1, +cnt.value || 1); head.appendChild(cnt);
    if (idx > 0) {
        const dep = document.createElement('label'); dep.className = 'inline-check';
        const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !!o.dependsOnPrevious; cb.onchange = () => o.dependsOnPrevious = cb.checked;
        dep.appendChild(cb); dep.appendChild(document.createTextNode('前一目标完成后启用')); head.appendChild(dep);
    }
    const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '删';
    del.onclick = () => { CUSTOM_DRAFT.objectives.splice(idx, 1); renderCustomForm(); }; head.appendChild(del);
    row.appendChild(head);

    const body = document.createElement('div'); body.className = 'quest-objective-body'; row.appendChild(body);
    if (o.type === 'handoverItem') {
        body.appendChild(singleItemPicker(o, 'tpl', 'name', null, '搜索需要上交的物品'));
        const fir = document.createElement('label'); fir.style.cssText = 'display:inline-flex;align-items:center;gap:4px';
        const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !!o.onlyFoundInRaid; cb.onchange = () => o.onlyFoundInRaid = cb.checked;
        fir.appendChild(cb); fir.appendChild(document.createTextNode('战局中找到')); body.appendChild(fir);
    } else if (o.type === 'weaponAssembly') {
        const grid = document.createElement('div'); grid.className = 'quest-field-grid'; body.appendChild(grid);
        appendLabeled(grid, '基础枪械', singleItemPicker(o, 'tpl', 'name', 'weapon', '搜索枪械'));
        appendLabeled(grid, '必须安装的配件', multiItemPicker(o.containsItems, 'weaponMod', '搜索并添加配件'));
        const stats = document.createElement('div'); stats.className = 'quest-stat-grid'; body.appendChild(stats);
        [
            ['durability', '耐久度', '>='], ['ergonomics', '人机工效', '>='], ['recoil', '后坐力', '<='],
            ['weight', '重量', '<='], ['height', '高度格', '<='], ['width', '宽度格', '<='],
            ['magazineCapacity', '弹匣容量', '>='], ['muzzleVelocity', '初速', '>='],
            ['effectiveDistance', '有效射程', '>='], ['emptyTacticalSlot', '空战术槽', '>='], ['baseAccuracy', '基础精度', '>='],
        ].forEach(([key, label, method]) => stats.appendChild(compareEditor(o, key, label, method)));
    } else if (o.type === 'kills') {
        renderKillObjective(body, o);
    } else if (o.type === 'transit') {
        appendLabeled(body, '从这些地图进行 Transit', locationSelect(o.locations));
        const hint = document.createElement('p'); hint.className = 'hint'; hint.textContent = '与“前一目标完成后启用”组合，可创建：地图击杀 → 转移 → 下一地图击杀的原版阶段链。'; body.appendChild(hint);
    }

    const localeGrid = document.createElement('div'); localeGrid.className = 'quest-field-grid';
    const textZh = document.createElement('textarea'); textZh.rows = 2; textZh.maxLength = 300;
    textZh.placeholder = '游戏内显示的中文目标文本；留空则自动生成';
    textZh.value = o.textZh || '';
    textZh.oninput = () => o.textZh = textZh.value;
    appendLabeled(localeGrid, '中文目标文本', textZh);
    const textEn = document.createElement('textarea'); textEn.rows = 2; textEn.maxLength = 300;
    textEn.placeholder = '可选；留空时回退中文文本';
    textEn.value = o.textEn || '';
    textEn.oninput = () => o.textEn = textEn.value;
    appendLabeled(localeGrid, '英文目标文本（可选）', textEn);
    body.appendChild(localeGrid);

    const localeHint = document.createElement('p'); localeHint.className = 'hint';
    localeHint.textContent = '文本会写入该目标 condition ID 的本地化键；留空也不会向客户端显示 tpl。';
    body.appendChild(localeHint);
    return row;
}

function renderKillObjective(body, o) {
    const grid = document.createElement('div'); grid.className = 'quest-field-grid'; body.appendChild(grid);
    const sides = QUEST_CATALOG.killTargets.filter(t => t.kind === 'side');
    const target = document.createElement('select');
    target.innerHTML = sides.map(t => `<option value="${esc(t.value)}"${t.value === (o.target || 'Any') ? ' selected' : ''}>${esc(t.name || t.value)}</option>`).join('');
    target.onchange = () => o.target = target.value; appendLabeled(grid, '击杀目标', target);
    const roles = QUEST_CATALOG.killTargets.filter(t => t.kind === 'savageRole').map(t => ({ value: t.value, name: t.name }));
    appendLabeled(grid, '指定 Scav/Boss 角色（可多选）', multiSelect(roles, o.savageRoles, values => o.savageRoles = values, 5));
    appendLabeled(grid, '指定地图（可多选）', locationSelect(o.locations));
    appendLabeled(grid, '指定枪械', multiItemPicker(o.weapons, 'weapon', '搜索并添加枪械'));
    appendLabeled(grid, '必须安装的配件', multiItemPicker(o.weaponMods, 'weaponMod', '搜索并添加配件'));
    const calibers = ['Caliber9x18PM','Caliber9x19PARA','Caliber9x21','Caliber45ACP','Caliber46x30','Caliber57x28','Caliber545x39','Caliber556x45NATO','Caliber762x25TT','Caliber762x35','Caliber762x39','Caliber762x51','Caliber762x54R','Caliber9x39','Caliber12g','Caliber20g','Caliber23x75'];
    appendLabeled(grid, '口径（可多选）', multiSelect(calibers.map(v => ({ value: v, name: v })), o.weaponCalibers, values => o.weaponCalibers = values, 4));
    const parts = ['Head','Chest','Stomach','LeftArm','RightArm','LeftLeg','RightLeg'];
    appendLabeled(grid, '致命部位（可多选）', multiSelect(parts.map(v => ({ value: v, name: v })), o.bodyParts, values => o.bodyParts = values, 4));
    appendLabeled(grid, '击杀距离', compareEditor(o, 'distance', '', '>='));
    appendLabeled(grid, '游戏内时段', daytimeEditor(o));
    const oneLife = document.createElement('label'); oneLife.className = 'inline-check';
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !!o.oneLife; cb.onchange = () => o.oneLife = cb.checked;
    oneLife.appendChild(cb); oneLife.appendChild(document.createTextNode('一命/同一战局完成（死亡或离局会重置未完成计数）')); body.appendChild(oneLife);
}

function appendLabeled(parent, label, control) {
    const wrap = document.createElement('label'); wrap.className = 'quest-field';
    const title = document.createElement('span'); title.textContent = label; wrap.appendChild(title); wrap.appendChild(control); parent.appendChild(wrap);
    return wrap;
}

function singleItemPicker(owner, valueKey, nameKey, category, placeholder) {
    const wrap = document.createElement('span'); wrap.className = 'quest-picker';
    const input = document.createElement('input'); input.placeholder = placeholder; input.value = owner[nameKey] || owner[valueKey] || '';
    const results = document.createElement('div'); results.className = 'ip-results'; results.style.display = 'none';
    wrap.appendChild(input); wrap.appendChild(results);
    BpPicker.attachItem(input, results, ds => { owner[valueKey] = ds.tpl; owner[nameKey] = ds.name; input.value = ds.name || ds.tpl; }, category ? { category } : undefined);
    return wrap;
}

function multiItemPicker(values, category, placeholder) {
    const wrap = document.createElement('div'); wrap.className = 'quest-multi-picker';
    const input = document.createElement('input'); input.placeholder = placeholder;
    const results = document.createElement('div'); results.className = 'ip-results'; results.style.display = 'none';
    const chips = document.createElement('div'); chips.className = 'multi-chips';
    wrap.appendChild(input); wrap.appendChild(results); wrap.appendChild(chips);
    const render = () => {
        chips.innerHTML = '';
        values.forEach((value, index) => {
            const chip = document.createElement('span'); chip.className = 'multi-chip'; chip.textContent = value;
            const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = '×'; remove.onclick = () => { values.splice(index, 1); render(); };
            chip.appendChild(remove); chips.appendChild(chip);
        });
    };
    BpPicker.attachItem(input, results, ds => { if (!values.includes(ds.tpl)) values.push(ds.tpl); input.value = ''; render(); }, { category });
    render(); return wrap;
}

function multiSelect(options, selected, onChange, size) {
    const select = document.createElement('select'); select.multiple = true; select.size = size || 5;
    select.innerHTML = options.map(option => `<option value="${esc(option.value)}"${selected.includes(option.value) ? ' selected' : ''}>${esc(option.name || option.value)}</option>`).join('');
    select.onchange = () => onChange(Array.from(select.selectedOptions).map(option => option.value));
    return select;
}

function locationSelect(selected) {
    return multiSelect(QUEST_CATALOG.locations.map(location => ({ value: location.value, name: `${location.name} (${location.value})` })), selected, values => {
        selected.splice(0, selected.length, ...values);
    }, 5);
}

function compareEditor(owner, key, label, defaultMethod) {
    const wrap = document.createElement('span'); wrap.className = 'compare-editor';
    if (label) { const name = document.createElement('span'); name.textContent = label; wrap.appendChild(name); }
    const method = document.createElement('select');
    method.innerHTML = '<option value="">不限</option><option value=">=">≥</option><option value=">">&gt;</option><option value="<=">≤</option><option value="<">&lt;</option><option value="=">=</option>';
    method.value = owner[key]?.compareMethod || '';
    const value = document.createElement('input'); value.type = 'number'; value.step = '0.1'; value.min = '0'; value.value = owner[key]?.value ?? ''; value.placeholder = '数值';
    method.onchange = () => { owner[key] = method.value ? { compareMethod: method.value, value: +value.value || 0 } : null; };
    value.oninput = () => { if (!method.value) method.value = defaultMethod; owner[key] = { compareMethod: method.value || defaultMethod, value: +value.value || 0 }; };
    wrap.appendChild(method); wrap.appendChild(value); return wrap;
}

function daytimeEditor(owner) {
    const wrap = document.createElement('span'); wrap.className = 'daytime-editor';
    const from = document.createElement('select'); const to = document.createElement('select');
    const options = '<option value="">不限</option>' + Array.from({ length: 24 }, (_, hour) => `<option value="${hour}">${String(hour).padStart(2, '0')}:00</option>`).join('');
    from.innerHTML = options; to.innerHTML = options; from.value = owner.daytime?.from ?? ''; to.value = owner.daytime?.to ?? '';
    const sync = () => { owner.daytime = from.value === '' || to.value === '' ? null : { from: +from.value, to: +to.value }; };
    from.onchange = sync; to.onchange = sync; wrap.appendChild(from); wrap.appendChild(document.createTextNode(' 至 ')); wrap.appendChild(to); return wrap;
}

async function saveCustom() {
    const d = CUSTOM_DRAFT;
    if (!d.nameZh.trim()) return toast('请填写中文任务名', false);
    if (!isTpl(d.traderId)) return toast('请选择归属商人', false);
    if (!d.objectives.length) return toast('至少需要一个完成目标', false);
    for (let index = 0; index < d.objectives.length; index++) {
        const o = d.objectives[index];
        if ((o.type === 'handoverItem' || o.type === 'weaponAssembly') && !isTpl(o.tpl)) return toast('上交/枪匠目标需选择有效物品或枪械', false);
        if (o.type === 'transit' && !o.locations.length) return toast('转移目标至少选择一个起始地图', false);
        if (index === 0 && o.dependsOnPrevious) return toast('第一个目标不能依赖前一目标', false);
    }
    for (const r of [...d.startedRewards, ...d.rewards]) {
        const error = validateQuestReward(r); if (error) return toast(error, false);
    }
    for (const p of d.prerequisites) {
        if (!isTpl(p.questId)) return toast('前置任务需选择有效任务', false);
    }
    if (isCollaborator()) {
        const sr = await submitChange('quests', 'quest.customUpsert', d);
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        if (sr.success) closeModal();
        return;
    }
    const r = await api('/custom', 'POST', d);
    toast(r.success ? '自定义任务已保存' : (r.message || '失败'), r.success);
    if (r.success) { closeModal(); await doSearch(); }
}

async function deleteCustom(questId, name) {
    if (!confirm(`删除自定义任务「${name || questId}」？删除后即时从游戏移除（不影响原版任务）。`)) return;
    if (isCollaborator()) {
        const sr = await submitChange('quests', 'quest.customDelete', { id: questId });
        toast(sr.success ? '已提交审核，等待管理员批准' : (sr.message || '提交失败'), sr.success);
        return;
    }
    const r = await api('/custom', 'DELETE', { id: questId });
    toast(r.success ? '已删除' : (r.message || '失败'), r.success);
    if (r.success) { SELECTED = null; el('detail-body').innerHTML = '<p class="hint">已删除。</p>'; await doSearch(); }
}

async function applyPendingQuestEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    const payload = change.proposedPayload || {};
    if (change.commandType === 'quest.customUpsert') {
        CUSTOM_DRAFT = normalizeDraft(payload);
        showCustomModal();
        return;
    }

    if (change.commandType === 'quest.customDelete') {
        toast('请选择新的自定义任务并点击删除，以更新原删除审核单', true);
        return;
    }

    if (change.commandType === 'quest.override' && payload.questId) {
        await selectQuest(payload.questId);
        toast('已定位到待审原版任务覆盖；重新执行禁用/改奖会更新原审核单', true);
    }
}

// ---- 事件绑定 + 启动 ----
el('admin-login-btn').onclick = doAdminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doAdminLogin(); });
el('admin-logout').onclick = logout;
el('search-btn').onclick = doSearch;
el('q').addEventListener('keydown', e => { if (e.key === 'Enter') doSearch(); });
el('q-trader').addEventListener('change', doSearch);
el('new-quest-btn').onclick = () => openCustomForm(null);
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

bootstrapAdminPage({ moduleCap: 'quests.read', onReady: async edit => { ADMIN_TOKEN = getAdminToken(); await enterConsole(); await applyPendingQuestEdit(edit); } });
