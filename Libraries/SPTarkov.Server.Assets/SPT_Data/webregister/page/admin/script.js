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
    await Promise.all([loadVersionSection(), loadPreRegistrations()]);
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
