'use strict';

let TASK_HUB_TOKEN = getAdminToken();

function el(id) { return document.getElementById(id); }

function hubToast(msg, ok) {
    const t = el('toast');
    t.textContent = msg;
    t.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { t.className = 'toast'; }, 2600);
}

function desiredTab() {
    const tab = new URLSearchParams(location.search || '').get('tab');
    return tab === 'trader' ? 'trader' : 'bp';
}

function editParam() {
    const id = new URLSearchParams(location.search || '').get('editChange');
    return id ? `?editChange=${encodeURIComponent(id)}` : '';
}

function setFrameSources(activeTab) {
    const edit = editParam();
    el('bp-task-frame').src = 'bp-tasks.html' + (activeTab === 'bp' ? edit : '');
    el('trader-task-frame').src = 'quests.html' + (activeTab === 'trader' ? edit : '');
}

function showTab(tab) {
    const bp = tab !== 'trader';
    document.querySelectorAll('[data-task-tab]').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.taskTab === (bp ? 'bp' : 'trader'));
    });
    el('bp-task-frame').classList.toggle('hidden', !bp);
    el('trader-task-frame').classList.toggle('hidden', bp);
    const url = new URL(location.href);
    url.searchParams.set('tab', bp ? 'bp' : 'trader');
    history.replaceState(null, '', url.pathname + url.search + url.hash);
}

async function doLogin() {
    const r = await adminLogin(el('admin-pass').value);
    if (!r.success) {
        el('admin-login-msg').textContent = r.message || '登录失败';
        return;
    }
    TASK_HUB_TOKEN = getAdminToken();
    enterHub();
}

function logout() {
    clearAdminToken();
    clearActorType();
    TASK_HUB_TOKEN = '';
    el('task-hub-view').classList.add('hidden');
    el('login-view').classList.remove('hidden');
}

function enterHub() {
    el('login-view').classList.add('hidden');
    el('task-hub-view').classList.remove('hidden');

    const canBp = !isCollaborator() || hasCapability('tasks.read');
    const canTrader = !isCollaborator() || hasCapability('quests.read');
    el('bp-task-tab').style.display = canBp ? '' : 'none';
    el('trader-task-tab').style.display = canTrader ? '' : 'none';

    if (!canBp && !canTrader) {
        showCollaboratorGate('你没有访问任务模块的协管授权，请联系管理员。');
        return;
    }

    let tab = desiredTab();
    if (tab === 'bp' && !canBp) tab = 'trader';
    if (tab === 'trader' && !canTrader) tab = 'bp';
    setFrameSources(tab);
    showTab(tab);
}

document.querySelectorAll('[data-task-tab]').forEach(btn => {
    btn.onclick = () => showTab(btn.dataset.taskTab);
});
el('admin-login-btn').onclick = doLogin;
el('admin-pass').addEventListener('keydown', e => { if (e.key === 'Enter') doLogin(); });
el('admin-logout').onclick = logout;

(async function () {
    const ok = await ensureAdminSession();
    if (ok) {
        TASK_HUB_TOKEN = getAdminToken();
        enterHub();
        return;
    }
    if (handleSessionFailure()) return;
    if (getAdminSessionError()) el('admin-login-msg').textContent = getAdminSessionError();
})();
