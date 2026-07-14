'use strict';

// 通行证称号管理页：复用通行证后台 admin token / 登录流程 / Portal SSO（同 trader.js）。
const ADMIN_API = '/battlepass/api/admin';
const TITLE_IMG = '/battlepass/api/title-image/';
let ADMIN_TOKEN = getAdminToken();

function el(id) { return document.getElementById(id); }
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

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
function logout() { clearAdminToken(); clearActorType(); clearActorCapabilities(); ADMIN_TOKEN = ''; el('titles-view').classList.add('hidden'); el('login-view').classList.remove('hidden'); }
async function enterConsole() {
    el('login-view').classList.add('hidden');
    el('titles-view').classList.remove('hidden');
    if (isCollaborator()) showCollaboratorBanner();
    await Promise.all([loadTitles(), loadPlayers(), loadHolders()]);
}

function showCollaboratorBanner() {
    if (el('collab-banner')) return;
    const bar = document.createElement('div');
    bar.id = 'collab-banner';
    bar.className = 'hint';
    bar.style.cssText = 'margin:10px 16px;padding:10px 14px;border-left:4px solid #e0a030;background:rgba(224,160,48,.12);font-weight:600;';
    bar.textContent = '协管模式：称号目录、图片上传、授予与撤销会提交审核，等待管理员批准后生效。';
    const view = el('titles-view');
    const topbar = view.querySelector('.topbar');
    if (topbar?.nextSibling) view.insertBefore(bar, topbar.nextSibling); else view.prepend(bar);
}

// ---- 标签 ----
document.querySelectorAll('.tab[data-tab]').forEach(tab => {
    tab.onclick = () => {
        document.querySelectorAll('.tab[data-tab]').forEach(t => t.classList.remove('active'));
        tab.classList.add('active');
        document.querySelectorAll('.tab-pane').forEach(p => p.classList.add('hidden'));
        el('pane-' + tab.dataset.tab).classList.remove('hidden');
    };
});

// ---- 预览渲染（与玩家页一致） ----
function previewInto(box, t) {
    box.innerHTML = '';
    if (t.type === 'image') {
        if (!t.id) { box.textContent = '（保存后上传图片可预览）'; return; }
        const img = document.createElement('img'); img.className = 'title-img';
        img.src = TITLE_IMG + t.id + '?t=' + Date.now(); img.width = 128; img.height = 32;
        img.onerror = () => { box.textContent = '（尚未上传图片）'; };
        box.appendChild(img);
        return;
    }
    const span = document.createElement('span'); span.className = 'title-text';
    span.textContent = t.text || t.name || '称号预览';
    if (t.colorEnd && t.color) {
        span.style.backgroundImage = `linear-gradient(90deg, ${t.color}, ${t.colorEnd})`;
        span.style.webkitBackgroundClip = 'text'; span.style.backgroundClip = 'text'; span.style.color = 'transparent';
    } else if (t.color) { span.style.color = t.color; }
    box.appendChild(span);
}

function currentFormTitle() {
    return {
        id: el('t-id').value.trim(),
        name: el('t-name').value.trim(),
        type: el('t-type').value,
        text: el('t-text').value,
        color: el('t-color-hex').value.trim() || el('t-color').value,
        colorEnd: el('t-gradient').checked ? (el('t-colorend-hex').value.trim() || el('t-colorend').value) : null,
        description: el('t-desc').value.trim() || null,
        width: 128, height: 32,
    };
}
function refreshFormPreview() { previewInto(el('t-preview'), currentFormTitle()); }

