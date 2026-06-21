'use strict';

const API = '/battlepass/api';
let TOKEN = sessionStorage.getItem('bp_token') || '';

function el(id) { return document.getElementById(id); }

function toast(msg, ok) {
    const t = el('toast');
    t.textContent = msg;
    t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json' };
    if (TOKEN) headers['X-BP-Token'] = TOKEN;
    const res = await fetch(API + path, {
        method: method || 'GET',
        headers,
        body: body ? JSON.stringify(body) : undefined
    });
    return res.json();
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
    showMain();
}

function logout() {
    TOKEN = '';
    sessionStorage.removeItem('bp_token');
    el('main-view').classList.add('hidden');
    el('login-view').classList.remove('hidden');
}

function showMain() {
    el('login-view').classList.add('hidden');
    el('main-view').classList.remove('hidden');
    loadState();
    loadTasks();
}

// ---- 状态 / 奖励轨 ----
function fmtRemaining(endUtc) {
    const secs = endUtc - Math.floor(Date.now() / 1000);
    if (secs <= 0) return '赛季已结束';
    const d = Math.floor(secs / 86400);
    const h = Math.floor((secs % 86400) / 3600);
    return `剩余 ${d} 天 ${h} 小时`;
}

async function loadState() {
    const r = await api('/state');
    if (!r.success) {
        if (r.message && r.message.indexOf('未登录') >= 0) { logout(); }
        toast(r.message || '加载失败', false);
        return;
    }

    el('season-name').textContent = r.season.name;
    el('season-timer').textContent = fmtRemaining(r.season.endUtc);
    el('bp-level').textContent = r.progress.level;
    el('bp-username').textContent = r.progress.username || '';

    const need = r.progress.xpToNext || 0;
    const pct = need > 0 ? Math.min(100, (r.progress.xp / need) * 100) : 100;
    el('xp-fill').style.width = pct + '%';
    el('xp-text').textContent = need > 0 ? `${r.progress.xp} / ${need} XP` : '满级';

    const pf = el('premium-flag');
    if (r.progress.premiumUnlocked) { pf.textContent = '付费轨 PREMIUM'; pf.classList.add('on'); }
    else { pf.textContent = '免费轨 FREE'; pf.classList.remove('on'); }

    renderRail(r.levels, r.progress.premiumUnlocked);
    renderCycle(r.cycle, r.progress, r.season);
}

// ---- 满级循环奖励 ----
let cycleState = null; // 缓存当前循环奖励，供「一键领取」用

// 奖励类型可读名（非物品奖励无 tpl 时的展示兜底）
function rewardTypeLabel(r) {
    switch ((r.type || 'item').toLowerCase()) {
        case 'purchaseright': return '商人购买权';
        case 'recipe': return '藏身处配方';
        case 'title': return '称号';
        default: return '奖励';
    }
}

// ---- 多奖励展开浮层（全局单例；展开仅展示，领取仍是整格一键领）----
function ensureRewardOverlay() {
    let ov = el('rw-overlay');
    if (ov) return ov;
    ov = document.createElement('div');
    ov.id = 'rw-overlay'; ov.className = 'rw-overlay hidden';
    ov.innerHTML = '<div class="rw-overlay-box"><div class="rw-overlay-head"><span id="rw-overlay-title">本级全部奖励</span><button id="rw-overlay-close">×</button></div><div id="rw-overlay-list"></div></div>';
    document.body.appendChild(ov);
    ov.addEventListener('click', e => { if (e.target === ov) ov.classList.add('hidden'); });
    ov.querySelector('#rw-overlay-close').onclick = () => ov.classList.add('hidden');
    return ov;
}

function showRewardOverlay(rewards, title) {
    const ov = ensureRewardOverlay();
    ov.querySelector('#rw-overlay-title').textContent = title || '本级全部奖励';
    const list = ov.querySelector('#rw-overlay-list');
    list.innerHTML = '';
    rewards.forEach(r => {
        const row = document.createElement('div');
        row.className = 'rw-overlay-row';
        const hasTpl = r.tpl && /^[a-fA-F0-9]{24}$/.test(r.tpl);
        const rtype = (r.type || 'item').toLowerCase();
        row.innerHTML = `
            ${hasTpl ? `<img class="rw-icon" src="${API}/icons/${r.tpl}" alt="" onerror="this.remove()" />` : '<span class="rw-icon rw-icon-ph"></span>'}
            <span class="rw-ov-name">${(r.name || (hasTpl ? r.tpl.slice(0, 8) + '…' : rewardTypeLabel(r)))}</span>
            ${rtype === 'item' ? `<span class="rw-ov-count">×${r.count || 1}</span>` : `<span class="rw-ov-badge">${rewardTypeLabel(r)}</span>`}
            ${r.foundInRaid ? '<span class="rw-ov-badge">FIR</span>' : ''}
            ${r.featured ? '<span class="rw-ov-badge featured">大奖</span>' : ''}`;
        list.appendChild(row);
    });
    ov.classList.remove('hidden');
}

