let countdownTimer = null;
let countdownSeconds = 60;
let usernameTaken = false;       // N3：实时查重结果，占用时禁止提交
let usernameCheckTimer = null;
let activationTimer = null;
let activationValid = false;     // N2：当前输入的激活码是否有效
let preRegLockVersion = null;    // 预注册锁定的版本
let codeLockVersion = null;      // 激活码锁定的版本（优先级高于预注册）

document.addEventListener('DOMContentLoaded', function() {
    loadAvailableVersions();
    document.getElementById('sendCodeBtn').addEventListener('click', sendVerificationCode);
    document.getElementById('registerForm').addEventListener('submit', handleRegister);
    document.getElementById('email').addEventListener('blur', checkPreRegistered);
    // N3：用户名实时查重（防抖 400ms）
    document.getElementById('username').addEventListener('input', function() {
        clearTimeout(usernameCheckTimer);
        usernameCheckTimer = setTimeout(checkUsernameAvailable, 400);
    });
    // N2：激活码实时校验（防抖 500ms），命中即锁定版本
    document.getElementById('activationCode').addEventListener('input', function() {
        clearTimeout(activationTimer);
        activationTimer = setTimeout(checkActivationCode, 500);
    });
});

// N3：实时查重用户名；占用→红色提示+禁用注册按钮（提交时后端仍兜底最终查重）
function checkUsernameAvailable() {
    const username = document.getElementById('username').value.trim();
    const hint = document.getElementById('usernameHint');

    if (!username || username.length < 3) {
        usernameTaken = false;
        hint.style.display = 'none';
        updateRegisterBtn();
        return;
    }

    fetch('/register/api/check-username?username=' + encodeURIComponent(username))
        .then(r => r.json())
        .then(data => {
            if (!data.success) return;
            usernameTaken = !data.available;
            hint.textContent = data.message;
            hint.className = 'field-hint ' + (data.available ? 'ok' : 'err');
            hint.style.display = 'inline';
            updateRegisterBtn();
        })
        .catch(() => { /* 网络异常不拦截，提交时后端兜底 */ });
}

// N2：实时校验激活码；有效→锁定为码绑定的版本（优先级高于预注册）
function checkActivationCode() {
    const code = document.getElementById('activationCode').value.trim();
    const hint = document.getElementById('activationHint');

    if (!code) {
        activationValid = false;
        codeLockVersion = null;
        hint.style.display = 'none';
        applyVersionLock();
        return;
    }

    fetch('/register/api/check-activation-code', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ code })
    })
    .then(r => r.json())
    .then(data => {
        activationValid = !!data.valid;
        codeLockVersion = data.valid ? data.edition : null;
        hint.textContent = data.valid ? ('✔ ' + data.message + '（版本锁定: ' + data.edition + '）') : ('✖ ' + data.message);
        hint.className = 'field-hint ' + (data.valid ? 'ok' : 'err');
        hint.style.display = 'inline';
        applyVersionLock();
    })
    .catch(() => {
        activationValid = false;
        codeLockVersion = null;
        hint.style.display = 'none';
        applyVersionLock();
    });
}

// 统一应用版本锁定：激活码 > 预注册 > 自由选择
function applyVersionLock() {
    const vsel = document.getElementById('version');
    const note = document.getElementById('versionLockNote');
    const locked = codeLockVersion || preRegLockVersion;

    if (locked) {
        // 锁定版本可能不在普通玩家白名单内（隐藏版本），下拉缺失则动态补入
        const exists = Array.prototype.some.call(vsel.options, function(o) { return o.value === locked; });
        if (!exists) {
            const opt = document.createElement('option');
            opt.value = locked;
            opt.textContent = locked;
            vsel.appendChild(opt);
        }
        vsel.value = locked;
        vsel.disabled = true;
        note.textContent = codeLockVersion ? '⚿ 版本已由激活码锁定' : '⚿ 版本已由管理员锁定';
        note.style.display = 'inline';
    } else {
        vsel.disabled = false;
        note.style.display = 'none';
    }
}

function updateRegisterBtn() {
    document.getElementById('registerBtn').disabled = usernameTaken;
}

function checkPreRegistered() {
    const email = document.getElementById('email').value.trim();

    if (!email || !validateEmail(email)) {
        preRegLockVersion = null;
        applyVersionLock();
        return;
    }

    fetch('/register/api/check-preregistered', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email })
    })
    .then(r => r.json())
    .then(data => {
        preRegLockVersion = (data.preRegistered && data.lockedVersion) ? data.lockedVersion : null;
        applyVersionLock();
    })
    .catch(() => {
        preRegLockVersion = null;
        applyVersionLock();
    });
}

function loadAvailableVersions() {
    fetch('/register/api/versions')
        .then(response => {
            if (!response.ok) throw new Error('获取版本列表失败');
            return response.json();
        })
        .then(data => {
            const versionSelect = document.getElementById('version');
            versionSelect.innerHTML = '<option value="">请选择版本</option>';

            const versions = data.versions || data;
            if (Array.isArray(versions) && versions.length > 0) {
                versions.forEach(version => {
                    const option = document.createElement('option');
                    option.value = version;
                    option.textContent = version;
                    versionSelect.appendChild(option);
                });
            } else {
                showMessage('暂无可用版本', 'warning');
            }
        })
        .catch(error => {
            console.error('加载版本列表失败:', error);
            showMessage('加载版本列表失败，请刷新页面重试', 'error');
        });
}

