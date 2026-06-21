'use strict';

// 玩家「我的称号」页：复用通行证玩家端登录态（sessionStorage bp_token）。
const API = '/battlepass/api';
let TOKEN = sessionStorage.getItem('bp_token') || '';
let EQUIPPED = null;

function el(id) { return document.getElementById(id); }
function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function toast(msg, ok) {
    const t = el('toast'); t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json' };
    if (TOKEN) headers['X-BP-Token'] = TOKEN;
    const res = await fetch(API + path, { method: method || 'GET', headers, body: body ? JSON.stringify(body) : undefined });
    return res.json();
}

// 把称号视图渲染成预览 DOM（文字单色/渐变，或图片）。共享给佩戴预览。
function renderTitlePreview(v) {
    if (v.type === 'image' && v.imageUrl) {
        const img = document.createElement('img');
        img.className = 'title-img';
        img.src = v.imageUrl;
        img.alt = v.name || '';
        img.width = v.width || 128;
        img.height = v.height || 32;
        return img;
    }
    const span = document.createElement('span');
    span.className = 'title-text';
    span.textContent = v.text || v.name || '';
    if (v.colorEnd && v.color) {
        span.style.backgroundImage = `linear-gradient(90deg, ${v.color}, ${v.colorEnd})`;
        span.style.webkitBackgroundClip = 'text';
        span.style.backgroundClip = 'text';
        span.style.color = 'transparent';
    } else if (v.color) {
        span.style.color = v.color;
    }
    return span;
}

// ---- 登录 ----
async function doLogin() {
    const username = el('login-username').value.trim();
    const password = el('login-password').value;
    if (!username || !password) { el('login-msg').textContent = '请输入账号和密码'; return; }
    const r = await api('/login', 'POST', { username, password });
    if (!r.success) { el('login-msg').textContent = r.message || '登录失败'; return; }
    TOKEN = r.token;
    sessionStorage.setItem('bp_token', TOKEN);
    el('bp-username').textContent = username;
    enter();
}
function logout() {
    TOKEN = ''; sessionStorage.removeItem('bp_token');
    el('titles-view').classList.add('hidden'); el('login-view').classList.remove('hidden');
}
function enter() {
    el('login-view').classList.add('hidden');
    el('titles-view').classList.remove('hidden');
    loadTitles();
}

async function loadTitles() {
    const r = await api('/my-titles');
    if (!r.success) {
        if (r.message && r.message.includes('登录')) { logout(); return; }
        toast(r.message || '加载失败', false); return;
    }
    EQUIPPED = r.equipped || null;
    const grid = el('title-grid'); grid.innerHTML = '';
    const titles = r.titles || [];
    if (titles.length === 0) {
        grid.innerHTML = '<div class="title-empty">暂无称号。完成通行证奖励轨或由管理员授予后会出现在这里。</div>';
        return;
    }
    titles.forEach(v => {
        const card = document.createElement('div');
        card.className = 'title-card' + (v.id === EQUIPPED ? ' equipped' : '');
        const pv = document.createElement('div'); pv.className = 'title-preview'; pv.appendChild(renderTitlePreview(v));
        const name = document.createElement('div'); name.className = 'title-name'; name.textContent = v.name || v.id;
        const btn = document.createElement('button');
        btn.className = 'btn small ' + (v.id === EQUIPPED ? 'ghost' : 'primary');
        btn.textContent = v.id === EQUIPPED ? '已佩戴' : '佩戴';
        btn.disabled = v.id === EQUIPPED;
        btn.onclick = () => equip(v.id);
        card.appendChild(pv); card.appendChild(name); card.appendChild(btn);
        grid.appendChild(card);
    });
}

async function equip(titleId) {
    const r = await api('/my-titles/equip', 'POST', { titleId });
    toast(r.success ? '已佩戴' : (r.message || '失败'), r.success);
    if (r.success) loadTitles();
}
async function unequip() {
    const r = await api('/my-titles/equip', 'POST', { titleId: '' });
    toast(r.success ? '已卸下' : (r.message || '失败'), r.success);
    if (r.success) loadTitles();
}

el('login-btn').onclick = doLogin;
el('login-password').addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });
el('logout-btn').onclick = logout;
el('unequip-btn').onclick = unequip;

if (TOKEN) enter();