// 多奖励格：右上角可点角标 +N，点击弹浮层展示全部
function attachExpandBadge(cell, rewards, title) {
    if (!rewards || rewards.length <= 1) return;
    const b = document.createElement('button');
    b.className = 'rw-expand';
    b.textContent = '+' + (rewards.length - 1);
    b.title = '查看本格全部 ' + rewards.length + ' 项奖励';
    b.onclick = e => { e.stopPropagation(); showRewardOverlay(rewards, title); };
    cell.appendChild(b);
}

function cycleCellContent(rewards) {
    const cell = document.createElement('div');
    cell.className = 'cell';
    if (!rewards || rewards.length === 0) { cell.classList.add('empty'); cell.textContent = '—'; return cell; }
    if (rewards.some(x => x.featured)) cell.classList.add('featured');
    const main = rewards[0];
    if (main.tpl && /^[a-fA-F0-9]{24}$/.test(main.tpl)) {
        const icon = document.createElement('img');
        icon.className = 'rw-icon'; icon.alt = main.name || ''; icon.loading = 'lazy';
        icon.src = API + '/icons/' + main.tpl;
        icon.onerror = () => icon.remove();
        cell.appendChild(icon);
    }
    const name = document.createElement('div'); name.className = 'rw-name';
    name.textContent = main.name || (main.tpl ? main.tpl.slice(0, 8) : rewardTypeLabel(main));
    const cnt = document.createElement('div'); cnt.className = 'rw-count';
    cnt.textContent = '×' + main.count;
    cell.appendChild(name); cell.appendChild(cnt);
    attachExpandBadge(cell, rewards, '循环奖励 · 全部');
    return cell;
}

function cycleRoundBtn(track, n, claimed, eligible) {
    const b = document.createElement('button');
    b.className = 'cyc-claim ' + track;
    const tn = track === 'free' ? '免费' : '付费';
    if (claimed) { b.textContent = tn + '已领'; b.disabled = true; b.classList.add('done'); return b; }
    if (!eligible) { b.textContent = '付费未解锁'; b.disabled = true; b.classList.add('locked'); return b; }
    b.textContent = '领' + tn;
    b.onclick = () => claimCycle(n, track);
    return b;
}

function renderCycle(cycle, progress, season) {
    const sec = el('cycle-section');
    const hasCycle = cycle && (((cycle.free || []).length) || ((cycle.premium || []).length));
    if (!hasCycle) { sec.classList.add('hidden'); cycleState = null; return; }

    cycleState = { cycle, premiumUnlocked: progress.premiumUnlocked };
    sec.classList.remove('hidden');

    const completed = cycle.completed || 0;
    el('cycle-sub').textContent = `满级后每满 ${season.cycleXp || 0} XP 一轮 · 已完成 ${completed} 轮`;

    const fc = el('cycle-free-cell'); fc.innerHTML = ''; fc.appendChild(cycleCellContent(cycle.free));
    const pc = el('cycle-premium-cell'); pc.innerHTML = ''; pc.appendChild(cycleCellContent(cycle.premium));

    const claimedFree = new Set(cycle.claimedFree || []);
    const claimedPremium = new Set(cycle.claimedPremium || []);
    const rounds = el('cycle-rounds');
    rounds.innerHTML = '';
    if (completed === 0) {
        rounds.innerHTML = '<div class="cyc-empty">满级后继续获得通行证经验即可累计循环轮次。</div>';
    } else {
        for (let n = 1; n <= completed; n++) {
            const chip = document.createElement('div'); chip.className = 'cyc-round';
            const lbl = document.createElement('span'); lbl.className = 'cyc-round-n'; lbl.textContent = '第 ' + n + ' 轮';
            chip.appendChild(lbl);
            chip.appendChild(cycleRoundBtn('free', n, claimedFree.has(n), true));
            chip.appendChild(cycleRoundBtn('premium', n, claimedPremium.has(n), progress.premiumUnlocked));
            rounds.appendChild(chip);
        }
    }

    let anyFree = false, anyPrem = false;
    for (let n = 1; n <= completed; n++) {
        if (!claimedFree.has(n)) anyFree = true;
        if (progress.premiumUnlocked && !claimedPremium.has(n)) anyPrem = true;
    }
    el('cycle-free-all').disabled = !anyFree;
    el('cycle-premium-all').disabled = !anyPrem;
}

