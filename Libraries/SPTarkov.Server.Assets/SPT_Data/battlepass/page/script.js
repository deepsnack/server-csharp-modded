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
    name.textContent = main.name || (main.tpl ? main.tpl.slice(0, 8) : '奖励');
    const cnt = document.createElement('div'); cnt.className = 'rw-count';
    cnt.textContent = '×' + main.count + (rewards.length > 1 ? ' +' + (rewards.length - 1) : '');
    cell.appendChild(name); cell.appendChild(cnt);
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
    const name = document.createElement('div'); name.className = 'rw-name'; name.textContent = main.name || main.tpl.slice(0, 8);
    const cnt = document.createElement('div'); cnt.className = 'rw-count';
    cnt.textContent = '×' + main.count + (rewards.length > 1 ? ' +' + (rewards.length - 1) : '');
    cell.appendChild(name); cell.appendChild(cnt);

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
async function loadTasks() {
    const r = await api('/tasks');
    if (!r.success) return;

    el('rerolls').textContent = '免费刷新 ' + r.freeRerollsLeft;
    const refreshBtn = el('refresh-daily');
    refreshBtn.disabled = r.freeRerollsLeft <= 0;
    const list = el('task-list');
    list.innerHTML = '';

    r.tasks.forEach(t => {
        const div = document.createElement('div');
        div.className = 'task' + (t.completed ? ' done' : '');
        const pct = t.target > 0 ? Math.min(100, (t.progress / t.target) * 100) : (t.completed ? 100 : 0);
        const canReroll = !t.completed && t.rotation !== 'fixed' && t.scope === 'daily' && r.freeRerollsLeft > 0;
        div.innerHTML =
            `<div class="t-head"><span class="t-title">${esc(t.title)}</span><span class="t-scope">${t.scope}</span></div>
             <div class="t-desc">${esc(t.description)}</div>
             <div class="t-prog"><div class="pf" style="width:${pct}%"></div></div>
             <div class="t-foot"><span class="t-xp">+${t.xp} XP · ${t.progress}/${t.target}</span></div>`;
        if (canReroll) {
            const rb = document.createElement('button'); rb.className = 't-reroll'; rb.textContent = '刷新';
            rb.onclick = () => reroll(t.taskId);
            div.querySelector('.t-foot').appendChild(rb);
        }
        list.appendChild(div);
    });
    if (r.tasks.length === 0) list.innerHTML = '<div class="t-desc">暂无任务</div>';
}

async function reroll(taskId) {
    const r = await api('/tasks/reroll', 'POST', { taskId });
    toast(r.success ? '已刷新' : (r.message || '刷新失败'), r.success);
    if (r.success) loadTasks();
}

async function refreshDailyTasks() {
    const r = await api('/tasks/refresh', 'POST', { scope: 'daily' });
    toast(r.success ? '每日任务已刷新' : (r.message || '刷新失败'), r.success);
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

function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

// ---- 绑定 ----
el('login-btn').onclick = doLogin;
el('login-password').addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });
el('logout-btn').onclick = logout;
el('redeem-btn').onclick = redeem;
el('refresh-daily').onclick = refreshDailyTasks;
el('cycle-free-all').onclick = () => claimAllCycle('free');
el('cycle-premium-all').onclick = () => claimAllCycle('premium');

if (TOKEN) { showMain(); }