// type / gradient 字段联动
function syncTypeFields() {
    const isImage = el('t-type').value === 'image';
    el('image-fields').classList.toggle('hidden', !isImage);
    el('text-fields').classList.toggle('hidden', isImage);
    el('upload-title-image').classList.toggle('hidden', !isImage);
    refreshFormPreview();
}
el('t-type').onchange = syncTypeFields;
el('t-gradient').onchange = () => { el('colorend-row').classList.toggle('hidden', !el('t-gradient').checked); refreshFormPreview(); };
// color 选择器 ↔ hex 输入框双向同步
el('t-color').oninput = () => { el('t-color-hex').value = el('t-color').value; refreshFormPreview(); };
el('t-color-hex').oninput = () => { if (/^#[0-9a-fA-F]{6}$/.test(el('t-color-hex').value)) el('t-color').value = el('t-color-hex').value; refreshFormPreview(); };
el('t-colorend').oninput = () => { el('t-colorend-hex').value = el('t-colorend').value; refreshFormPreview(); };
el('t-colorend-hex').oninput = () => { if (/^#[0-9a-fA-F]{6}$/.test(el('t-colorend-hex').value)) el('t-colorend').value = el('t-colorend-hex').value; refreshFormPreview(); };
['t-text', 't-name'].forEach(id => el(id).oninput = refreshFormPreview);

// ---- 目录 CRUD ----
async function loadTitles() {
    const r = await api('/titles');
    if (!r.success) { if (r.message) toast(r.message, false); return; }
    const box = el('title-rows'); box.innerHTML = '';
    (r.titles || []).forEach(t => {
        const div = document.createElement('div'); div.className = 'row-item';
        const left = document.createElement('div');
        const pv = document.createElement('div'); pv.className = 'title-preview small'; previewInto(pv, t);
        const meta = document.createElement('div'); meta.className = 'meta';
        meta.textContent = `${t.id} · ${t.type === 'image' ? '图片 128×32' : '文字'}${t.colorEnd ? ' · 渐变' : ''}`;
        left.appendChild(pv); left.appendChild(meta); div.appendChild(left);
        const acts = document.createElement('div'); acts.className = 'acts';
        const edit = document.createElement('button'); edit.className = 'mini'; edit.textContent = '编辑'; edit.onclick = () => fillTitle(t);
        const del = document.createElement('button'); del.className = 'mini del'; del.textContent = '删除'; del.onclick = () => delTitle(t.id);
        acts.appendChild(edit); acts.appendChild(del); div.appendChild(acts);
        box.appendChild(div);
    });
    if (!r.titles || r.titles.length === 0) box.innerHTML = '<div class="row-item">暂无称号</div>';
    // 同步授予 tab 的称号下拉
    fillTitleSelect(r.titles || []);
}

function fillTitle(t) {
    el('t-id').value = t.id; el('t-name').value = t.name || ''; el('t-type').value = t.type || 'text';
    el('t-text').value = t.text || '';
    el('t-color').value = /^#[0-9a-fA-F]{6}$/.test(t.color || '') ? t.color : '#E8B923';
    el('t-color-hex').value = t.color || '';
    el('t-gradient').checked = !!t.colorEnd;
    el('colorend-row').classList.toggle('hidden', !t.colorEnd);
    el('t-colorend').value = /^#[0-9a-fA-F]{6}$/.test(t.colorEnd || '') ? t.colorEnd : '#AF002D';
    el('t-colorend-hex').value = t.colorEnd || '';
    el('t-desc').value = t.description || '';
    syncTypeFields();
    window.scrollTo({ top: 0, behavior: 'smooth' });
}
function clearTitle() {
    ['t-id', 't-name', 't-text', 't-color-hex', 't-colorend-hex', 't-desc'].forEach(id => el(id).value = '');
    el('t-type').value = 'text'; el('t-gradient').checked = false;
    el('t-image-file').value = '';
    syncTypeFields();
}
el('clear-title').onclick = clearTitle;
el('refresh-titles').onclick = loadTitles;

el('save-title').onclick = async () => {
    const t = currentFormTitle();
    if (!t.id) return toast('请填写称号 ID', false);
    if (!/^[A-Za-z0-9_-]{1,64}$/.test(t.id)) return toast('ID 仅允许字母/数字/_/-（≤64）', false);
    if (!t.name) return toast('请填写名称', false);
    if (isCollaborator()) {
        const r = await submitChange('titles', 'title.upsert', t);
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/titles', 'POST', t);
    toast(r.success ? '已保存' : (r.message || '失败'), r.success);
    if (r.success) loadTitles();
};

async function delTitle(id) {
    if (!confirm('删除称号 ' + id + ' ?（已授予玩家的记录不会自动清理，但目录中将不可见）')) return;
    if (isCollaborator()) {
        const r = await submitChange('titles', 'title.delete', { id });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '提交失败'), r.success);
        return;
    }
    const r = await api('/titles', 'DELETE', { id });
    toast(r.success ? '已删除' : '失败', r.success); loadTitles();
}

// ---- 图片上传（仅 PNG，客户端先校验 128×32）----
el('upload-title-image').onclick = async () => {
    const id = el('t-id').value.trim();
    if (!id) return toast('请先保存称号并确保 ID 正确', false);
    const f = el('t-image-file').files[0];
    if (!f) return toast('请先选择 PNG 图片', false);
    if (f.type !== 'image/png') return toast('仅支持 PNG', false);
    if (f.size > 512 * 1024) return toast('图片超过 512KB', false);
    // 客户端校验尺寸
    const dim = await new Promise(res => {
        const img = new Image(); img.onload = () => res({ w: img.naturalWidth, h: img.naturalHeight }); img.onerror = () => res(null);
        img.src = URL.createObjectURL(f);
    });
    if (!dim || dim.w !== 128 || dim.h !== 32) return toast(`尺寸必须为 128×32（当前 ${dim ? dim.w + '×' + dim.h : '未知'}）`, false);
    const dataUrl = await new Promise((res, rej) => { const r = new FileReader(); r.onload = () => res(r.result); r.onerror = rej; r.readAsDataURL(f); });
    if (isCollaborator()) {
        const r = await submitChange('titles', 'title.image', { id, image: dataUrl });
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '上传失败'), r.success);
        return;
    }
    const r = await api('/title-image', 'POST', { id, image: dataUrl });
    toast(r.success ? '图片已上传' : (r.message || '上传失败'), r.success);
    if (r.success) { el('t-type').value = 'image'; syncTypeFields(); loadTitles(); }
};

// ---- 授予 / 撤销 ----
async function loadPlayers() {
    const r = await api('/players');
    if (!r.success) return;
    const sel = el('g-player'); sel.innerHTML = '<option value="">— 选择玩家 —</option>';
    (r.players || []).forEach(p => {
        const o = document.createElement('option'); o.value = p.profileId;
        o.textContent = (p.username || '(无名)') + ' · ' + p.profileId.slice(0, 8) + '…';
        sel.appendChild(o);
    });
}
function fillTitleSelect(titles) {
    const sel = el('g-title'); const cur = sel.value; sel.innerHTML = '';
    titles.forEach(t => { const o = document.createElement('option'); o.value = t.id; o.textContent = (t.name || t.id) + ' (' + t.id + ')'; sel.appendChild(o); });
    if (cur) sel.value = cur;
}
function grantTarget() {
    const pid = el('g-profileid').value.trim() || el('g-player').value;
    const titleId = el('g-title').value;
    return { profileId: pid, titleId };
}
el('grant-btn').onclick = async () => {
    const b = grantTarget();
    if (!b.profileId || !b.titleId) { el('grant-msg').textContent = '请选择玩家和称号'; return; }
    if (isCollaborator()) {
        const r = await submitChange('titles', 'title.grant', b);
        el('grant-msg').textContent = r.message || (r.success ? '已提交审核' : '失败');
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '失败'), r.success);
        return;
    }
    const r = await api('/titles/grant', 'POST', b);
    el('grant-msg').textContent = r.message || (r.success ? '完成' : '失败');
    toast(r.message || (r.success ? '完成' : '失败'), r.success); loadHolders();
};
el('revoke-btn').onclick = async () => {
    const b = grantTarget();
    if (!b.profileId || !b.titleId) { el('grant-msg').textContent = '请选择玩家和称号'; return; }
    if (isCollaborator()) {
        const r = await submitChange('titles', 'title.revoke', b);
        el('grant-msg').textContent = r.message || (r.success ? '已提交审核' : '失败');
        toast(r.success ? '已提交审核，等待管理员批准' : (r.message || '失败'), r.success);
        return;
    }
    const r = await api('/titles/revoke', 'POST', b);
    el('grant-msg').textContent = r.message || (r.success ? '完成' : '失败');
    toast(r.message || (r.success ? '完成' : '失败'), r.success); loadHolders();
};