async function claimCycle(cycle, track) {
    const r = await api('/claim-cycle', 'POST', { cycle, track });
    toast(r.message, r.success);
    if (r.success) loadState();
}

async function claimAllCycle(track) {
    if (!cycleState) return;
    const c = cycleState.cycle;
    if (track === 'premium' && !cycleState.premiumUnlocked) { toast('付费轨未解锁', false); return; }
    const claimed = new Set((track === 'premium' ? c.claimedPremium : c.claimedFree) || []);
    let claimedAny = false;
    for (let n = 1; n <= (c.completed || 0); n++) {
        if (claimed.has(n)) continue;
        const r = await api('/claim-cycle', 'POST', { cycle: n, track });
        if (!r.success) { toast(r.message, false); break; }
        claimedAny = true;
    }
    if (claimedAny) { toast('循环奖励已领取', true); loadState(); }
}

function rewardCell(rewards, type, level, reached, claimed, premiumUnlocked) {
    const cell = document.createElement('div');
    cell.className = 'cell ' + type;
    if (!rewards || rewards.length === 0) { cell.classList.add('empty'); cell.textContent = '—'; return cell; }
    if (rewards.some(x => x.featured)) cell.classList.add('featured');

    const main = rewards[0];
    // 物品图标（同源接口，CDN 兜底）。加载失败则移除，不影响布局。
    if (main.tpl && /^[a-fA-F0-9]{24}$/.test(main.tpl)) {
        const icon = document.createElement('img');
        icon.className = 'rw-icon loading';
        icon.alt = main.name || '';
        icon.loading = 'lazy';
        icon.src = API + '/icons/' + main.tpl;
        icon.onload = () => icon.classList.remove('loading');
        icon.onerror = () => icon.remove();
        cell.appendChild(icon);
    }
    // 非物品奖励可能无 tpl：名称兜底到类型可读名，避免空引用
    const name = document.createElement('div'); name.className = 'rw-name';
    name.textContent = main.name || (main.tpl ? main.tpl.slice(0, 8) : rewardTypeLabel(main));
    const cnt = document.createElement('div'); cnt.className = 'rw-count';
    cnt.textContent = '×' + main.count;
    cell.appendChild(name); cell.appendChild(cnt);
    attachExpandBadge(cell, rewards, 'Lv' + level + ' · ' + (type === 'free' ? '免费' : '付费') + '轨全部奖励');

    // 奖励类型角标：区分「商人购买权」「藏身处配方」与普通直发物品
    const rtype = (main.type || 'item').toLowerCase();
    if (rtype === 'purchaseright' || rtype === 'recipe') {
        const badge = document.createElement('div');
        badge.className = 'rw-badge ' + rtype;
        badge.textContent = rtype === 'purchaseright' ? '商人购买权' : '藏身处配方';
        cell.appendChild(badge);
    }

    if (claimed) { cell.classList.add('claimed'); return cell; }
    if (!reached) { cell.classList.add('locked'); return cell; }
    if (type === 'premium' && !premiumUnlocked) { cell.classList.add('locktrack', 'locked'); return cell; }

    // 已解锁且可领取：强高亮 + 发光，突出「通行证可用」状态
    cell.classList.add('claimable');
    const btn = document.createElement('button'); btn.className = 'claim'; btn.textContent = '领取';
    btn.onclick = () => claim(level, type);
    cell.appendChild(btn);
    return cell;
}

