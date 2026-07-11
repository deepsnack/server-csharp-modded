// ---- 共享管理鉴权模块 ----
// 由所有管理页引用，统一处理管理员 / 协管 token 的接收、存储和登录。
// 管理员：Portal #sso= 或密码登录，token 即 WebRegister admin token，即时写入。
// 协管：玩家页 exchange 得到协管会话 token，经 #bpsso= 落地为 collaborator 主体，
//       各模块页保存改走审核队列（submitChange），失效时显示无密码提示而非登录框。
'use strict';

const BP_ADMIN_TOKEN_KEY = 'bp_admin_token';
const BP_ADMIN_CAPS_KEY = 'bp_actor_caps';
const REGISTER_ADMIN_LOGIN = '/register/api/admin/login';
const BP_ADMIN_ME_API = '/battlepass/api/admin/me';
let BP_ADMIN_SESSION_ERROR = '';
let BP_ACTOR_CAPABILITIES = [];
let BP_PENDING_CHANGE_EDIT = null;

/** 获取当前管理会话 token。 */
function getAdminToken() {
    return sessionStorage.getItem(BP_ADMIN_TOKEN_KEY) || '';
}

/** 存储管理会话 token。 */
function setAdminToken(token) {
    sessionStorage.setItem(BP_ADMIN_TOKEN_KEY, token);
}

/** 清除管理会话。 */
function clearAdminToken() {
    sessionStorage.removeItem(BP_ADMIN_TOKEN_KEY);
}

/** 最近一次管理会话建立失败的可读原因。 */
function getAdminSessionError() {
    return BP_ADMIN_SESSION_ERROR;
}

function setAdminSessionError(message) {
    BP_ADMIN_SESSION_ERROR = String(message || '');
}

/** 使用 WebRegister admin 密码登录；返回的 token 可直接用于 BattlePass 管理 API。 */
async function adminLogin(password) {
    let loginData;
    try {
        const loginRes = await fetch(REGISTER_ADMIN_LOGIN, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ password }),
        });
        loginData = await loginRes.json();
    } catch (_) {
        return { success: false, message: '无法连接管理员登录服务' };
    }

    if (!loginData.success || !loginData.token) {
        return { success: false, message: loginData.message || '登录失败' };
    }

    setAdminToken(loginData.token);
    setActorType('admin');
    return { success: true, actorType: 'admin' };
}

// ---- 主体类型（admin / collaborator）----
const BP_ACTOR_TYPE_KEY = 'bp_actor_type';
function setActorType(t) { sessionStorage.setItem(BP_ACTOR_TYPE_KEY, t || ''); }
function getActorType() { return sessionStorage.getItem(BP_ACTOR_TYPE_KEY) || ''; }
function isCollaborator() { return getActorType() === 'collaborator'; }

