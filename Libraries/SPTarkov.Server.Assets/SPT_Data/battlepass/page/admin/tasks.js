'use strict';

const ADMIN_API = '/battlepass/api/admin';
const LOTTERY_ADMIN_API = '/battlepass/api/admin/lottery';
const ITEMS_SEARCH = '/battlepass/api/admin/items/search';
const ICON_API = '/battlepass/api/icons/';
// REGISTER_ADMIN_LOGIN / getAdminToken / isCollaborator / ensureAdminSession / submitChange 由 auth.js 提供（勿在此重复声明 const）。
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';
let lotteryPoolsForRewards = [];

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

// 区域 ID 候选（PlaceItem / VisitZone 用）。可下拉选择、也可自由输入自定义区域。
// 扩展：新增区域只需在此数组加一行；各地图的精确区域 id 可在游戏内由客户端补丁「发现模式」Debug 日志获取
//（安放/到访时会打印 zone=… / id=…）。
const ZONE_NAMES = [
    'ZoneDormitory',
    'ZoneGasStation',
    'ZoneOldAZS',
    'ZoneCrossRoad',
    'ZoneFactoryCenter',
    'ZoneRailStrorage',
    'ZoneScavBase',
    'ZoneBoiler',
    'ZoneBlockPost',
    'ZoneCustoms',
    'ZoneBunkerStorage',
    'ZoneSubStorage',
    'ZonePTOR',
    'ZoneSnow',
    'ZoneWood',
];

const MULTI_ITEM_FIELDS = {
    'f-weapons': 'weapon',
    'f-weaponmods': 'weaponMod',
};
const TAG_FIELDS = ['f-calibers', 'f-savageroles'];
const multiItemLabels = {};

function el(id) { return document.getElementById(id); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }
function randHex(n) { const a = new Uint8Array(Math.ceil(n / 2)); crypto.getRandomValues(a); return Array.from(a, b => b.toString(16).padStart(2, '0')).join('').slice(0, n); }

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() };
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
function enterConsole() {
    el('login-view').classList.add('hidden');
    el('tasks-view').classList.remove('hidden');
    if (isCollaborator()) applyCollaboratorUi();
    return Promise.all([loadLotteryPoolsForRewards(), loadTasks(), loadTaskSettings(), loadGenSpec(), loadGenPool()]);
}

// 协管态：顶部提示条 + 隐藏仅管理员可用的运营/无审核动作。
// 允许协管提交审核：任务模板保存/删除(task.upsert/task.delete)、保存生成规格(task.genSpec)。
// 仅管理员：立即生成、刷新任务池、清空生成池、赛季投放设置保存。
function applyCollaboratorUi() {
    showCollaboratorBanner();
    ['gen-now', 'gen-pool-refresh', 'gen-pool-clear', 'refresh-active-tasks', 'save-task-settings'].forEach(id => {
        const b = el(id); if (b) b.style.display = 'none';
    });
}

function showCollaboratorBanner() {
    if (el('collab-banner')) return;
    const bar = document.createElement('div');
    bar.id = 'collab-banner';
    bar.className = 'hint';
    bar.style.cssText = 'margin:10px 16px;padding:10px 14px;border-left:4px solid #e0a030;background:rgba(224,160,48,.12);font-weight:600;';
    bar.textContent = '协管模式：任务模板/生成规格的修改将提交审核后生效，执行生成/刷新等操作仅管理员可用';
    const view = el('tasks-view');
    const topbar = view.querySelector('.topbar');
    if (topbar && topbar.nextSibling) view.insertBefore(bar, topbar.nextSibling);
    else view.insertBefore(bar, view.firstChild);
}