function renderRail(levels, premiumUnlocked) {
    const rail = el('rail');
    rail.innerHTML = '';
    levels.forEach(lv => {
        const col = document.createElement('div');
        col.className = 'lvl-col' + (lv.unlocked ? ' reached' : '');
        const head = document.createElement('div'); head.className = 'lvl-head'; head.textContent = 'LV ' + lv.level;
        col.appendChild(head);
        col.appendChild(rewardCell(lv.free, 'free', lv.level, lv.unlocked, lv.claimedFree, premiumUnlocked));
        col.appendChild(rewardCell(lv.premium, 'premium', lv.level, lv.unlocked, lv.claimedPremium, premiumUnlocked));
        rail.appendChild(col);
    });
}

async function claim(level, track) {
    const r = await api('/claim', 'POST', { level, track });
    toast(r.message, r.success);
    if (r.success) loadState();
}

// ---- 任务 ----
let taskDisplayCount = 4;
async function loadTasks() {
    const list = el('task-list');
    list.innerHTML = '<div class="task-empty">任务加载中...</div>';

    const r = await api('/tasks');
    if (!r || !r.success) {
        updateRefreshButtons([], {});
        list.innerHTML = `<div class="task-empty">${esc((r && r.message) || '任务加载失败')}</div>`;
        if (r && r.message && r.message.indexOf('未登录') >= 0) { logout(); }
        return;
    }

    const tasks = Array.isArray(r.tasks) ? r.tasks : [];
    const refreshLeft = r.refreshLeft || {};
    taskDisplayCount = Number(r.displayCount) > 0 ? Number(r.displayCount) : 4;
    updateRefreshButtons(tasks, refreshLeft);
    list.innerHTML = '';

    tasks.forEach(t => {
        const taskId = t.taskId || t.TaskId || '';
        const scope = String(t.scope || t.Scope || '').toLowerCase();
        const rotation = String(t.rotation || t.Rotation || '').toLowerCase();
        const conditionType = String(t.conditionType || t.ConditionType || '').toLowerCase();
        const completed = !!(t.completed ?? t.done);
        const progress = Math.max(0, Number(t.progress || 0));
        const target = Math.max(0, Number(t.target || t.count || 0));
        const xp = Math.max(0, Number(t.xp || 0));
        const pct = target > 0 ? Math.min(100, (progress / target) * 100) : (completed ? 100 : 0);
        const dailyLeft = Math.max(0, Number(refreshLeft.daily ?? r.freeRerollsLeft ?? 0));
        const canReroll = !!taskId && !completed && rotation !== 'fixed' && scope === 'daily' && dailyLeft > 0;
        const div = document.createElement('div');
        div.className = 'task' + (completed ? ' done' : '');
        div.innerHTML =
            `<div class="t-head"><span class="t-title">${esc(t.title || taskId || '未命名任务')}</span><span class="t-scope">${esc(scope || 'task')}</span></div>
             <div class="t-desc">${esc(t.description)}</div>
             <div class="t-prog"><div class="pf" style="width:${pct}%"></div></div>
             <div class="t-foot"><span class="t-xp">+${xp} XP · ${progress}/${target}</span></div>`;
        if (canReroll) {
            const rb = document.createElement('button'); rb.className = 't-reroll'; rb.textContent = '刷新';
            rb.onclick = () => reroll(taskId);
            div.querySelector('.t-foot').appendChild(rb);
        }
        // 上交物品任务：网页内上交入口（读取存档匹配物品 → 移除 → 计进度）
        if (conditionType === 'handoveritem' && !completed && taskId) {
            const hb = document.createElement('button'); hb.className = 't-handover'; hb.textContent = '上交物品';
            const panel = document.createElement('div'); panel.className = 't-handover-panel'; panel.style.display = 'none';
            hb.onclick = () => toggleHandover(taskId, panel);
            div.querySelector('.t-foot').appendChild(hb);
            div.appendChild(panel);
        }
        list.appendChild(div);
    });
    if (tasks.length === 0) list.innerHTML = '<div class="task-empty">暂无任务</div>';
    applyTaskDisplayLimit();
}

// 三类任务各自的「主动刷新」按钮：仅当该类有活跃任务时显示，剩余次数为 0 时禁用
function updateRefreshButtons(tasks, left) {
    [['daily', 'refresh-daily', '刷新每日'], ['weekly', 'refresh-weekly', '刷新每周'], ['season', 'refresh-season', '刷新赛季']]
        .forEach(([scope, id, label]) => {
            const btn = el(id);
            if (!btn) return;
            const hasScope = (tasks || []).some(t => String(t.scope || t.Scope || '').toLowerCase() === scope);
            const n = Math.max(0, Number((left || {})[scope] ?? 0));
            btn.style.display = hasScope ? '' : 'none';
            btn.disabled = n <= 0;
            btn.textContent = `${label}（剩${n}）`;
        });
}

