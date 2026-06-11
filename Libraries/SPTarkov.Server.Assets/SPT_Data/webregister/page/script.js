let countdownTimer = null;
let countdownSeconds = 60;

document.addEventListener('DOMContentLoaded', function() {
    loadAvailableVersions();
    document.getElementById('sendCodeBtn').addEventListener('click', sendVerificationCode);
    document.getElementById('registerForm').addEventListener('submit', handleRegister);
    document.getElementById('email').addEventListener('blur', checkPreRegistered);
});

function checkPreRegistered() {
    const email = document.getElementById('email').value.trim();
    const vsel = document.getElementById('version');
    const note = document.getElementById('versionLockNote');

    if (!email || !validateEmail(email)) {
        vsel.disabled = false;
        note.style.display = 'none';
        return;
    }

    fetch('/register/api/check-preregistered', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email })
    })
    .then(r => r.json())
    .then(data => {
        if (data.preRegistered && data.lockedVersion) {
            // 管理员锁定的版本可能不在普通玩家白名单内（预注册无视限制），
            // 此时下拉里没有对应 option，直接赋值会静默失败导致 version 为空、
            // 提交时误报"请选择版本"。若缺失则动态补入该选项再选中。
            const exists = Array.prototype.some.call(vsel.options, function(o) {
                return o.value === data.lockedVersion;
            });
            if (!exists) {
                const opt = document.createElement('option');
                opt.value = data.lockedVersion;
                opt.textContent = data.lockedVersion;
                vsel.appendChild(opt);
            }
            vsel.value = data.lockedVersion;
            vsel.disabled = true;
            note.style.display = 'inline';
        } else {
            vsel.disabled = false;
            note.style.display = 'none';
        }
    })
    .catch(() => {
        vsel.disabled = false;
        note.style.display = 'none';
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

    if (!validateForm(email, verificationCode, username, password, confirmPassword, version)) return;

    const registerBtn = document.getElementById('registerBtn');
    registerBtn.disabled = true;
    registerBtn.textContent = '注册中...';

    fetch('/register/api/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, verificationCode, username, password, edition: version })
    })
    .then(response => {
        if (!response.ok) throw new Error('注册失败');
        return response.json();
    })
    .then(data => {
        if (data.success) {
            showMessage('注册成功！请使用您的账号在启动器中登录。', 'success');
            document.getElementById('registerForm').reset();
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
