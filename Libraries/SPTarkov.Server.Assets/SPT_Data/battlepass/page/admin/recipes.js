'use strict';

// 自定义藏身处配方管理页：登录/Token 与通行证后台一致（X-Admin-Token / Portal SSO）。
const ADMIN_API = '/battlepass/api/admin';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
const ICON_API = '/battlepass/api/icons/';
let ADMIN_TOKEN = sessionStorage.getItem('bp_admin_token') || '';

(function () {
    const m = location.hash.match(/sso=([a-zA-Z0-9]+)/);
    if (m) { ADMIN_TOKEN = m[1]; sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN); history.replaceState(null, '', location.pathname); }
})();

function el(id) { return document.getElementById(id); }
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': ADMIN_TOKEN };
    const res = await fetch(ADMIN_API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// ---- 登录 ----
async function adminLogin() {
    const password = el('admin-pass').value;
    const res = await fetch(REGISTER_ADMIN_LOGIN, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password })
    });
    const r = await res.json();
    if (!r.success) { el('admin-login-msg').textContent = r.message || '登录失败'; return; }
    ADMIN_TOKEN = r.token;
    sessionStorage.setItem('bp_admin_token', ADMIN_TOKEN);
    enterConsole();
}
function logout() { ADMIN_TOKEN = ''; sessionStorage.removeItem('bp_admin_token'); el('recipes-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
function enterConsole() {
    el('login-view').classList.add('hidden');
    el('recipes-view').classList.remove('hidden');
    loadRecipes();
}

// ---- 建筑（HideoutAreas 枚举值 → 中文名；仅列常用制造区域在前，其余折叠在后）----
const AREAS = [
    [10, '工作台 Workbench'], [7, '医疗站 MedStation'], [8, '营养单元 Kitchen'], [2, '厕所 Lavatory'],
    [11, '情报中心 IntelCenter'], [19, '酿酒机 BoozeGenerator'], [6, '集水器 WaterCollector'],
    [4, '发电机 Generator'], [5, '供暖 Heating'], [9, '休息区 RestSpace'], [12, '靶场 ShootingRange'],
    [13, '书库 Library'], [14, 'Scav箱 ScavCase'], [15, '照明 Illumination'], [16, '荣誉墙 PlaceOfFame'],
    [17, '空气滤净 AirFiltering'], [18, '太阳能 SolarPower'], [20, '比特币农场 BitcoinFarm'], [23, '健身房 Gym'],
];
function fillAreaSelect() {
    const sel = el('r-area'); sel.innerHTML = '';
    AREAS.forEach(([v, name]) => {
        const o = document.createElement('option'); o.value = v; o.textContent = name; sel.appendChild(o);
    });
}

// ---- 原料行 ----
function addIngredientRow(ing) {
    ing = ing || { tpl: '', count: 1, isTool: false };
    const row = document.createElement('div');
    row.className = 'tpl-row ing-row';
    row.style.marginBottom = '6px';
    row.innerHTML = `
        <img class="icon-preview ing-icon" alt="" style="${ing.tpl ? '' : 'display:none'}" src="${ing.tpl ? ICON_API + esc(ing.tpl) : ''}" />
        <input class="ing-tpl" value="${esc(ing.tpl)}" placeholder="搜索物品名称或 tpl…" autocomplete="off" style="flex:1" />
        <input class="ing-count" type="number" value="${ing.count || 1}" min="1" style="width:64px" title="数量" ${ing.isTool ? 'disabled' : ''} />
        <label class="with-cb" style="white-space:nowrap"><input class="ing-tool" type="checkbox" ${ing.isTool ? 'checked' : ''} /> 工具</label>
        <button class="mini del" title="删除">×</button>
        <div class="ing-results rw-tpl-results" style="display:none"></div>`;
    el('r-ingredients').appendChild(row);

    const input = row.querySelector('.ing-tpl');
    const results = row.querySelector('.ing-results');
    const icon = row.querySelector('.ing-icon');
    BpPicker.attachItem(input, results, ds => {
        input.value = ds.tpl;
        icon.src = ICON_API + ds.tpl; icon.style.display = '';
        icon.onerror = () => { icon.style.display = 'none'; };
    }, { limit: 6 });
    row.querySelector('.ing-tool').addEventListener('change', e => {
        // 工具不消耗：数量固定 1，禁用数量输入
        row.querySelector('.ing-count').disabled = e.target.checked;
        if (e.target.checked) row.querySelector('.ing-count').value = 1;
    });
    row.querySelector('.mini.del').onclick = () => row.remove();
}

// ---- 表单 ----
let editingId = '';

function clearForm() {
    editingId = '';
    el('recipe-form-title').textContent = '新建 / 编辑配方';
    el('r-area').value = AREAS[0][0];
    el('r-arealevel').value = 1;
    el('r-product').value = ''; el('r-product-icon').style.display = 'none';
    el('r-count').value = 1;
    el('r-hours').value = 1; el('r-minutes').value = 0;
    el('r-ingredients').innerHTML = '';
    addIngredientRow();
    el('r-locked').checked = true;
    el('r-note').value = '';
}

function loadIntoForm(r) {
    editingId = r.id;
    el('recipe-form-title').textContent = '编辑配方 ' + r.id.slice(0, 8) + '…';
    el('r-area').value = r.areaType;
    el('r-arealevel').value = r.areaLevel || 1;
    el('r-product').value = r.endProduct;
    el('r-product-icon').src = ICON_API + r.endProduct; el('r-product-icon').style.display = '';
    el('r-count').value = r.count || 1;
    el('r-hours').value = Math.floor((r.productionTime || 0) / 3600);
    el('r-minutes').value = Math.floor(((r.productionTime || 0) % 3600) / 60);
    el('r-ingredients').innerHTML = '';
    (r.ingredients || []).forEach(addIngredientRow);
    if (!(r.ingredients || []).length) addIngredientRow();
    el('r-locked').checked = !!r.locked;
    el('r-note').value = r.note || '';
    window.scrollTo({ top: 0, behavior: 'smooth' });
}

async function saveRecipe() {
    const isTplStr = s => /^[a-fA-F0-9]{24}$/.test((s || '').trim());
    const product = el('r-product').value.trim();
    if (!isTplStr(product)) return toast('请先选择产物（需 24 位 tpl，可用搜索选择）', false);

    const ingredients = [];
    let bad = 0;
    el('r-ingredients').querySelectorAll('.ing-row').forEach(row => {
        const tpl = row.querySelector('.ing-tpl').value.trim();
        if (!tpl) return;
        if (!isTplStr(tpl)) { bad++; return; }
        const isTool = row.querySelector('.ing-tool').checked;
        ingredients.push({ tpl, count: isTool ? 1 : Math.max(1, +(row.querySelector('.ing-count').value || 1)), isTool });
    });
    if (bad > 0) return toast('存在无效原料 tpl，请用搜索选择物品', false);
    if (!ingredients.length) return toast('至少需要一种原料', false);

    const seconds = Math.max(60, (+el('r-hours').value || 0) * 3600 + (+el('r-minutes').value || 0) * 60);
    const body = {
        id: editingId || '',
        areaType: +el('r-area').value,
        areaLevel: Math.max(1, +el('r-arealevel').value || 1),
        productionTime: seconds,
        endProduct: product,
        count: Math.max(1, +el('r-count').value || 1),
        ingredients,
        locked: el('r-locked').checked,
        note: el('r-note').value.trim() || null,
    };
    const r = await api('/custom-recipes', 'POST', body);
    if (r.success) {
        toast('配方已保存并注入（id: ' + r.id.slice(0, 8) + '…）', true);
        clearForm();
        loadRecipes();
    } else {
        toast(r.message || '保存失败', false);
    }
}

// ---- 列表 ----
const AREA_NAME = Object.fromEntries(AREAS);
async function loadRecipes() {
    const r = await api('/custom-recipes');
    if (!r.success) return;
    const box = el('recipe-rows'); box.innerHTML = '';
    const recipes = r.recipes || [];
    if (!recipes.length) { box.innerHTML = '<div class="muted" style="padding:12px">暂无自定义配方</div>'; return; }
    recipes.forEach(rc => {
        const div = document.createElement('div');
        div.className = 'row-item';
        const h = Math.floor(rc.productionTime / 3600), m = Math.floor((rc.productionTime % 3600) / 60);
        const ings = (rc.ingredients || []).map(i => (i.isTool ? '🔧' : '') + (i.isTool ? '' : i.count + '×') + i.tpl.slice(0, 6) + '…').join(' + ') || '无';
        const left = document.createElement('div');
        left.innerHTML = `<div class="offer-title"><img class="rw-icon" src="${ICON_API}${esc(rc.endProduct)}" alt="" onerror="this.remove()" />${esc(rc.note || rc.endProduct.slice(0, 8) + '…')} ×${rc.count}</div>
            <div class="meta">${esc(AREA_NAME[rc.areaType] || ('区域 ' + rc.areaType))} Lv${rc.areaLevel} · ${h}h${m}m · 原料 ${esc(ings)} · ${rc.locked ? '🔒 通行证解锁' : '✅ 全员可用'} · id ${esc(rc.id.slice(0, 8))}…</div>`;
        const right = document.createElement('div');
        const editBtn = document.createElement('button'); editBtn.className = 'btn ghost small'; editBtn.textContent = '编辑';
        editBtn.onclick = () => loadIntoForm(rc);
        const copyBtn = document.createElement('button'); copyBtn.className = 'btn ghost small'; copyBtn.textContent = '复制 id';
        copyBtn.onclick = () => { navigator.clipboard.writeText(rc.id); toast('已复制 ' + rc.id, true); };
        const delBtn = document.createElement('button'); delBtn.className = 'btn ghost small'; delBtn.textContent = '删除';
        delBtn.onclick = async () => {
            if (!confirm('删除该配方？（已领取解锁的玩家会失去此制造项）')) return;
            const res = await api('/custom-recipes/' + encodeURIComponent(rc.id), 'DELETE');
            if (res.success) { toast('已删除', true); loadRecipes(); } else toast('删除失败', false);
        };
        right.append(editBtn, copyBtn, delBtn);
        div.append(left, right);
        box.appendChild(div);
    });
}

// ---- 绑定 ----
el('admin-login-btn').onclick = adminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') adminLogin(); });
el('admin-logout').onclick = logout;
el('r-add-ing').onclick = () => addIngredientRow();
el('save-recipe').onclick = saveRecipe;
el('clear-recipe').onclick = clearForm;
el('refresh-recipes').onclick = loadRecipes;

// 产物选择器
BpPicker.attachItem(el('r-product'), el('r-product-results'), ds => {
    el('r-product').value = ds.tpl;
    el('r-product-icon').src = ICON_API + ds.tpl; el('r-product-icon').style.display = '';
    el('r-product-icon').onerror = () => { el('r-product-icon').style.display = 'none'; };
}, { limit: 8 });

fillAreaSelect();
clearForm();
if (ADMIN_TOKEN) enterConsole();