// ---- 任务系统全局设置（写入当前赛季）----
let taskSettingsSeason = null;
function tsSet(id, v, dflt) { const e = el(id); if (e) e.value = (v ?? dflt); }
function tsNum(id, dflt) { const v = el(id)?.value?.trim(); return (v === '' || v == null) ? dflt : +v; }
async function loadTaskSettings() {
    const r = await api('/season');
    if (!r || !r.success) return;
    const s = taskSettingsSeason = r.season || {};
    tsSet('ts-daily-count', s.dailyTaskCount, 3);
    tsSet('ts-weekly-count', s.weeklyTaskCount, 2);
    tsSet('ts-season-count', s.seasonTaskCount, 0);
    tsSet('ts-display-count', s.taskDisplayCount, 4);
    tsSet('ts-daily-period', s.dailyPeriodHours, 24);
    tsSet('ts-weekly-period', s.weeklyPeriodHours, 168);
    tsSet('ts-season-period', s.seasonPeriodHours, 0);
    tsSet('ts-daily-refresh', s.dailyRefreshLimit, 1);
    tsSet('ts-weekly-refresh', s.weeklyRefreshLimit, 1);
    tsSet('ts-season-refresh', s.seasonRefreshLimit, 0);
    el('ts-daily-budget-on').checked = !!s.dailyBudgetEnabled;
    el('ts-weekly-budget-on').checked = !!s.weeklyBudgetEnabled;
    el('ts-season-budget-on').checked = !!s.seasonBudgetEnabled;
    tsSet('ts-daily-budget', s.dailyDifficultyBudget, 4);
    tsSet('ts-weekly-budget', s.weeklyDifficultyBudget, 6);
    tsSet('ts-season-budget', s.seasonDifficultyBudget, 12);
}
async function saveTaskSettings() {
    if (isCollaborator()) return toast('该操作仅管理员可用', false);
    if (!taskSettingsSeason) { const r = await api('/season'); taskSettingsSeason = (r && r.season) || {}; }
    const payload = {
        ...taskSettingsSeason,
        dailyTaskCount: tsNum('ts-daily-count', 3),
        weeklyTaskCount: tsNum('ts-weekly-count', 2),
        seasonTaskCount: tsNum('ts-season-count', 0),
        taskDisplayCount: Math.max(1, tsNum('ts-display-count', 4)),
        dailyPeriodHours: tsNum('ts-daily-period', 24),
        weeklyPeriodHours: tsNum('ts-weekly-period', 168),
        seasonPeriodHours: tsNum('ts-season-period', 0),
        dailyRefreshLimit: tsNum('ts-daily-refresh', 1),
        weeklyRefreshLimit: tsNum('ts-weekly-refresh', 1),
        seasonRefreshLimit: tsNum('ts-season-refresh', 0),
        dailyBudgetEnabled: el('ts-daily-budget-on').checked,
        weeklyBudgetEnabled: el('ts-weekly-budget-on').checked,
        seasonBudgetEnabled: el('ts-season-budget-on').checked,
        dailyDifficultyBudget: tsNum('ts-daily-budget', 4),
        weeklyDifficultyBudget: tsNum('ts-weekly-budget', 6),
        seasonDifficultyBudget: tsNum('ts-season-budget', 12),
    };
    const r = await api('/season', 'POST', payload);
    if (r && r.success) {
        taskSettingsSeason = payload;
        el('ts-msg').textContent = '已保存 ' + new Date().toLocaleTimeString();
        toast('任务设置已保存', true);
    } else {
        toast((r && r.message) || '保存失败', false);
    }
}

// ---- 任务自动生成规格 ----
const GEN_SCOPES = [['daily', '每日'], ['weekly', '每周'], ['season', '赛季']];
const GEN_TYPES = [['Kills', '击杀'], ['Exploration', '撤离']];
const GEN_TARGETS = [['Any', '任意'], ['Savage', 'Scav'], ['AnyPmc', 'PMC'], ['Bear', 'BEAR'], ['Usec', 'USEC'], ['Boss', '头目']];