async function loadHolders() {
    const r = await api('/title-holders');
    if (!r.success) return;
    const box = el('holder-rows'); box.innerHTML = '';
    (r.holders || []).forEach(h => {
        const div = document.createElement('div'); div.className = 'row-item';
        div.innerHTML = `<div><div class="offer-title">${esc(h.username || '(无名)')}</div>
            <div class="meta">${esc(h.profileId.slice(0, 8))}… · 持有 ${(h.owned || []).length} · 佩戴 ${esc(h.equipped || '无')}</div></div>`;
        box.appendChild(div);
    });
    if (!r.holders || r.holders.length === 0) box.innerHTML = '<div class="row-item">暂无持有记录</div>';
}
el('refresh-holders').onclick = loadHolders;

function switchTab(tabName) {
    const tab = document.querySelector(`.tab[data-tab="${tabName}"]`);
    if (tab) tab.click();
}

function applyPendingTitleEdit(change) {
    if (!change) return;
    showPendingChangeEditBanner(change);
    const payload = change.proposedPayload || {};
    if (change.commandType === 'title.upsert') {
        fillTitle(payload);
        switchTab('catalog');
    } else if (change.commandType === 'title.image') {
        if (payload.id) el('t-id').value = payload.id;
        el('t-type').value = 'image';
        syncTypeFields();
        switchTab('catalog');
        toast('请选择新的 PNG 并点击上传，以更新原图片审核单', true);
    } else if (change.commandType === 'title.delete') {
        switchTab('catalog');
        toast('请选择新的称号并点击删除，以更新原删除审核单', true);
    } else if (change.commandType === 'title.grant' || change.commandType === 'title.revoke') {
        el('g-profileid').value = payload.profileId || '';
        if (payload.titleId) el('g-title').value = payload.titleId;
        switchTab('grant');
    }
}

el('admin-login-btn').onclick = doAdminLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doAdminLogin(); });
el('admin-logout').onclick = logout;

bootstrapAdminPage({ moduleCap: 'titles.read', onReady: async edit => { ADMIN_TOKEN = getAdminToken(); await enterConsole(); applyPendingTitleEdit(edit); } });
