'use strict';

const COLLAB_TOKEN_KEY = 'bp_collab_token';
const COLLAB_PRINCIPAL_KEY = 'bp_collab_principal';
const COLLAB_ME_API = '/battlepass/api/admin/me';

function getCollaboratorToken() {
    return sessionStorage.getItem(COLLAB_TOKEN_KEY) || '';
}

function clearCollaboratorSession() {
    sessionStorage.removeItem(COLLAB_TOKEN_KEY);
    sessionStorage.removeItem(COLLAB_PRINCIPAL_KEY);
}

function saveCollaboratorSession(token, principal) {
    sessionStorage.setItem(COLLAB_TOKEN_KEY, token);
    sessionStorage.setItem(COLLAB_PRINCIPAL_KEY, JSON.stringify(principal || {}));
}

function showCollaboratorGate(message) {
    const text = document.getElementById('gate-message');
    if (text) text.textContent = message || '协管会话无效，请从通行证玩家页重新进入。';
    document.getElementById('session-gate')?.classList.remove('hidden');
    document.getElementById('workspace')?.classList.add('hidden');
}

async function parseJsonResponse(response) {
    try { return await response.json(); } catch (_) { return {}; }
}

async function validateCollaboratorSession() {
    const token = getCollaboratorToken();
    if (!token) return { success: false, message: '未找到协管会话。' };

    let response;
    try {
        response = await fetch(COLLAB_ME_API, { headers: { 'X-BP-Admin-Token': token } });
    } catch (_) {
        return { success: false, message: '无法连接协管会话服务。' };
    }

    const data = await parseJsonResponse(response);
    if (!response.ok || !data.success) return { success: false, message: data.message || '协管会话已失效。' };
    if (data.actorType !== 'collaborator') return { success: false, message: '当前会话不是协管身份，已拒绝进入。' };

    const principal = {
        actorType: data.actorType,
        actorId: data.actorId || '',
        displayName: data.displayName || data.actorId || '协管',
        capabilities: Array.isArray(data.capabilities) ? data.capabilities : [],
        expiresUtc: data.expiresUtc || 0,
    };
    saveCollaboratorSession(token, principal);
    return { success: true, principal };
}

async function ensureCollaboratorSession() {
    const result = await validateCollaboratorSession();
    if (!result.success) {
        clearCollaboratorSession();
        showCollaboratorGate(result.message);
    }
    return result;
}

async function collaboratorFetchJson(url, options = {}) {
    const token = getCollaboratorToken();
    if (!token) throw new Error('协管会话不存在');
    const headers = Object.assign({}, options.headers || {}, { 'X-Admin-Token': token });
    if (options.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
    const response = await fetch(url, Object.assign({}, options, { headers }));
    const data = await parseJsonResponse(response);
    if (!response.ok || data.success === false) {
        if (data.message === '未授权') {
            clearCollaboratorSession();
            showCollaboratorGate('协管授权或会话已失效，请从通行证玩家页重新进入。');
        }
        throw new Error(data.message || `请求失败（${response.status}）`);
    }
    return data;
}

function logoutCollaborator() {
    clearCollaboratorSession();
    location.href = '/battlepass/index.html';
}