function genBlockHtml(scope, label, s) {
    s = s || {};
    const ckTypes = GEN_TYPES.map(([v, t]) =>
        `<label class="with-cb"><input type="checkbox" data-gt="type" value="${v}" ${(s.conditionTypes || ['Kills', 'Exploration']).includes(v) ? 'checked' : ''} /> ${t}</label>`).join(' ');
    const ckTargets = GEN_TARGETS.map(([v, t]) =>
        `<label class="with-cb"><input type="checkbox" data-gt="target" value="${v}" ${(s.killTargets || ['Any', 'Savage', 'AnyPmc']).includes(v) ? 'checked' : ''} /> ${t}</label>`).join(' ');
    return `<div class="gen-scope" data-scope="${scope}">
        <div class="gen-scope-head">
            <label class="with-cb"><input type="checkbox" data-gf="enabled" ${s.enabled ? 'checked' : ''} /> <strong>${label}</strong> 启用生成</label>
        </div>
        <div class="ts-grid">
            <label>生成条数<input type="number" min="1" data-gf="count" value="${s.count ?? 3}" /></label>
            <label>自动周期(时)<span class="ts-sub">0=仅手动</span><input type="number" min="0" step="0.5" data-gf="autoPeriodHours" value="${s.autoPeriodHours ?? 0}" /></label>
            <label>目标次数下限<input type="number" min="1" data-gf="minCount" value="${s.minCount ?? 2}" /></label>
            <label>目标次数上限<input type="number" min="1" data-gf="maxCount" value="${s.maxCount ?? 8}" /></label>
            <label>经验·易<input type="number" min="0" data-gf="xpEasy" value="${s.xpEasy ?? 300}" /></label>
            <label>经验·中<input type="number" min="0" data-gf="xpMed" value="${s.xpMed ?? 600}" /></label>
            <label>经验·难<input type="number" min="0" data-gf="xpHard" value="${s.xpHard ?? 1000}" /></label>
        </div>
        <div class="gen-checks"><span class="ts-sub">类型：</span>${ckTypes}</div>
        <div class="gen-checks"><span class="ts-sub">击杀目标：</span>${ckTargets}</div>
        <label class="gen-locs">地图池（逗号分隔地图 id，空=不限）<input data-gf="locations" value="${esc((s.locations || []).join(', '))}" placeholder="bigmap, Woods, Sandbox" /></label>
    </div>`;
}

async function loadGenSpec() {
    const r = await api('/tasks/gen-spec');
    const spec = (r && r.success && r.spec) || {};
    el('gen-scopes').innerHTML = GEN_SCOPES.map(([scope, label]) => genBlockHtml(scope, label, spec[scope])).join('');
}

function collectGenSpec() {
    const spec = {};
    el('gen-scopes').querySelectorAll('.gen-scope').forEach(block => {
        const scope = block.dataset.scope;
        const gf = f => block.querySelector(`[data-gf="${f}"]`);
        const checks = t => Array.from(block.querySelectorAll(`[data-gt="${t}"]:checked`)).map(c => c.value);
        spec[scope] = {
            enabled: gf('enabled').checked,
            count: +gf('count').value || 3,
            autoPeriodHours: +gf('autoPeriodHours').value || 0,
            conditionTypes: checks('type'),
            killTargets: checks('target'),
            locations: gf('locations').value.split(',').map(s => s.trim()).filter(Boolean),
            minCount: +gf('minCount').value || 2,
            maxCount: +gf('maxCount').value || 8,
            xpEasy: +gf('xpEasy').value || 0,
            xpMed: +gf('xpMed').value || 0,
            xpHard: +gf('xpHard').value || 0,
        };
    });
    return spec;
}

