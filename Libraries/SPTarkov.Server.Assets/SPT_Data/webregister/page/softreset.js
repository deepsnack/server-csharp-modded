document.addEventListener('DOMContentLoaded', function() {
    document.getElementById('softResetForm').addEventListener('submit', handleSoftReset);
});

function handleSoftReset(event) {
    event.preventDefault();

    const username = document.getElementById('username').value.trim();
    const password = document.getElementById('password').value;
    const confirmed = document.getElementById('confirmReset').checked;

    if (!username) { showMessage('请输入用户名', 'error'); return; }
    if (!password) { showMessage('请输入密码', 'error'); return; }
    if (!confirmed) { showMessage('请先勾选确认项，知晓进度将被清空', 'warning'); return; }

    // 浏览器级二次确认，避免误触
    if (!window.confirm('确认重置账号「' + username + '」的存档？\n\n全部游戏进度将被清空并重新开荒，账号与邮箱保留。此操作不可恢复！')) {
        return;
    }

    const btn = document.getElementById('softResetBtn');
    btn.disabled = true;

    fetch('/register/api/self-soft-reset', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password })
    })
    .then(response => {
        if (!response.ok) throw new Error('重置失败');
        return response.json();
    })
    .then(data => {
        if (data.success) {
            showMessage(data.message || '存档已重置，进入游戏即可重新开荒。', 'success');
            document.getElementById('softResetForm').reset();
        } else {
            showMessage(data.message || '重置失败，请检查用户名和密码', 'error');
        }
    })
    .catch(error => {
        console.error('重置失败:', error);
        showMessage('重置失败，请稍后重试', 'error');
    })
    .finally(() => {
        btn.disabled = false;
    });
}

function showMessage(message, type) {
    const messageDiv = document.getElementById('message');
    messageDiv.classList.remove('success', 'error', 'warning');
    messageDiv.textContent = message;
    messageDiv.classList.add(type);
    setTimeout(() => messageDiv.classList.remove('success', 'error', 'warning'), 4000);
}
