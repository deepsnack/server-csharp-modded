'use strict';

const ADMIN_API = '/battlepass/api/admin';
const ITEMS_SEARCH = '/battlepass/api/admin/items/search';
const ICON_API = '/battlepass/api/icons/';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

(function () {
    const m = location.hash.match(/sso=([a-zA-Z0-9]+)/);
    if (m) { ADMIN_TOKEN = m[1]; sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN); history.replaceState(null, '', location.pathname); }
})();

// ---- 地图映射 ----
const MAP_NAMES = [
    ['', '不限 / 自定义'],
    ['bigmap', '海关 (Customs)'],
    ['factory4_day', '工厂·昼 (Factory Day)'],
    ['factory4_night', '工厂·夜 (Factory Night)'],
    ['Woods', '森林 (Woods)'],
    ['Shoreline', '海岸线 (Shoreline)'],
    ['RezervBase', '储备站 (Reserve)'],
    ['Interchange', '立交桥 (Interchange)'],
    ['laboratory', '实验室 (Labs)'],
    ['Lighthouse', '灯塔 (Lighthouse)'],
    ['TarkovStreets', '街区 (Streets)'],
    ['Sandbox', '机房 (Ground Zero)'],
];

function el(id) { return document.getElementById(id); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }
function randHex(n) { const a = new Uint8Array(Math.ceil(n / 2)); crypto.getRandomValues(a); return Array.from(a, b => b.toString(16).padStart(2, '0')).join('').slice(0, n); }

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN };
    const res = await fetch(ADMIN_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// ---- 登录 ----
async function adminLogin() {
    const password = el('admin-pass').value;
    const res = await fetch(REGISTER_ADMIN_LOGIN, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password }) });
    const r = await res.json();
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    ADMIN_TOKEN = r.token;
    sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN);
    enterConsole();
}
function logout() { ADMIN_TOKEN = ''; sessionStorage.removeItem('bp_admin_token'); el('tasks-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
function enterConsole() { el('login-view').classList.add('hidden'); el('tasks-view').classList.remove('hidden'); loadTasks(); }

// ---- 类型卡片 ----
let currentConditionType = 'Kills';
document.querySelectorAll('.type-card').forEach(card => {
    card.onclick = () => {
        document.querySelectorAll('.type-card').forEach(c => c.classList.remove('active'));
        card.classList.add('active');
        currentConditionType = card.dataset.ct;
        renderTargetSection();
        el('kills-fine').style.display = currentConditionType === 'Kills' ? '' : 'none';
        el('f-id').value = el('f-id').value || genTaskId();
    };
});

function genTaskId() {
    const scope = el('f-scope').value;
    const type = currentConditionType.toLowerCase().replace(/[^a-z0-9]/g, '');
    return `${scope}_${type}_${randHex(8)}`;
}

// ---- ID 自动生成 ----
el('gen-id-btn').onclick = () => { el('f-id').value = genTaskId(); toast('ID 已生成', true); };
el('f-scope').addEventListener('change', () => { if (!el('f-id').value) el('f-id').value = genTaskId(); });

// ---- 物品选择器（统一组件 BpPicker，见 picker.js）----
function setupItemPicker(idx) {
    const input = el(`fi-tpl-${idx}`);
    const results = el(`fi-results-${idx}`);
    BpPicker.attachItem(input, results, ds => pickItem(idx, ds.tpl, ds.name), { limit: 8 });
    // 输入 24 位 tpl 时即时显示图标
    input.addEventListener('input', () => {
        const q = input.value.trim();
        if (BpPicker.isTpl(q)) {
            const icon = el(`fi-icon-${idx}`);
            icon.src = BpPicker.ICON_API + q; icon.style.display = 'inline-block';
            el(`fi-name-${idx}`).textContent = q;
        }
    });
}

function pickItem(idx, tpl, name) {
    el(`fi-tpl-${idx}`).value = tpl;
    el(`fi-name-${idx}`).textContent = name || tpl;
    const icon = el(`fi-icon-${idx}`);
    icon.src = ICON_API + tpl;
    icon.style.display = 'inline-block';
    icon.onerror = () => { icon.style.display = 'none'; };
    el(`fi-results-${idx}`).style.display = 'none';
}

// ---- 目标配置区（按类型动态渲染）----
function renderTargetSection() {
    const ct = currentConditionType;
    const section = el('target-section');
    if (ct === 'Kills') {
        section.innerHTML = `<div class="form-row two-col">
            <div class="form-col"><label>数量</label><input id="f-count" type="number" value="3" /></div>
            <div class="form-col"><label>地图</label>${mapSelect('f-location')}</div>
        </div>`;
    } else if (ct === 'Exploration') {
        section.innerHTML = `<div class="form-row two-col">
            <div class="form-col"><label>撤离次数</label><input id="f-count" type="number" value="3" /></div>
            <div class="form-col"><label>地图（留空=不限）</label>${mapSelect('f-location')}</div>
        </div>`;
    } else if (ct === 'HandoverItem' || ct === 'FindItem') {
        const label = ct === 'HandoverItem' ? '需上交的物品' : '需找到的物品';
        const showFindInRaid = ct === 'FindItem' ? '' : '';
        section.innerHTML = `<div class="item-req-block" id="item-req-block">
            ${itemPickerHtml(0, label)}
            <div class="form-row three-col">
                <div class="form-col"><label>数量</label><input id="f-count" type="number" value="1" /></div>
                <div class="form-col"><label>地图（可空）</label>${mapSelect('f-location')}</div>
                <div class="form-col"><label class="with-cb"><input id="f-find-in-raid" type="checkbox" /> 仅限战局中找到</label></div>
            </div>
        </div>`;
        setTimeout(() => setupItemPicker(0), 10);
    } else if (ct === 'PlaceItem') {
        section.innerHTML = `<div class="item-req-block" id="item-req-block">
            ${itemPickerHtml(0, '要安放的物品')}
            <div class="form-row two-col">
                <div class="form-col"><label>数量</label><input id="f-count" type="number" value="1" /></div>
                <div class="form-col"><label>安放耗时（秒）</label><input id="f-plant-time" type="number" value="5" placeholder="默认 5 秒" /></div>
            </div>
            <div class="form-row two-col">
                <div class="form-col"><label>地图</label>${mapSelect('f-location')}</div>
                <div class="form-col"><label>区域 ID</label><input id="f-zone" placeholder="如 BotZone / ZoneDormitory" /></div>
            </div>
        </div>`;
        setTimeout(() => setupItemPicker(0), 10);
    } else if (ct === 'VisitZone') {
        section.innerHTML = `<div class="form-row two-col">
            <div class="form-col"><label>到达次数</label><input id="f-count" type="number" value="1" /></div>
            <div class="form-col"><label>地图</label>${mapSelect('f-location')}</div>
        </div>
        <div class="form-row">
            <div class="form-col"><label>区域 ID</label><input id="f-zone" placeholder="如 ZoneDormitory / BotZone" /></div>
        </div>`;
    }
}

function mapSelect(id) {
    let opts = MAP_NAMES.map(([v, label]) => `<option value="${esc(v)}">${esc(label)}</option>`).join('');
    return `<select id="${esc(id)}">${opts}</select>`;
}

function itemPickerHtml(idx, label) {
    return `<div class="form-row">
        <div class="form-col"><label>${label}</label>
            <div class="item-picker">
                <div class="ip-input-row">
                    <img id="fi-icon-${idx}" class="ip-icon" src="" alt="" style="display:none" />
                    <input id="fi-tpl-${idx}" placeholder="搜索物品名称或 tpl…" autocomplete="off" />
                </div>
                <div id="fi-results-${idx}" class="ip-results" style="display:none"></div>
                <div id="fi-name-${idx}" class="ip-selected-name"></div>
            </div>
        </div>
    </div>`;
}

// ---- CSV 辅助 ----
function csv(id) { const v = el(id).value.trim(); if (!v) return null; const a = v.split(',').map(s => s.trim()).filter(Boolean); return a.length ? a : null; }
function setCsv(id, arr) { el(id).value = Array.isArray(arr) ? arr.join(', ') : ''; }
function numOrNull(id) { const v = el(id).value.trim(); return v === '' ? null : +v; }
function mapVal(id) { const v = el(id)?.value; return v === '' ? null : v; }

// ---- 任务列表 ----
async function loadTasks() {
    const r = await api('/tasks');
    if (!r.success) return;
    const box = el('task-rows');
    box.innerHTML = '';
    if (!r.tasks || r.tasks.length === 0) { box.innerHTML = '<div class="row-item muted">暂无任务模板</div>'; return; }
    r.tasks.forEach(t => {
        const div = document.createElement('div');
        div.className = 'row-item task-row';
        div.onclick = () => fillForm(t);
        const scopeBadge = `<span class="badge badge-${t.scope}">${scopeLabel(t.scope)}</span>`;
        const ctLabel = t.conditionType || 'Kills';
        const itemInfo = t.itemRequirements?.length ? ` · ${t.itemRequirements[0].name || t.itemRequirements[0].tpl.slice(0,8)+'…'}` : '';
        const zoneInfo = t.zoneId ? ` · 区域 ${t.zoneId}` : '';
        const locInfo = t.location ? ` · ${mapChinese(t.location)}` : '';
        div.innerHTML = `<div><div><strong>${esc(t.title)}</strong> ${scopeBadge}</div>
            <div class="meta">${esc(t.id)} · ${ctLabel}${itemInfo}${zoneInfo}${locInfo} · +${t.xp}XP</div></div>`;
        const acts = document.createElement('div'); acts.className = 'acts';
        const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '×';
        del.onclick = e => { e.stopPropagation(); delTask(t.id); };
        acts.appendChild(del); div.appendChild(acts);
        box.appendChild(div);
    });
}

function scopeLabel(s) { return { daily: '日', weekly: '周', season: '赛季' }[s] || s; }
function mapChinese(id) {
    const m = Object.fromEntries(MAP_NAMES);
    return m[id]?.split(' ')[0] || id;
}

// ---- 表单填充 ----
function fillForm(t) {
    el('editor-title').textContent = '编辑任务: ' + t.title;
    currentConditionType = t.conditionType || 'Kills';
    document.querySelectorAll('.type-card').forEach(c => {
        c.classList.toggle('active', c.dataset.ct === currentConditionType);
    });
    renderTargetSection();
    el('kills-fine').style.display = currentConditionType === 'Kills' ? '' : 'none';

    el('f-id').value = t.id; el('f-title').value = t.title; el('f-desc').value = t.description;
    el('f-scope').value = t.scope; el('f-rotation').value = t.rotation; el('f-xp').value = t.xp;
    el('f-weight').value = t.weight;

    setTimeout(() => {
        if (el('f-target')) el('f-target').value = t.target || 'Any';
        if (el('f-count')) el('f-count').value = t.count;
        if (el('f-location')) el('f-location').value = t.location || '';
        if (el('f-zone')) el('f-zone').value = t.zoneId || '';
        if (el('f-plant-time')) el('f-plant-time').value = t.plantTime ?? '';
        if (el('f-find-in-raid')) el('f-find-in-raid').checked = !!t.findInRaid;

        setCsv('f-weapons', t.weapons); setCsv('f-calibers', t.weaponCalibers); setCsv('f-bodyparts', t.bodyParts);
        setCsv('f-savageroles', t.savageRoles); setCsv('f-enemyequip', t.enemyEquipment);
        setCsv('f-playerequip', t.playerEquipment); setCsv('f-weaponmods', t.weaponMods);
        el('f-distcompare').value = t.distanceCompare || ''; el('f-distval').value = t.distanceValue ?? '';
        el('f-daytimefrom').value = t.daytimeFrom ?? ''; el('f-daytimeto').value = t.daytimeTo ?? '';

        // 物品选择器
        if (t.itemRequirements?.length) {
            const item = t.itemRequirements[0];
            const tpInput = el('fi-tpl-0');
            if (tpInput) {
                tpInput.value = item.tpl;
                pickItem(0, item.tpl, item.name || item.tpl);
            }
        }
    }, 20);

    el('delete-task').style.display = '';
    el('editor-panel').scrollIntoView({ behavior: 'smooth' });
}

// ---- 新建 / 清空 ----
el('new-task-btn').onclick = () => {
    clearForm();
    el('editor-title').textContent = '新建任务';
    el('editor-panel').scrollIntoView({ behavior: 'smooth' });
};

function clearForm() {
    currentConditionType = 'Kills';
    document.querySelectorAll('.type-card').forEach(c => { c.classList.toggle('active', c.dataset.ct === 'Kills'); });
    el('f-id').value = genTaskId();
    el('f-title').value = ''; el('f-desc').value = '';
    el('f-scope').value = 'daily'; el('f-rotation').value = 'random'; el('f-xp').value = 500; el('f-weight').value = 1;
    el('delete-task').style.display = 'none';
    el('kills-fine').style.display = currentConditionType === 'Kills' ? '' : 'none';
    renderTargetSection();
    setTimeout(() => {
        ['f-distcompare', 'f-distval', 'f-daytimefrom', 'f-daytimeto',
            'f-weapons', 'f-calibers', 'f-bodyparts', 'f-savageroles',
            'f-enemyequip', 'f-playerequip', 'f-weaponmods',
            'f-zone', 'f-plant-time'].forEach(id => {
                const e = el(id); if (e) { if (e.type === 'number') e.value = ''; else e.value = ''; }
            });
        if (el('f-target')) el('f-target').value = 'Any';
        if (el('f-count')) el('f-count').value = 3;
        if (el('f-location')) el('f-location').value = '';
        if (el('f-find-in-raid')) el('f-find-in-raid').checked = false;
    }, 20);
}
el('clear-task').onclick = clearForm;

// ---- 保存 ----
el('save-task').onclick = async () => {
    const ct = currentConditionType;
    const task = {
        conditionType: ct,
        id: el('f-id').value.trim(),
        title: el('f-title').value.trim(),
        description: el('f-desc').value.trim(),
        scope: el('f-scope').value,
        rotation: el('f-rotation').value,
        xp: +el('f-xp').value,
        weight: +el('f-weight').value,
    };
    if (!task.id) return toast('请填写或自动生成任务 ID', false);

    if (ct === 'Kills') {
        task.target = el('f-target')?.value || 'Any';
        task.count = +(el('f-count')?.value || 3);
        task.location = mapVal('f-location');
        task.weapons = csv('f-weapons'); task.weaponCalibers = csv('f-calibers');
        task.bodyParts = csv('f-bodyparts'); task.savageRoles = csv('f-savageroles');
        task.distanceCompare = el('f-distcompare')?.value || null;
        task.distanceValue = numOrNull('f-distval');
        task.daytimeFrom = numOrNull('f-daytimefrom'); task.daytimeTo = numOrNull('f-daytimeto');
        task.enemyEquipment = csv('f-enemyequip'); task.playerEquipment = csv('f-playerequip');
        task.weaponMods = csv('f-weaponmods');
    } else if (ct === 'Exploration') {
        task.count = +(el('f-count')?.value || 1);
        task.location = mapVal('f-location');
        task.target = 'Any';
    } else if (ct === 'HandoverItem' || ct === 'FindItem') {
        task.count = +(el('f-count')?.value || 1);
        task.location = mapVal('f-location');
        task.findInRaid = el('f-find-in-raid')?.checked || false;
        task.target = 'Any';
        const tpl = el('fi-tpl-0')?.value?.trim();
        if (tpl && isTpl(tpl)) {
            task.itemRequirements = [{ tpl, count: task.count, name: el('fi-name-0')?.textContent?.replace(/^[a-f0-9]{24}/, '').trim() || null }];
        }
    } else if (ct === 'PlaceItem') {
        task.count = +(el('f-count')?.value || 1);
        task.location = mapVal('f-location');
        task.zoneId = el('f-zone')?.value?.trim() || null;
        task.plantTime = numOrNull('f-plant-time');
        task.target = 'Any';
        const tpl = el('fi-tpl-0')?.value?.trim();
        if (tpl && isTpl(tpl)) {
            task.itemRequirements = [{ tpl, count: task.count, name: el('fi-name-0')?.textContent?.replace(/^[a-f0-9]{24}/, '').trim() || null }];
        }
    } else if (ct === 'VisitZone') {
        task.count = +(el('f-count')?.value || 1);
        task.location = mapVal('f-location');
        task.zoneId = el('f-zone')?.value?.trim() || null;
        task.target = 'Any';
    }

    const r = await api('/tasks', 'POST', task);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) { loadTasks(); el('delete-task').style.display = ''; }
};

// ---- 删除 ----
el('delete-task').onclick = async () => {
    const id = el('f-id').value.trim();
    if (!id) return;
    if (!confirm('删除任务 ' + id + ' ?')) return;
    const r = await api('/tasks', 'DELETE', { id });
    toast(r.success ? '已删除' : (r.message || '失败'), r.success);
    if (r.success) { clearForm(); loadTasks(); }
};

async function delTask(id) {
    if (!confirm('删除任务 ' + id + ' ?')) return;
    const r = await api('/tasks', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success);
    loadTasks();
}

async function refreshActiveTasks() {
    const profileId = el('refresh-profile').value.trim();
    const body = { scope: el('refresh-scope').value };
    if (profileId) body.profileId = profileId;
    else body.all = true;

    const r = await api('/tasks/refresh', 'POST', body);
    toast(r.success ? `已刷新 ${r.refreshed || 0}，跳过 ${r.skipped || 0}` : (r.message || '刷新失败'), r.success);
}

// ---- 启动 ----
el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;
el('refresh-active-tasks').onclick = refreshActiveTasks;

if (ADMIN_TOKEN) enterConsole();
