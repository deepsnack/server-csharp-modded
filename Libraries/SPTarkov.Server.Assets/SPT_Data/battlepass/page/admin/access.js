// ---- 协管授权管理页 ----
'use strict';

const ACCESS_API = '/battlepass/api/admin/access';

function esc(s) { const d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
function el(id) { return document.getElementById(id); }
function toast(msg, ok) {
    const t = el('toast'); if (!t) { alert(msg); return; }
    t.textContent = msg; t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, opts = {}) {
    const token = getAdminToken();
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': token, 'X-BP-Admin-Token': token };
    const res = await fetch(ACCESS_API + path, { cache: 'no-store', headers, ...opts });
    if (res.status === 401 || res.status === 403) { clearAdminToken(); location.href = 'index.html'; return { success: false, message: '会话失效' }; }
    return res.json();
}

// ---- 现有协管列表 ----
async function load() {
    const r = await api('/grants');
    render(r.success ? (r.grants || []) : []);
}

function render(grants) {
    const box = el('grant-list');
    if (!grants.length) { box.innerHTML = '<div class="empty-msg">暂无协管授权</div>'; return; }

    box.innerHTML = grants.map(g => {
        const status = g.enabled ? '启用' : '停用';
        const statusClass = g.enabled ? 'enabled' : 'disabled';
        const caps = (g.capabilities || []).join(' · ');
        const time = g.grantedUtc ? new Date(g.grantedUtc * 1000).toLocaleDateString('zh-CN') : '';
        return `<div class="grant-item" data-pid="${esc(g.profileId)}">
            <div class="gi-main">
                <span class="gi-name">${esc(g.usernameSnapshot || g.profileId)}</span>
                <span class="gi-status ${statusClass}">${status}</span>
            </div>
            <div class="gi-caps">${esc(caps || '无能力')}</div>
            <div class="gi-foot">
                <span class="gi-time">${esc(time)}</span>
                <span class="gi-actions">
                    <button class="btn ghost small" onclick="toggleGrant('${esc(g.profileId)}', ${!g.enabled})">${g.enabled ? '撤销权限' : '恢复权限'}</button>
                    <button class="btn ghost small danger-txt" onclick="deleteGrant('${esc(g.profileId)}')">删除</button>
                </span>
            </div>
        </div>`;
    }).join('');
}

async function toggleGrant(profileId, enabled) {
    const r = await api('/grants/' + encodeURIComponent(profileId), { method: 'PATCH', body: JSON.stringify({ enabled }) });
    if (r.success) { toast(enabled ? '权限已恢复' : '权限已撤销', true); load(); } else toast(r.message || '操作失败', false);
}

async function deleteGrant(profileId) {
    if (!confirm('确认永久删除该协管记录？其现有会话会立即失效，之后可重新授权。')) return;
    const r = await api('/grants/' + encodeURIComponent(profileId), { method: 'DELETE' });
    if (r.success) { toast('协管记录已删除', true); load(); } else toast(r.message || '删除失败', false);
}

// ---- 玩家搜索 + 选择 ----
let pickedProfileId = '';
let searchTimer = null;

function clearPicked() {
    pickedProfileId = '';
    el('picked-player').style.display = 'none';
    el('add-btn').disabled = true;
    el('player-search').value = '';
}

function pickPlayer(profileId, name, granted) {
    if (granted) { toast('该玩家已是协管', false); return; }
    pickedProfileId = profileId;
    el('picked-name').textContent = name;
    el('picked-id').textContent = profileId.slice(0, 8) + '…';
    el('picked-player').style.display = 'flex';
    el('add-btn').disabled = false;
    el('player-results').style.display = 'none';
}

function renderResults(players) {
    const box = el('player-results');
    if (!players.length) { box.innerHTML = '<div class="picker-empty">无匹配玩家</div>'; box.style.display = 'block'; return; }
    box.innerHTML = players.map(p => {
        const name = p.nickname || p.username || p.profileId;
        const sub = p.username && p.nickname && p.username !== p.nickname ? ` · ${esc(p.username)}` : '';
        const grantedTag = p.granted ? '<span class="pr-granted">已授权</span>' : '';
        return `<div class="picker-row${p.granted ? ' disabled' : ''}"
            onclick="pickPlayer('${esc(p.profileId)}', '${esc(name)}', ${p.granted ? 'true' : 'false'})">
            <span class="pr-name">${esc(name)}${sub}</span>${grantedTag}
        </div>`;
    }).join('');
    box.style.display = 'block';
}

async function searchPlayers(q) {
    const r = await api('/players?q=' + encodeURIComponent(q || ''));
    if (r.success) { renderResults(r.players || []); return; }
    // 显式暴露失败原因，避免“搜索没反应”的假象
    const box = el('player-results');
    box.innerHTML = `<div class="picker-empty">${esc(r.message || '搜索失败，请重新登录后台')}</div>`;
    box.style.display = 'block';
}

el('player-search').addEventListener('input', () => {
    const q = el('player-search').value.trim();
    clearTimeout(searchTimer);
    if (q.length === 0) { el('player-results').style.display = 'none'; return; }
    searchTimer = setTimeout(() => searchPlayers(q), 220);
});

el('player-search').addEventListener('focus', () => {
    const q = el('player-search').value.trim();
    if (q.length > 0) searchPlayers(q);
});

// 点击外部关闭搜索结果
document.addEventListener('click', (e) => {
    if (!e.target.closest('.player-picker')) el('player-results').style.display = 'none';
});

el('add-btn').onclick = async () => {
    if (!pickedProfileId) return;
    const r = await api('/grants', { method: 'POST', body: JSON.stringify({ profileId: pickedProfileId }) });
    if (r.success) { toast('授权成功', true); clearPicked(); load(); }
    else toast(r.message || '授权失败', false);
};

// ---- 启动 ----
if (!getAdminToken()) { location.href = 'index.html'; }
else { load(); }