// 主页任务区只显示 displayCount 张卡，其余靠滚轮/滑块查看
function applyTaskDisplayLimit() {
    const list = el('task-list');
    requestAnimationFrame(() => {
        list.style.maxHeight = '';
        const cards = list.querySelectorAll('.task');
        if (taskDisplayCount > 0 && cards.length > taskDisplayCount) {
            const h = cards[taskDisplayCount].offsetTop - cards[0].offsetTop;
            list.style.maxHeight = h > 0 ? `min(${h}px, 62vh)` : '';
        } else {
            list.style.maxHeight = '';
        }
    });
}

async function reroll(taskId) {
    const r = await api('/tasks/reroll', 'POST', { taskId });
    toast(r.success ? '已刷新' : (r.message || '刷新失败'), r.success);
    if (r.success) loadTasks();
}

// ---- 上交物品（网页入口）----
async function toggleHandover(taskId, panel) {
    if (panel.style.display !== 'none') { panel.style.display = 'none'; return; }
    panel.style.display = '';
    panel.innerHTML = '<div class="t-desc">加载中…</div>';
    const r = await api('/handover/' + encodeURIComponent(taskId));
    if (!r.success) { panel.innerHTML = `<div class="t-desc">${esc(r.message || '加载失败')}</div>`; return; }
    const info = r.info || {};
    const items = info.items || [];
    const rows = items.length ? items.map(it => `
        <div class="ho-row">
            <img class="rw-icon" src="${API}/icons/${it.tpl}" alt="" onerror="this.remove()" />
            <span class="ho-name">${esc(it.name || it.tpl)}</span>
            <span class="ho-id">${esc(it.tpl)}</span>
            <span class="ho-have">存档可交 ${it.available}</span>
        </div>`).join('') : '<div class="t-desc">存档中没有匹配的可上交物品</div>';
    const totalAvail = items.reduce((s, it) => s + (it.available || 0), 0);
    const canGo = totalAvail > 0 && info.remaining > 0;
    panel.innerHTML = `
        <div class="ho-head">还需上交 ${info.remaining}${info.findInRaid ? ' · 仅限战局中找到(FiR)' : ''}</div>
        ${rows}
        <button class="t-handover-go"${canGo ? '' : ' disabled'}>确认上交（本次最多 ${Math.max(0, Math.min(totalAvail, info.remaining))}）</button>`;
    const go = panel.querySelector('.t-handover-go');
    if (go && canGo) go.addEventListener('click', () => doHandover(taskId));
}

async function doHandover(taskId) {
    const r = await api('/handover', 'POST', { taskId });
    if (!r.success) { toast(r.message || '上交失败', false); return; }
    const res = r.result || {};
    toast(res.completed ? `上交完成！+${res.gainedXp} XP` : `已上交 ${res.handed}，进度 ${res.progress}/${res.target}`, true);
    loadState();
    loadTasks();
}

async function refreshScope(scope) {
    const label = { daily: '每日', weekly: '每周', season: '赛季' }[scope] || '';
    const r = await api('/tasks/refresh', 'POST', { scope });
    toast(r.success ? `${label}任务已刷新` : (r.message || '刷新失败'), r.success);
    if (r.success) {
        loadState();
        loadTasks();
    }
}

async function redeem() {
    const code = el('redeem-code').value.trim();
    if (!code) return;
    const r = await api('/redeem', 'POST', { code });
    el('redeem-msg').textContent = r.message || '';
    el('redeem-msg').className = 'msg ' + (r.success ? 'ok' : '');
    if (r.success) { el('redeem-code').value = ''; loadState(); }
}

function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

// ---- 绑定 ----
el('login-btn').onclick = doLogin;
el('login-password').addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });
el('logout-btn').onclick = logout;
el('redeem-btn').onclick = redeem;
el('refresh-daily').onclick = () => refreshScope('daily');
el('refresh-weekly').onclick = () => refreshScope('weekly');
el('refresh-season').onclick = () => refreshScope('season');
el('cycle-free-all').onclick = () => claimAllCycle('free');
el('cycle-premium-all').onclick = () => claimAllCycle('premium');
window.addEventListener('resize', applyTaskDisplayLimit);

if (TOKEN) { showMain(); }