function readAuthFragment() {
    const params = new URLSearchParams((location.hash || '').replace(/^#/, ''));
    return {
        adminToken: params.get('sso') || '',
        collaboratorToken: params.get('bpsso') || '',
        params,
    };
}

function clearAuthFragment(params) {
    params.delete('sso');
    params.delete('bpsso');
    const rest = params.toString();
    history.replaceState(null, '', location.pathname + location.search + (rest ? '#' + rest : ''));
}

/** 校验协管会话是否仍有效，同时刷新 capabilities。有效返回 true。 */
async function validateCollaboratorSession(token) {
    let data;
    try {
        const res = await fetch(BP_ADMIN_ME_API, { headers: { 'X-BP-Admin-Token': token } });
        data = await res.json();
        if (!res.ok || !data.success) { setAdminSessionError(data.message || '协管会话已失效。'); return false; }
    } catch (_) {
        setAdminSessionError('无法连接协管会话服务。');
        return false;
    }
    if (data.actorType !== 'collaborator') { setAdminSessionError('当前会话不是协管身份。'); return false; }
    setActorCapabilities(Array.isArray(data.capabilities) ? data.capabilities : []);
    return true;
}

async function validateStoredAdminSession() {
    const token = getAdminToken();
    if (!token) return false;

    // 协管会话：调用 /admin/me 校验仍有效并刷新能力；失效交由页面失效降级（不显示密码框）。
    if (getActorType() === 'collaborator') {
        const ok = await validateCollaboratorSession(token);
        // 保留 actorType='collaborator' 作为「曾是协管」标记，让失效走无密码 gate；
        // 若在此 clearActorType，handleSessionFailure 会误判为非协管并回落管理员密码框（违反铁律）。
        if (!ok) { clearAdminToken(); clearActorCapabilities(); }
        return ok;
    }

    // 管理员：管理 API 逐请求验证 token，此处不依赖额外路由，确保新旧服务端均可落地。
    setActorType('admin');
    return true;
}

/**
 * 统一管理会话入口：
 * - Portal #sso= → 落地为管理员会话（WebRegister admin token，即时写入）。
 * - 玩家页 #bpsso= → 落地为协管会话（提交审核模式），经 /admin/me 校验并拉取 capabilities。
 * - 无 fragment → 校验已存会话（区分管理员 / 协管）。
 */
async function ensureAdminSession() {
    setAdminSessionError('');
    const auth = readAuthFragment();

    if (auth.adminToken || auth.collaboratorToken) {
        // fragment 已捕获到内存后立即从地址栏清除，避免复制链接时泄漏来源 token。
        clearAuthFragment(auth.params);
        clearAdminToken();
        clearActorType();
        clearActorCapabilities();

        if (auth.collaboratorToken) {
            // 协管会话 token：落地并校验，失效则由页面显示无密码提示。
            setAdminToken(auth.collaboratorToken);
            setActorType('collaborator');
            const ok = await validateCollaboratorSession(auth.collaboratorToken);
            // 保留 actorType='collaborator' 让失效走无密码 gate，勿在此 clearActorType（否则回落密码框，违反铁律）。
            if (!ok) { clearAdminToken(); clearActorCapabilities(); return false; }
            return true;
        }

        // Portal bridge 签发的就是 WebRegister admin token，管理 API 可直接验证；
        // 不再依赖额外 exchange/me 路由，避免部署版本不一致时退回密码页。
        setAdminToken(auth.adminToken);
        setActorType('admin');
        return true;
    }

    return validateStoredAdminSession();
}

/**
 * 统一变更提交：协管在各模块页保存时改走审核队列。
 * module: shop|tasks|tracks|lottery|trader|recipes|items|flea；commandType 见后端白名单；input 为该命令的规范化输入对象。
 * 返回 { success, changeId?/message? }。
 */
async function submitChange(module, commandType, input) {
    const edit = BP_PENDING_CHANGE_EDIT;
    if (edit && (edit.module !== module || edit.commandType !== commandType)) {
        return { success: false, message: `当前正在编辑 ${edit.commandType} 审核单，请先完成或取消该编辑` };
    }

    const url = edit
        ? `/battlepass/api/admin/reviews/${encodeURIComponent(edit.id)}/edit`
        : '/battlepass/api/admin/reviews/submit';
    const body = edit
        ? { input, expectedVersion: edit.version || 1 }
        : { module, commandType, input };
    const res = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Admin-Token': getAdminToken() },
        body: JSON.stringify(body),
    });
    const data = await res.json();
    if (edit && data.success) {
        edit.version = data.version || edit.version;
        edit.updatedUtc = data.updatedUtc || edit.updatedUtc;
        edit.proposedPayload = input;
        data.editedExisting = true;
    }
    // 协管会话在提交时失效（撤权/删除/过期）→ 无密码失效降级。
    if ((res.status === 401 || data.message === '未授权') && isCollaborator()) {
        setAdminSessionError('协管授权或会话已失效，请从通行证玩家页重新进入。');
        showCollaboratorGate();
    }
    return data;
}

/** 从 ?editChange= 加载协管本人待审记录，供原模块可视化表单回填。 */
async function loadPendingChangeEdit() {
    BP_PENDING_CHANGE_EDIT = null;
    if (!isCollaborator()) return null;
    const id = new URLSearchParams(location.search || '').get('editChange');
    if (!id) return null;

    const res = await fetch(`/battlepass/api/admin/reviews/${encodeURIComponent(id)}`, {
        headers: { 'X-Admin-Token': getAdminToken() },
    });
    const data = await res.json();
    if (!res.ok || !data.success) throw new Error(data.message || '无法读取待审内容');
    if (!data.change || data.change.status !== 'pending') throw new Error('该审核单已不再处于待审核状态');
    BP_PENDING_CHANGE_EDIT = data.change;
    return BP_PENDING_CHANGE_EDIT;
}

function getPendingChangeEdit() { return BP_PENDING_CHANGE_EDIT; }

function cancelPendingChangeEdit() {
    BP_PENDING_CHANGE_EDIT = null;
    const url = new URL(location.href);
    url.searchParams.delete('editChange');
    history.replaceState(null, '', url.pathname + url.search + url.hash);
}

function showPendingChangeEditBanner(change) {
    if (!change || document.getElementById('pending-edit-banner')) return;
    const bar = document.createElement('div');
    bar.id = 'pending-edit-banner';
    bar.className = 'hint';
    bar.style.cssText = 'margin:10px 16px;padding:10px 14px;border-left:4px solid #e0a030;background:rgba(224,160,48,.12);font-weight:600;';
    bar.textContent = `正在编辑待审项目：${change.summary || change.commandType}。保存会更新原审核单，不会新建记录。`;
    const cancel = document.createElement('button');
    cancel.type = 'button'; cancel.className = 'btn ghost small'; cancel.style.marginLeft = '12px'; cancel.textContent = '取消编辑';
    cancel.onclick = () => { cancelPendingChangeEdit(); location.href = 'my-changes.html'; };
    bar.appendChild(cancel);
    const view = document.querySelector('.view:not(.hidden)') || document.body;
    const topbar = view.querySelector?.('.topbar');
    if (topbar?.nextSibling) view.insertBefore(bar, topbar.nextSibling); else view.prepend(bar);
}