function sendVerificationCode() {
    const email = document.getElementById('email').value.trim();
    const sendCodeBtn = document.getElementById('sendCodeBtn');

    if (!email) { showMessage('请输入邮箱地址', 'error'); return; }
    if (!validateEmail(email)) { showMessage('请输入有效的邮箱地址', 'error'); return; }

    sendCodeBtn.disabled = true;

    fetch('/register/api/send-verification-code', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email: email })
    })
    .then(response => {
        if (!response.ok) throw new Error('发送验证码失败');
        return response.json();
    })
    .then(data => {
        if (data.success) {
            showMessage(data.message || '验证码已发送，请查收邮件', 'success');
            startCountdown();
        } else {
            showMessage(data.message || '发送验证码失败', 'error');
            sendCodeBtn.disabled = false;
        }
    })
    .catch(error => {
        console.error('发送验证码失败:', error);
        showMessage('发送验证码失败，请稍后重试', 'error');
        sendCodeBtn.disabled = false;
    });
}

function startCountdown() {
    const sendCodeBtn = document.getElementById('sendCodeBtn');
    countdownSeconds = 60;
    sendCodeBtn.textContent = `${countdownSeconds}秒后重发`;

    countdownTimer = setInterval(function() {
        countdownSeconds--;
        if (countdownSeconds <= 0) {
            clearInterval(countdownTimer);
            sendCodeBtn.disabled = false;
            sendCodeBtn.textContent = '发送验证码';
        } else {
            sendCodeBtn.textContent = `${countdownSeconds}秒后重发`;
        }
    }, 1000);
}

function handleRegister(event) {
    event.preventDefault();

    const email = document.getElementById('email').value.trim();
    const verificationCode = document.getElementById('verificationCode').value.trim();
    const username = document.getElementById('username').value.trim();
    const password = document.getElementById('password').value;
    const confirmPassword = document.getElementById('confirmPassword').value;
    const version = document.getElementById('version').value;
    const activationCode = document.getElementById('activationCode').value.trim();

    if (!validateForm(email, verificationCode, username, password, confirmPassword, version)) return;

    if (usernameTaken) { showMessage('该账户名已被占用，请更换', 'error'); return; }
    if (activationCode && !activationValid) { showMessage('激活码无效，请检查或清空激活码', 'error'); return; }

    const registerBtn = document.getElementById('registerBtn');
    registerBtn.disabled = true;
    registerBtn.textContent = '注册中...';

    fetch('/register/api/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, verificationCode, username, password, edition: version, activationCode: activationCode || null })
    })
    .then(response => {
        if (!response.ok) throw new Error('注册失败');
        return response.json();
    })
    .then(data => {
        if (data.success) {
            showMessage('注册成功！请使用您的账号在启动器中登录。', 'success');
            document.getElementById('registerForm').reset();
            usernameTaken = false;
            activationValid = false;
            codeLockVersion = null;
            preRegLockVersion = null;
            document.getElementById('usernameHint').style.display = 'none';
            document.getElementById('activationHint').style.display = 'none';
            applyVersionLock();
            updateRegisterBtn();
            if (countdownTimer) {
                clearInterval(countdownTimer);
                const sendCodeBtn = document.getElementById('sendCodeBtn');
                sendCodeBtn.disabled = false;
                sendCodeBtn.textContent = '发送验证码';
            }
        } else {
            showMessage(data.message || '注册失败，请检查输入信息后重试', 'error');
        }
    })
    .catch(error => {
        console.error('注册失败:', error);
        showMessage('注册失败，请检查输入信息后重试', 'error');
    })
    .finally(() => {
        registerBtn.disabled = false;
        registerBtn.textContent = '注册';
    });
}

function validateForm(email, verificationCode, username, password, confirmPassword, version) {
    if (!email || !validateEmail(email)) { showMessage('请输入有效的邮箱地址', 'error'); return false; }
    if (!verificationCode || verificationCode.length < 4) { showMessage('请输入有效的验证码', 'error'); return false; }
    if (!username || username.length < 3) { showMessage('用户名至少需要3个字符', 'error'); return false; }
    if (username.length > 20) { showMessage('用户名不能超过20个字符', 'error'); return false; }
    if (!password || password.length < 6) { showMessage('密码至少需要6个字符', 'error'); return false; }
    if (password !== confirmPassword) { showMessage('两次输入的密码不一致', 'error'); return false; }
    if (!version) { showMessage('请选择版本', 'error'); return false; }
    return true;
}

function validateEmail(email) {
    return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

function showMessage(message, type) {
    const messageDiv = document.getElementById('message');
    messageDiv.classList.remove('success', 'error', 'warning');
    messageDiv.textContent = message;
    messageDiv.classList.add(type);
    setTimeout(() => messageDiv.classList.remove('success', 'error', 'warning'), 4000);
}
