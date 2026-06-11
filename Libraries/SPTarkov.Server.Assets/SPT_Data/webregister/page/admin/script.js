const TOKEN_KEY = 'spt_admin_token';

// 来自 Portal SSO 的免密登录：sidecar 验证 Portal 短 token 后，把签发的 admin token 放在 URL fragment
// (#sso=...) 跳转过来。fragment 不会发往服务端、不进服务器日志；读取后立即存入 sessionStorage 并抹掉。
(function consumePortalSso() {
    const m = location.hash.match(/[#&]sso=([^&]+)/);
    if (m) {
        try { sessionStorage.setItem(TOKEN_KEY, decodeURIComponent(m[1])); } catch (e) {}
        history.replaceState(null, '', location.pathname + location.search);
    }
})();

window.addEventListener('DOMContentLoaded', async () => {
    const token = sessionStorage.getItem(TOKEN_KEY);
    if (token) {
        const ok = await apiFetch('GET', '/register/api/admin/config', null, token);
        if (ok && ok.success) { showAdminPanel(); return; }
        sessionStorage.removeItem(TOKEN_KEY);
    }
    document.getElementById('loginPanel').style.display = '';
});

async function adminLogin() {
    const password = document.getElementById('adminPassword').value;
    const btn = document.getElementById('loginBtn');
    btn.disabled = true;
    const data = await apiFetch('POST', '/register/api/admin/login', { password });
    if (data && data.success) {
        sessionStorage.setItem(TOKEN_KEY, data.token);
        showAdminPanel();
    } else {
        showMsg('loginMessage', (data && data.message) || '登录失败', 'error');
        btn.disabled = false;
    }
}

document.getElementById('adminPassword').addEventListener('keydown', function(e) {
    if (e.key === 'Enter') adminLogin();
});

function adminLogout() {
    sessionStorage.removeItem(TOKEN_KEY);
    document.getElementById('adminPanel').style.display = 'none';
    document.getElementById('loginPanel').style.display = '';
    document.getElementById('adminPassword').value = '';
}

async function showAdminPanel() {
    document.getElementById('loginPanel').style.display = 'none';
    document.getElementById('adminPanel').style.display = '';
    await Promise.all([loadVersionSection(), loadPreRegistrations(), loadActivationCodes(), loadActivationLogs()]);
    renderCodeEditionSelect();
}

var allVersions = [];
var allowedVersions = [];

async function loadVersionSection() {
    var allData = await apiFetch('GET', '/register/api/admin/all-versions');
    var cfgData = await apiFetch('GET', '/register/api/admin/config');
    allVersions = (allData && allData.versions) || [];
    allowedVersions = (cfgData && cfgData.allowedVersions) || [];
    renderVersionCheckList();
    renderPreVersionSelect();
}

function renderVersionCheckList() {
    var container = document.getElementById('versionCheckList');
    container.innerHTML = '';
    allVersions.forEach(function(v) {
        var label = document.createElement('label');
        label.className = 'check-item';
        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.value = v;
        cb.checked = allowedVersions.indexOf(v) !== -1;
        label.appendChild(cb);
        label.appendChild(document.createTextNode(v));
        container.appendChild(label);
    });
}

function renderPreVersionSelect() {
    var sel = document.getElementById('preVersion');
    sel.innerHTML = '<option value="">选择版本</option>';
    allVersions.forEach(function(v) {
        var opt = document.createElement('option');
        opt.value = v;
        opt.textContent = v;
        sel.appendChild(opt);
    });
}

async function saveAllowedVersions() {
    var checked = [];
    document.querySelectorAll('#versionCheckList input[type=checkbox]:checked').forEach(function(cb) {
        checked.push(cb.value);
    });
    var data = await apiFetch('POST', '/register/api/admin/config', { allowedVersions: checked });
    if (data && data.success) {
        allowedVersions = checked;
        showMsg('versionMessage', '已保存', 'success');
    } else {
        showMsg('versionMessage', (data && data.message) || '保存失败', 'error');
    }
}

async function loadPreRegistrations() {
    var data = await apiFetch('GET', '/register/api/admin/preregistrations');
    var regs = (data && data.registrations) || {};
    renderPreregTable(regs);
}

function renderPreregTable(regs) {
    var tbody = document.getElementById('preregBody');
    tbody.innerHTML = '';
    var entries = Object.keys(regs);
    if (entries.length === 0) {
        var tr = document.createElement('tr');
        tr.innerHTML = '<td colspan="3" style="color:#aaa;text-align:center;padding:16px;">暂无预注册记录</td>';
        tbody.appendChild(tr);
        return;
    }
    entries.forEach(function(email) {
        var version = regs[email];
        var tr = document.createElement('tr');
        var tdEmail = document.createElement('td');
        tdEmail.textContent = email;
        var tdVersion = document.createElement('td');
        tdVersion.textContent = version;
        var tdAction = document.createElement('td');
        var delBtn = document.createElement('button');
        delBtn.className = 'delete-btn';
        delBtn.textContent = '删除';
        delBtn.onclick = function() { deletePreRegistration(email); };
        tdAction.appendChild(delBtn);
        tr.appendChild(tdEmail);
        tr.appendChild(tdVersion);
        tr.appendChild(tdAction);
        tbody.appendChild(tr);
    });
}

async function addPreRegistration() {
    var email = document.getElementById('preEmail').value.trim();
    var version = document.getElementById('preVersion').value;
    if (!email || !version) { showMsg('preregMessage', '邮箱和版本均不能为空', 'error'); return; }
    var data = await apiFetch('POST', '/register/api/admin/preregistrations', { email: email, version: version });
    if (data && data.success) {
        document.getElementById('preEmail').value = '';
        document.getElementById('preVersion').value = '';
        showMsg('preregMessage', '已添加', 'success');
        await loadPreRegistrations();
    } else {
        showMsg('preregMessage', (data && data.message) || '添加失败', 'error');
    }
}

async function deletePreRegistration(email) {
    var data = await apiFetch('DELETE', '/register/api/admin/preregistrations', { email: email });
    if (data && data.success) {
        showMsg('preregMessage', '已删除', 'success');
        await loadPreRegistrations();
    } else {
        showMsg('preregMessage', (data && data.message) || '删除失败', 'error');
    }
}

// ==================== 账号管理（N1） ====================

async function searchAccounts() {
    var q = document.getElementById('accountQuery').value.trim();
    var data = await apiFetch('GET', '/register/api/admin/accounts?query=' + encodeURIComponent(q));
    if (!data || !data.success) {
        showMsg('accountMessage', (data && data.message) || '搜索失败', 'error');
        return;
    }
    renderAccountTable(data.accounts || []);
}

function renderAccountTable(accounts) {
    var tbody = document.getElementById('accountBody');
    tbody.innerHTML = '';
    if (accounts.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" style="color:#aaa;text-align:center;padding:16px;">无匹配账号</td></tr>';
        return;
    }
    accounts.forEach(function(a) {
        var tr = document.createElement('tr');
        [a.username, a.email || '—', a.edition || '—', a.lastLogin ? new Date(a.lastLogin).toLocaleString() : '从未登录'].forEach(function(text) {
            var td = document.createElement('td');
            td.textContent = text;
            tr.appendChild(td);
        });
        var tdAction = document.createElement('td');
        var delBtn = document.createElement('button');
        delBtn.className = 'delete-btn';
        delBtn.textContent = '删除账号';
        delBtn.onclick = function() { deleteAccount(a); };
        tdAction.appendChild(delBtn);
        tr.appendChild(tdAction);
        tbody.appendChild(tr);
    });
}

async function deleteAccount(account) {
    // 二次确认：删除不可逆（存档真删 + 邮箱释放）
    var sure = window.confirm('确认删除账号「' + account.username + '」？\n\n存档将被彻底删除，邮箱 ' + (account.email || '(无)') + ' 将被释放可重新注册。此操作不可恢复！');
    if (!sure) return;
    var data = await apiFetch('DELETE', '/register/api/admin/accounts/' + encodeURIComponent(account.profileId));
    if (data && data.success) {
        showMsg('accountMessage', data.message || '已删除', 'success');
        await searchAccounts();
    } else {
        showMsg('accountMessage', (data && data.message) || '删除失败', 'error');
    }
}

// ==================== 注册激活码（N2） ====================

function renderCodeEditionSelect() {
    var sel = document.getElementById('codeEdition');
    sel.innerHTML = '<option value="">绑定版本</option>';
    // 激活码可绑定全部版本（含对普通玩家隐藏的版本）
    allVersions.forEach(function(v) {
        var opt = document.createElement('option');
        opt.value = v;
        opt.textContent = v;
        sel.appendChild(opt);
    });
}

async function loadActivationCodes() {
    var data = await apiFetch('GET', '/register/api/admin/activation-codes');
    renderCodeTable((data && data.codes) || []);
}

function renderCodeTable(codes) {
    var tbody = document.getElementById('codeBody');
    tbody.innerHTML = '';
    if (codes.length === 0) {
        tbody.innerHTML = '<tr><td colspan="8" style="color:#aaa;text-align:center;padding:16px;">暂无激活码</td></tr>';
        return;
    }
    var statusText = { Unused: '未使用', Used: '已使用', Revoked: '已作废' };
    codes.forEach(function(c) {
        var tr = document.createElement('tr');
        [
            c.code,
            c.edition,
            statusText[c.status] || c.status,
            c.usedByEmail || '—',
            c.usedByUsername || '—',
            c.usedAt ? new Date(c.usedAt).toLocaleString() : '—',
            c.note || '—'
        ].forEach(function(text) {
            var td = document.createElement('td');
            td.textContent = text;
            tr.appendChild(td);
        });
        var tdAction = document.createElement('td');
        if (c.status === 'Unused') {
            var btn = document.createElement('button');
            btn.className = 'delete-btn';
            btn.textContent = '作废';
            btn.onclick = function() { revokeActivationCode(c.code); };
            tdAction.appendChild(btn);
        } else {
            tdAction.textContent = '—';
        }
        tr.appendChild(tdAction);
        tbody.appendChild(tr);
    });
}

async function createActivationCodes() {
    var edition = document.getElementById('codeEdition').value;
    var count = parseInt(document.getElementById('codeCount').value, 10) || 1;
    var note = document.getElementById('codeNote').value.trim();
    if (!edition) { showMsg('codeMessage', '必须选择绑定版本', 'error'); return; }
    var data = await apiFetch('POST', '/register/api/admin/activation-codes', { edition: edition, count: count, note: note || null });
    if (data && data.success) {
        showMsg('codeMessage', '已创建 ' + data.codes.length + ' 个激活码', 'success');
        document.getElementById('codeNote').value = '';
        await loadActivationCodes();
        await loadActivationLogs();
    } else {
        showMsg('codeMessage', (data && data.message) || '创建失败', 'error');
    }
}

async function revokeActivationCode(code) {
    if (!window.confirm('确认作废激活码 ' + code + '？')) return;
    var data = await apiFetch('DELETE', '/register/api/admin/activation-codes/' + encodeURIComponent(code));
    if (data && data.success) {
        showMsg('codeMessage', '已作废', 'success');
        await loadActivationCodes();
        await loadActivationLogs();
    } else {
        showMsg('codeMessage', (data && data.message) || '作废失败', 'error');
    }
}

// ==================== 激活码日志（N2） ====================

async function loadActivationLogs() {
    var data = await apiFetch('GET', '/register/api/admin/activation-codes/logs');
    if (!data || !data.success) return;
    document.getElementById('logRetention').value = data.retentionDays || 0;
    var tbody = document.getElementById('logBody');
    tbody.innerHTML = '';
    var logs = data.logs || [];
    if (logs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="color:#aaa;text-align:center;padding:16px;">暂无日志</td></tr>';
        return;
    }
    var eventText = { created: '创建', used: '使用', revoked: '作废', released: '回滚' };
    // 最新在前
    logs.slice().reverse().forEach(function(e) {
        var tr = document.createElement('tr');
        [
            new Date(e.time).toLocaleString(),
            eventText[e.event] || e.event,
            e.code,
            e.edition || '—',
            e.email || '—',
            e.username || '—'
        ].forEach(function(text) {
            var td = document.createElement('td');
            td.textContent = text;
            tr.appendChild(td);
        });
        tbody.appendChild(tr);
    });
}

async function saveLogRetention() {
    var days = parseInt(document.getElementById('logRetention').value, 10) || 0;
    var data = await apiFetch('POST', '/register/api/admin/activation-codes/log-retention', { days: days });
    if (data && data.success) {
        showMsg('logMessage', data.message || '已保存', 'success');
    } else {
        showMsg('logMessage', (data && data.message) || '保存失败', 'error');
    }
}

function exportActivationLogs() {
    var token = sessionStorage.getItem(TOKEN_KEY) || '';
    fetch('/register/api/admin/activation-codes/logs/export', { headers: { 'X-Admin-Token': token } })
        .then(function(res) {
            if (!res.ok) throw new Error('export failed');
            return res.blob();
        })
        .then(function(blob) {
            var a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = 'activation-log.csv';
            a.click();
            URL.revokeObjectURL(a.href);
        })
        .catch(function() { showMsg('logMessage', '导出失败', 'error'); });
}

async function clearActivationLogs() {
    if (!window.confirm('确认清空全部激活码日志？此操作不可恢复（建议先导出）。')) return;
    var data = await apiFetch('DELETE', '/register/api/admin/activation-codes/logs');
    if (data && data.success) {
        showMsg('logMessage', data.message || '已清空', 'success');
        await loadActivationLogs();
    } else {
        showMsg('logMessage', (data && data.message) || '清空失败', 'error');
    }
}

async function apiFetch(method, url, body, tokenOverride) {
    var token = tokenOverride || sessionStorage.getItem(TOKEN_KEY) || '';
    var opts = { method: method, headers: { 'Content-Type': 'application/json', 'X-Admin-Token': token } };
    if (body !== undefined && body !== null) opts.body = JSON.stringify(body);
    try {
        var res = await fetch(url, opts);
        return await res.json();
    } catch(e) {
        return null;
    }
}

function showMsg(elementId, text, type) {
    var el = document.getElementById(elementId);
    el.className = 'message ' + type;
    el.textContent = text;
    setTimeout(function() { el.textContent = ''; el.className = 'message'; }, 4000);
}