/** 清除管理会话时一并清主体类型。 */
function clearActorType() { sessionStorage.removeItem(BP_ACTOR_TYPE_KEY); }

// ---- 协管能力（capabilities）----
function setActorCapabilities(caps) {
    BP_ACTOR_CAPABILITIES = Array.isArray(caps) ? caps : [];
    try { sessionStorage.setItem(BP_ADMIN_CAPS_KEY, JSON.stringify(BP_ACTOR_CAPABILITIES)); } catch (_) {}
}
function clearActorCapabilities() {
    BP_ACTOR_CAPABILITIES = [];
    sessionStorage.removeItem(BP_ADMIN_CAPS_KEY);
}
/** 当前主体能力列表；管理员视为拥有全部（含通配 '*'）。 */
function getActorCapabilities() {
    if (BP_ACTOR_CAPABILITIES.length) return BP_ACTOR_CAPABILITIES;
    try { BP_ACTOR_CAPABILITIES = JSON.parse(sessionStorage.getItem(BP_ADMIN_CAPS_KEY) || '[]') || []; } catch (_) { BP_ACTOR_CAPABILITIES = []; }
    return BP_ACTOR_CAPABILITIES;
}
/** 是否具备某能力（管理员或通配 '*' 恒为 true）。 */
function hasCapability(cap) {
    if (!isCollaborator()) return true;
    const caps = getActorCapabilities();
    return caps.includes('*') || caps.includes(cap);
}

// ---- 协管失效降级：显示无密码提示，绝不暴露管理员密码框 ----
/**
 * 协管会话失效时调用：隐藏所有管理视图与登录框，覆盖一个无密码的失效提示层，
 * 只提供「返回通行证玩家页」入口。管理员失效仍走各页原有 login-view 密码框。
 */
function showCollaboratorGate(message) {
    document.querySelectorAll('.view').forEach(v => v.classList.add('hidden'));
    let gate = document.getElementById('collab-gate');
    if (!gate) {
        gate = document.createElement('section');
        gate.id = 'collab-gate';
        gate.className = 'view collab-gate-view';
        gate.innerHTML = '<div class="collab-gate-card">'
            + '<span class="collab-gate-eyebrow">BATTLE PASS / COLLABORATOR</span>'
            + '<h1>协管会话已失效</h1>'
            + '<p id="collab-gate-message"></p>'
            + '<a class="btn primary" href="/battlepass/index.html">返回通行证玩家页</a>'
            + '<p class="collab-gate-note">此页面不接受管理员密码。授权失效时，请从通行证玩家页重新进入。</p>'
            + '</div>';
        document.body.appendChild(gate);
    }
    const msg = document.getElementById('collab-gate-message');
    if (msg) msg.textContent = message || getAdminSessionError() || '协管授权或会话已失效，请从通行证玩家页重新进入。';
    gate.classList.remove('hidden');
}

/**
 * 各页 bootstrap 会话失败时统一调用：
 * - 曾是/仍是协管 → 无密码失效提示（showCollaboratorGate）。
 * - 否则 → 返回 false，交给页面显示管理员 login-view 密码框。
 * 返回 true 表示已由本函数接管（协管），页面无需再显示密码框。
 */
function handleSessionFailure() {
    if (getActorType() === 'collaborator') {
        showCollaboratorGate(getAdminSessionError());
        return true;
    }
    return false;
}

/** 协管登出 / 返回玩家页。 */
function logoutCollaborator() {
    clearAdminToken(); clearActorType(); clearActorCapabilities();
    location.href = '/battlepass/index.html';
}

/**
 * 各业务模块页统一入口。
 * @param {object} opts
 *   - moduleCap: 该模块要求的读能力（如 'shop.read'）；协管缺此能力时展示无权 gate。
 *   - onReady: 会话有效且有权时调用（页面在此设置 ADMIN_TOKEN 并 enterConsole）。
 * 会话失败时：协管走无密码 gate（铁律），管理员回落密码登录框。
 */
async function bootstrapAdminPage(opts) {
    opts = opts || {};
    const ok = await ensureAdminSession();
    if (ok) {
        if (isCollaborator() && opts.moduleCap && !hasCapability(opts.moduleCap)) {
            showCollaboratorGate('你没有访问该模块的协管授权，请联系管理员。');
            return;
        }
        let editContext = null;
        try { editContext = await loadPendingChangeEdit(); }
        catch (error) {
            setAdminSessionError(error.message || '无法加载待审内容');
            showCollaboratorGate(getAdminSessionError());
            return;
        }
        if (typeof opts.onReady === 'function') await opts.onReady(editContext);
        return;
    }
    if (handleSessionFailure()) return;
    const msg = getAdminSessionError();
    if (msg) { const e = document.getElementById('admin-login-msg'); if (e) e.textContent = msg; }
}
