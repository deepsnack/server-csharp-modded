let countdownTimer = null;
let countdownSeconds = 60;

document.addEventListener('DOMContentLoaded', function() {
    document.getElementById('sendCodeBtn').addEventListener('click', sendResetCode);
    document.getElementById('resetForm').addEventListener('submit', handleReset);
});

function sendResetCode() {
    const email = document.getElementById('email').value.trim();
    const sendCodeBtn = document.getElementById('sendCodeBtn');

    if (!email) { showMessage('请输入邮箱地址', 'error'); return; }
    if (!validateEmail(email)) { showMessage('请输入有效的邮箱地址', 'error'); return; }

    sendCodeBtn.disabled = true;

    fetch('/register/api/send-reset-code', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email })
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
            // 邮箱未注册等情形：明确提示，并解锁按钮以便用户更正邮箱
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

function handleReset(event) {
    event.preventDefault();

    const email = document.getElementById('email').value.trim();
    const verificationCode = document.getElementById('verificationCode').value.trim();
    const newPassword = document.getElementById('newPassword').value;
    const confirmPassword = document.getElementById('confirmPassword').value;

    if (!email || !validateEmail(email)) { showMessage('请输入有效的邮箱地址', 'error'); return; }
    if (!verificationCode || verificationCode.length < 4) { showMessage('请输入有效的验证码', 'error'); return; }
    if (!newPassword || newPassword.length < 6) { showMessage('新密码至少需要6个字符', 'error'); return; }
    if (newPassword !== confirmPassword) { showMessage('两次输入的密码不一致', 'error'); return; }

    const resetBtn = document.getElementById('resetBtn');
    resetBtn.disabled = true;

    fetch('/register/api/reset-password', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, verificationCode, newPassword })
    })
    .then(response => {
        if (!response.ok) throw new Error('重置失败');
        return response.json();
    })
    .then(data => {
        if (data.success) {
            showMessage(data.message || '密码重置成功，请使用新密码登录。', 'success');
            document.getElementById('resetForm').reset();
            if (countdownTimer) {
                clearInterval(countdownTimer);
                const sendCodeBtn = document.getElementById('sendCodeBtn');
                sendCodeBtn.disabled = false;
                sendCodeBtn.textContent = '发送验证码';
            }
        } else {
            showMessage(data.message || '重置失败，请检查输入信息后重试', 'error');
        }
    })
    .catch(error => {
        console.error('重置失败:', error);
        showMessage('重置失败，请检查输入信息后重试', 'error');
    })
    .finally(() => {
        resetBtn.disabled = false;
    });
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
