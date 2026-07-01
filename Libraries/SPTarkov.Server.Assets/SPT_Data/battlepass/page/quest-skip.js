'use strict';

const API = '/battlepass/api';
let TOKEN = sessionStorage.getItem('bp_token') || '';
let STATE = null;
let PENDING = null;
let SUBMITTING = false;

function el(id) { return document.getElementById(id); }

function toast(message, ok) {
    const box = el('toast');
    box.textContent = message || '';
    box.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { box.className = 'toast'; }, 2800);
}

async function api(path, method, body) {
    const headers = { 'Content-Type': 'application/json' };
    if (TOKEN) headers['X-BP-Token'] = TOKEN;
    const response = await fetch(API + path, {
        method: method || 'GET',
        headers,
        body: body ? JSON.stringify(body) : undefined,
    });
    return response.json();
}

async function login() {
    const username = el('login-username').value.trim();
    const password = el('login-password').value;
    if (!username || !password) {
        el('login-msg').textContent = '请输入账号和密码';
        return;
    }

    el('login-btn').disabled = true;
    try {
        const result = await api('/login', 'POST', { username, password });
        if (!result.success) {
            el('login-msg').textContent = result.message || '登录失败';
            return;
        }

        TOKEN = result.token;
        sessionStorage.setItem('bp_token', TOKEN);
        enter();
    } catch {
        el('login-msg').textContent = '无法连接服务器';
    } finally {
        el('login-btn').disabled = false;
    }
}

function logout() {
    TOKEN = '';
    STATE = null;
    sessionStorage.removeItem('bp_token');
    el('quest-skip-view').classList.add('hidden');
    el('login-view').classList.remove('hidden');
}

function enter() {
    el('login-view').classList.add('hidden');
    el('quest-skip-view').classList.remove('hidden');
    loadState();
}

async function loadState() {
    el('refresh-btn').disabled = true;
    el('task-count').textContent = '读取中';
    try {
        const result = await api('/quest-skip/state');
        if (!result.success) {
            if ((result.message || '').includes('登录') || (result.message || '').includes('会话')) {
                logout();
                return;
            }
            toast(result.message || '读取任务失败', false);
            return;
        }

        STATE = result;
        renderState();
    } catch {
        toast('无法连接服务器', false);
    } finally {
        el('refresh-btn').disabled = false;
    }
}

function renderState() {
    el('ticket-count').textContent = String(STATE.ticketCount || 0);
    el('raid-warning').classList.toggle('hidden', !STATE.inRaid);
    const tasks = STATE.tasks || [];
    el('task-count').textContent = `共 ${tasks.length} 个任务`;

    const list = el('quest-list');
    list.replaceChildren();
    if (tasks.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'quest-skip-empty';
        empty.textContent = '当前没有正在进行且可展示的任务。';
        list.appendChild(empty);
        return;
    }

    tasks.forEach(task => list.appendChild(renderTask(task)));
}

function renderTask(task) {
    const card = document.createElement('article');
    card.className = 'quest-skip-card';

    const heading = document.createElement('h3');
    heading.className = 'quest-skip-title';
    heading.textContent = task.titleZh || '任务';
    card.appendChild(heading);

    const objectives = document.createElement('div');
    objectives.className = 'quest-objectives';
    (task.objectives || []).forEach(objective => {
        objectives.appendChild(renderObjective(task, objective));
    });
    card.appendChild(objectives);
    return card;
}

function renderObjective(task, objective) {
    const row = document.createElement('div');
    row.className = 'quest-objective' + (objective.completed ? ' completed' : '');

    const copy = document.createElement('div');
    copy.className = 'quest-objective-copy';
    const name = document.createElement('strong');
    name.className = 'quest-objective-name';
    name.textContent = objective.conditionNameZh || '特殊任务条件';
    const description = document.createElement('p');
    description.className = 'quest-objective-description';
    description.textContent = objective.conditionDescriptionZh || '完成特殊任务条件';
    copy.append(name, description);

    const action = document.createElement('div');
    action.className = 'quest-objective-action';
    if (objective.completed) {
        const badge = document.createElement('span');
        badge.className = 'quest-completed-badge';
        badge.textContent = '已完成';
        action.appendChild(badge);
    } else {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'btn primary small quest-skip-button';
        button.textContent = STATE.inRaid ? '战局中不可跳过' : (STATE.ticketCount > 0 ? '跳过目标' : '缺少跳过券');
        button.disabled = !objective.canSkip || !objective.actionId;
        button.onclick = () => openConfirm(task, objective);
        action.appendChild(button);
    }

    row.append(copy, action);
    return row;
}

function openConfirm(task, objective) {
    if (SUBMITTING || !objective.actionId) return;
    PENDING = { task, objective };
    el('skip-confirm-task').textContent = task.titleZh || '任务';
    el('skip-confirm-objective').textContent = `${objective.conditionNameZh}：${objective.conditionDescriptionZh}`;
    el('skip-confirm').classList.remove('hidden');
    el('skip-submit').focus();
}

function closeConfirm() {
    if (SUBMITTING) return;
    PENDING = null;
    el('skip-confirm').classList.add('hidden');
}

async function submitSkip() {
    if (SUBMITTING || !PENDING?.objective?.actionId) return;
    SUBMITTING = true;
    el('skip-submit').disabled = true;
    el('skip-cancel').disabled = true;
    el('skip-submit').textContent = '正在保存…';
    try {
        const result = await api('/quest-skip/objectives/skip', 'POST', { actionId: PENDING.objective.actionId });
        if (!result.success && ((result.message || '').includes('登录') || (result.message || '').includes('会话'))) {
            logout();
            return;
        }
        toast(result.message || (result.success ? '任务目标已跳过' : '跳过失败'), result.success);
        el('skip-confirm').classList.add('hidden');
        PENDING = null;
        await loadState();
    } catch {
        toast('无法连接服务器，未执行跳过', false);
    } finally {
        SUBMITTING = false;
        el('skip-submit').disabled = false;
        el('skip-cancel').disabled = false;
        el('skip-submit').textContent = '确认消耗并跳过';
    }
}

el('login-btn').onclick = login;
el('login-password').addEventListener('keydown', event => { if (event.key === 'Enter') login(); });
el('logout-btn').onclick = logout;
el('refresh-btn').onclick = loadState;
el('skip-cancel').onclick = closeConfirm;
el('skip-submit').onclick = submitSkip;
el('skip-confirm').addEventListener('click', event => { if (event.target === el('skip-confirm')) closeConfirm(); });
document.addEventListener('keydown', event => { if (event.key === 'Escape') closeConfirm(); });

if (TOKEN) enter();