async function saveGenSpec() {
    const spec = collectGenSpec();
    if (isCollaborator()) {
        const r = await submitChange('tasks', 'task.genSpec', spec);
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/tasks/gen-spec', 'POST', spec);
    el('gen-msg').textContent = r.success ? '已保存 ' + new Date().toLocaleTimeString() : (r.message || '保存失败');
    toast(r.success ? '生成规格已保存' : (r.message || '失败'), r.success);
}

async function generateNow() {
    if (isCollaborator()) return toast('该操作仅管理员可用', false);
    // 先保存当前规格，再生成，避免用未保存的设置
    await api('/tasks/gen-spec', 'POST', collectGenSpec());
    const r = await api('/tasks/generate', 'POST', {});
    toast(r.success ? `已生成 ${r.generated || 0} 条任务` : (r.message || '生成失败'), r.success);
    if (r.success) { loadTasks(); loadGenPool(); }
}

// ---- 生成池（独立于自定义任务池，可查看与一键清空）----
async function loadGenPool() {
    const r = await api('/tasks/generated');
    const box = el('gen-pool-list');
    if (!r || !r.success) { el('gen-pool-msg').textContent = (r && r.message) || '加载失败'; return; }
    const tasks = r.tasks || [];
    el('gen-pool-msg').textContent = `共 ${tasks.length} 条`;
    if (tasks.length === 0) { box.innerHTML = '<div class="muted" style="font-size:11px">生成池为空</div>'; return; }
    const byScope = {};
    tasks.forEach(t => { (byScope[t.scope] || (byScope[t.scope] = [])).push(t); });
    box.innerHTML = Object.entries(byScope).map(([scope, list]) => {
        const label = (GEN_SCOPES.find(s => s[0] === scope) || [scope, scope])[1];
        const rows = list.map(t => `<div class="gp-row" style="font-size:11px;padding:2px 0;border-bottom:1px solid var(--line)">
            <code>${esc(t.id)}</code> · ${esc(t.title || t.description || t.conditionType || '')}</div>`).join('');
        return `<div style="margin-bottom:8px"><div class="ts-sub">${label}（${list.length}）</div>${rows}</div>`;
    }).join('');
}

async function clearGenPool() {
    if (isCollaborator()) return toast('该操作仅管理员可用', false);
    if (!confirm('确定清空生成池？将删除全部 gen_ 自动/手动生成任务（不影响左侧自定义任务）。')) return;
    const r = await api('/tasks/generated', 'DELETE');
    toast(r && r.success ? `已清空 ${r.cleared || 0} 条生成任务` : ((r && r.message) || '清空失败'), r && r.success);
    if (r && r.success) { loadGenPool(); loadTasks(); }
}

// ---- 类型卡片 ----
let currentConditionType = 'Kills';
const ONE_LIFE_TYPES = new Set(['Kills', 'FindItem', 'PlaceItem', 'VisitZone']);

function updateOneLifeVisibility(resetUnsupported = false) {
    const supported = ONE_LIFE_TYPES.has(currentConditionType);
    el('one-life-row').style.display = supported ? '' : 'none';
    if (!supported && resetUnsupported) el('f-one-life').checked = false;
}
document.querySelectorAll('.type-card').forEach(card => {
    card.onclick = () => {
        document.querySelectorAll('.type-card').forEach(c => c.classList.remove('active'));
        card.classList.add('active');
        currentConditionType = card.dataset.ct;
        renderTargetSection();
        updateOneLifeVisibility(true);
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

function csvValues(id) {
    const input = el(id);
    if (!input) return [];
    return input.value.split(',').map(s => s.trim()).filter(Boolean);
}

function writeCsvValues(id, values) {
    const input = el(id);
    if (!input) return;
    const unique = Array.from(new Set(values.filter(Boolean)));
    input.value = unique.join(', ');
    renderMultiChips(id);
}

function renderMultiChips(id) {
    const box = el(`${id}-chips`);
    if (!box) return;
    const values = csvValues(id);
    box.innerHTML = values.map(v => {
        const label = multiItemLabels[id]?.[v];
        const text = label ? `${label} · ${v.slice(0, 8)}…` : v;
        return `<span class="multi-chip" title="${esc(v)}"><span>${esc(text)}</span><button type="button" data-tpl="${esc(v)}">×</button></span>`;
    }).join('');
    box.querySelectorAll('button').forEach(btn => {
        btn.onclick = () => writeCsvValues(id, values.filter(v => v !== btn.dataset.tpl));
    });
}

function setupMultiItemPicker(id, category) {
    const search = el(`${id}-search`);
    const results = el(`${id}-results`);
    const valuesInput = el(id);
    if (!search || !results || !valuesInput) return;

    BpPicker.attachItem(search, results, ds => {
        multiItemLabels[id] ??= {};
        multiItemLabels[id][ds.tpl] = ds.name || ds.tpl;
        writeCsvValues(id, [...csvValues(id), ds.tpl]);
        search.value = '';
    }, { limit: 8, category });

    valuesInput.addEventListener('input', () => renderMultiChips(id));
    renderMultiChips(id);
}

function setupTagPicker(id) {
    const input = el(`${id}-search`);
    const add = el(`${id}-add`);
    if (!input || !add) return;
    const commit = () => {
        const value = input.value.trim();
        if (!value) return;
        writeCsvValues(id, [...csvValues(id), value]);
        input.value = '';
    };
    add.onclick = commit;
    input.addEventListener('keydown', event => {
        if (event.key === 'Enter') { event.preventDefault(); commit(); }
    });
    renderMultiChips(id);
}

function choiceValues(id) {
    return Array.from(el(id)?.querySelectorAll('input[type="checkbox"]:checked') || []).map(input => input.value);
}

function setChoiceValues(id, values) {
    const selected = new Set(values || []);
    el(id)?.querySelectorAll('input[type="checkbox"]').forEach(input => { input.checked = selected.has(input.value); });
}

function setupHourSelects() {
    const options = ['<option value="">不限</option>'];
    for (let hour = 0; hour < 24; hour++) options.push(`<option value="${hour}">${String(hour).padStart(2, '0')}:00</option>`);
    ['f-daytimefrom', 'f-daytimeto'].forEach(id => { if (el(id)) el(id).innerHTML = options.join(''); });
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
                <div class="form-col"><label>区域 ID</label>${zoneSelect('f-zone')}</div>
            </div>
        </div>`;
        setTimeout(() => setupItemPicker(0), 10);
    } else if (ct === 'VisitZone') {
        section.innerHTML = `<div class="form-row two-col">
            <div class="form-col"><label>到达次数</label><input id="f-count" type="number" value="1" /></div>
            <div class="form-col"><label>地图</label>${mapSelect('f-location')}</div>
        </div>
        <div class="form-row">
            <div class="form-col"><label>区域 ID</label>${zoneSelect('f-zone')}</div>
        </div>`;
    }
}

function mapSelect(id) {
    let opts = MAP_NAMES.map(([v, label]) => `<option value="${esc(v)}">${esc(label)}</option>`).join('');
    return `<select id="${esc(id)}">${opts}</select>`;
}

// 区域 ID：可下拉选择常见区域、也可手填自定义区域（datalist）。与纯文本框读写兼容（el(id).value）。
function zoneSelect(id) {
    const listId = esc(id) + '-list';
    const opts = ZONE_NAMES.map(z => `<option value="${esc(z)}"></option>`).join('');
    return `<input id="${esc(id)}" list="${listId}" placeholder="选择或输入区域 ID（见游戏内发现日志）" autocomplete="off" />
        <datalist id="${listId}">${opts}</datalist>`;
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
function setCsv(id, arr) { el(id).value = Array.isArray(arr) ? arr.join(', ') : ''; renderMultiChips(id); }
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
        const oneLifeInfo = t.oneLife ? ' · 一命完成' : '';
        div.innerHTML = `<div><div><strong>${esc(t.title)}</strong> ${scopeBadge}</div>
            <div class="meta">${esc(t.id)} · ${ctLabel}${itemInfo}${zoneInfo}${locInfo}${oneLifeInfo} · 难度${t.difficulty || 1} · +${t.xp}XP</div></div>`;
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
    updateOneLifeVisibility(false);
    el('f-one-life').checked = !!t.oneLife;
    el('kills-fine').style.display = currentConditionType === 'Kills' ? '' : 'none';

    el('f-id').value = t.id; el('f-title').value = t.title; el('f-desc').value = t.description;
    el('f-scope').value = t.scope; el('f-rotation').value = t.rotation; el('f-xp').value = t.xp;
    el('f-weight').value = t.weight;
    el('f-difficulty').value = t.difficulty || 1;
    el('f-rewardmode').value = t.rewardMode || 'xp';
    setRewards(t.rewards || []);
    updateRewardVisibility();

    setTimeout(() => {
        if (el('f-target')) el('f-target').value = t.target || 'Any';
        if (el('f-count')) el('f-count').value = t.count;
        if (el('f-location')) el('f-location').value = t.location || '';
        if (el('f-zone')) el('f-zone').value = t.zoneId || '';
        if (el('f-plant-time')) el('f-plant-time').value = t.plantTime ?? '';
        if (el('f-find-in-raid')) el('f-find-in-raid').checked = !!t.findInRaid;

        setCsv('f-weapons', t.weapons); setCsv('f-calibers', t.weaponCalibers); setCsv('f-weaponmods', t.weaponMods);
        setChoiceValues('f-bodyparts', t.bodyParts); setCsv('f-savageroles', t.savageRoles);
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
    el('f-difficulty').value = 1;
    el('f-rewardmode').value = 'xp'; setRewards([]); updateRewardVisibility();
    el('f-one-life').checked = false; updateOneLifeVisibility(true);
    el('delete-task').style.display = 'none';
    el('kills-fine').style.display = currentConditionType === 'Kills' ? '' : 'none';
    renderTargetSection();
    setTimeout(() => {
        ['f-distcompare', 'f-distval', 'f-daytimefrom', 'f-daytimeto',
            'f-weapons', 'f-weaponmods', 'f-calibers', 'f-savageroles',
            'f-zone', 'f-plant-time'].forEach(id => {
                const e = el(id); if (e) { if (e.type === 'number') e.value = ''; else e.value = ''; }
            });
        Object.keys(MULTI_ITEM_FIELDS).forEach(id => renderMultiChips(id));
        TAG_FIELDS.forEach(id => renderMultiChips(id));
        setChoiceValues('f-bodyparts', []);
        if (el('f-target')) el('f-target').value = 'Any';
        if (el('f-count')) el('f-count').value = 3;
        if (el('f-location')) el('f-location').value = '';
        if (el('f-find-in-raid')) el('f-find-in-raid').checked = false;
    }, 20);
}
el('clear-task').onclick = clearForm;

// ---- 任务奖励编辑器（物品 / 权益 / 抽奖资源，与等级奖励轨一致）----
function lotteryPoolOptions(selected) {
    return RewardEditor.poolOptions(lotteryPoolsForRewards, selected);
}

function refreshRewardPoolSelect(select) {
    if (!select) return;
    const current = select.value || '';
    select.innerHTML = lotteryPoolOptions(current);
    if (current) select.value = current;
}

function refreshRewardPoolSelects() {
    document.querySelectorAll('select.rw-poolid').forEach(refreshRewardPoolSelect);
}

async function loadLotteryPoolsForRewards() {
    try {
        const res = await fetch(LOTTERY_ADMIN_API + '/pools', { headers: { 'X-Admin-Token': getAdminToken() } });
        const r = await res.json();
        if (!r.success) return;
        lotteryPoolsForRewards = r.pools || [];
        refreshRewardPoolSelects();
    } catch (e) {
        // 抽奖模块未加载不影响任务页其他功能。
    }
}

// 奖励卡渲染/收集/选择器已迁到共享组件 reward-editor.js（RewardEditor）；此处只做薄封装，奖池用 lotteryPoolsForRewards。
function addRewardRow(r) {
    RewardEditor.addCard(el('reward-items'), r, lotteryPoolsForRewards);
}

function setRewards(list) {
    RewardEditor.fill(el('reward-items'), list || [], lotteryPoolsForRewards);
}

function collectRewards() {
    return RewardEditor.collect(el('reward-items'), { dropInvalid: true }).rewards;
}

function updateRewardVisibility() {
    const itemsMode = el('f-rewardmode').value !== 'xp';
    el('reward-items').style.display = itemsMode ? '' : 'none';
    el('add-reward-btn').style.display = itemsMode ? '' : 'none';
}

el('add-reward-btn').onclick = () => addRewardRow();
el('f-rewardmode').addEventListener('change', updateRewardVisibility);

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
        difficulty: +el('f-difficulty').value || 1,
        oneLife: ONE_LIFE_TYPES.has(ct) && !!el('f-one-life').checked,
    };
    if (!task.id) return toast('请填写或自动生成任务 ID', false);

    if (ct === 'Kills') {
        task.target = el('f-target')?.value || 'Any';
        task.count = +(el('f-count')?.value || 3);
        task.location = mapVal('f-location');
        task.weapons = csv('f-weapons'); task.weaponCalibers = csv('f-calibers'); task.weaponMods = csv('f-weaponmods');
        task.bodyParts = choiceValues('f-bodyparts'); task.savageRoles = csv('f-savageroles');
        task.distanceCompare = el('f-distcompare')?.value || null;
        task.distanceValue = numOrNull('f-distval');
        task.daytimeFrom = numOrNull('f-daytimefrom'); task.daytimeTo = numOrNull('f-daytimeto');
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

    task.rewardMode = el('f-rewardmode').value;
    const rewards = collectRewards();
    if (rewards.length) task.rewards = rewards;

    if (isCollaborator()) {
        const r = await submitChange('tasks', 'task.upsert', task);
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
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
    if (isCollaborator()) {
        const r = await submitChange('tasks', 'task.delete', { id });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/tasks', 'DELETE', { id });
    toast(r.success ? '已删除' : (r.message || '失败'), r.success);
    if (r.success) { clearForm(); loadTasks(); }
};

async function delTask(id) {
    if (!confirm('删除任务 ' + id + ' ?')) return;
    if (isCollaborator()) {
        const r = await submitChange('tasks', 'task.delete', { id });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/tasks', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success);
    loadTasks();
}

function applyPendingTaskEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    const payload = change.proposedPayload || {};
    if (change.commandType === 'task.upsert') fillForm(payload);
    else if (change.commandType === 'task.genSpec') {
        el('gen-scopes').innerHTML = GEN_SCOPES.map(([scope, label]) => genBlockHtml(scope, label, payload[scope])).join('');
        el('gen-scopes').scrollIntoView({ behavior: 'smooth', block: 'start' });
    } else if (change.commandType === 'task.delete') {
        toast('请选择新的任务并点击删除，以更新原删除审核单', true);
    }
}

async function refreshActiveTasks() {
    if (isCollaborator()) return toast('该操作仅管理员可用', false);
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
el('save-task-settings').onclick = saveTaskSettings;
el('save-gen-spec').onclick = saveGenSpec;
el('gen-now').onclick = generateNow;
el('gen-pool-refresh').onclick = loadGenPool;
el('gen-pool-clear').onclick = clearGenPool;
Object.entries(MULTI_ITEM_FIELDS).forEach(([id, category]) => setupMultiItemPicker(id, category));
TAG_FIELDS.forEach(setupTagPicker);
setupHourSelects();

// ---- 入口登录 gate：协管 #bpsso= 免密落地换会话；admin 旧式 #sso= 由文件头兼容处理 ----
bootstrapAdminPage({ moduleCap: 'tasks.read', onReady: async edit => { ADMIN_TOKEN = getAdminToken(); await enterConsole(); applyPendingTaskEdit(edit); } });
