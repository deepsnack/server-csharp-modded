// ---- 共享管理 API 请求模块 ----
// 统一处理 Content-Type、token 注入、401/403/202 响应。
'use strict';

/**
 * 发起管理 API 请求。
 * @param {string} path - 相对路径（如 '/battlepass/api/admin/shop'）
 * @param {object} opts - fetch 选项（method、body 等）
 * @returns {Promise<object>} 响应 JSON
 */
async function adminApi(path, opts = {}) {
    const token = getAdminToken();
    const headers = {
        'Content-Type': 'application/json',
        'X-Admin-Token': token,
        'X-BP-Admin-Token': token,
        ...(opts.headers || {}),
    };

    const res = await fetch(path, { ...opts, headers });

    // 401/403 统一跳转登录
    if (res.status === 401 || res.status === 403) {
        clearAdminToken();
        location.href = 'index.html';
        return { success: false, message: '会话失效' };
    }

    const data = await res.json();

    // 202 待审核响应：协管提交后的统一提示
    if (res.status === 202 && data.mode === 'pendingReview') {
        showPendingReviewNotice(data);
        return data;
    }

    return data;
}

/** 显示"已提交审核"的统一提示。 */
function showPendingReviewNotice(data) {
    const msg = data.message || '变更已提交审核，尚未生效';
    const changeId = data.changeId || '';
    // 简单 alert；后续可改为 toast
    alert(`${msg}\n\n变更 ID: ${changeId}\n可在「我的提交」中查看状态。`);
}
