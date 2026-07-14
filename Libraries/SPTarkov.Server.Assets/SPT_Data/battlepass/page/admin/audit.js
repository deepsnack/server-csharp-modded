'use strict';

const AUDIT_API = '/battlepass/api/admin/audit';
const MODULE_NAMES = { shop: '商店', tasks: '通行证任务', tracks: '奖励轨', lottery: '抽奖', trader: '商人', recipes: '配方', items: '物品管控', quests: '商人任务', titles: '称号', flea: '跳蚤' };
const EVENT_NAMES = { submit: '协管提交', approve: '管理员批准', reject: '管理员驳回', rollback: '管理员回溯' };

function el(id) { return document.getElementById(id); }
function esc(value) { const div = document.createElement('div'); div.textContent = value == null ? '' : String(value); return div.innerHTML; }
function toast(message, ok) {
    const node = el('toast');
    node.textContent = message; node.className = 'toast show ' + (ok ? 'ok' : 'err');
    setTimeout(() => { node.className = 'toast'; }, 2800);
}

async function api(path, options = {}) {
    const token = getAdminToken();
    const headers = { 'Content-Type': 'application/json', 'X-Admin-Token': token, 'X-BP-Admin-Token': token };
    const response = await fetch(AUDIT_API + path, { cache: 'no-store', headers, ...options });
    return response.json();
}

async function loadAudit() {
    const params = new URLSearchParams({ limit: '200' });
    const module = el('audit-module').value;
    const eventType = el('audit-event').value;
    if (module) params.set('module', module);
    if (eventType) params.set('eventType', eventType);
    const result = await api('?' + params.toString());
    if (!result.success) { toast(result.message || '审计日志加载失败', false); return; }
    el('retention-days').value = result.retentionDays || 180;
    renderAudit(result.items || []);
}

function renderAudit(items) {
    const list = el('audit-list');
    if (!items.length) { list.innerHTML = '<div class="empty-msg">暂无审计日志</div>'; return; }
    list.innerHTML = items.map(item => {
        const actor = item.actor?.displayName || item.actor?.actorId || '未知';
        const submitter = item.submittedBy?.displayName || item.submittedBy?.actorId || '未知';
        const time = new Date((item.createdUtc || 0) * 1000).toLocaleString('zh-CN');
        const rollback = item.reversible
            ? item.rolledBack
                ? `<span class="audit-rolled">已于 ${esc(new Date(item.rolledBackUtc * 1000).toLocaleString('zh-CN'))} 回溯</span>`
                : `<button class="btn ghost small danger-txt" data-rollback="${esc(item.id)}">一键回溯</button>`
            : '';
        return `<article class="audit-entry event-${esc(item.eventType)}">
            <div class="audit-entry-head">
                <span class="audit-event">${esc(EVENT_NAMES[item.eventType] || item.eventType)}</span>
                <span class="audit-module">${esc(MODULE_NAMES[item.module] || item.module)}</span>
                <strong>${esc(item.targetDisplayName || item.targetKey)}</strong>
                <time>${esc(time)}</time>
            </div>
            <p>${esc(item.summary || '')}</p>
            <div class="audit-meta">操作者：${esc(actor)} · 原提交人：${esc(submitter)} · 变更编号：${esc(item.changeId)}</div>
            ${item.detail ? `<div class="audit-detail">${esc(item.detail)}</div>` : ''}
            <div class="audit-actions">${rollback}</div>
        </article>`;
    }).join('');

    list.querySelectorAll('[data-rollback]').forEach(button => {
        button.onclick = () => rollbackAudit(button.dataset.rollback);
    });
}

async function rollbackAudit(auditId) {
    const reason = prompt('请输入回溯原因（可留空）：', '') ?? null;
    if (reason === null) return;
    if (!confirm('确认撤销这条批准记录对应的改动？若目标已有后续修改，系统会自动拒绝。')) return;
    const result = await api('/' + encodeURIComponent(auditId) + '/rollback', {
        method: 'POST', body: JSON.stringify({ reason: reason.trim() }),
    });
    toast(result.message || (result.success ? '回溯成功' : '回溯失败'), result.success);
    if (result.success) loadAudit();
}

el('save-retention').onclick = async () => {
    const retentionDays = Number(el('retention-days').value);
    const result = await api('/settings', { method: 'PATCH', body: JSON.stringify({ retentionDays }) });
    toast(result.success ? `已保存为 ${result.retentionDays} 天，清理 ${result.purged || 0} 条过期日志` : (result.message || '保存失败'), result.success);
};
el('refresh-audit').onclick = loadAudit;
el('audit-module').onchange = loadAudit;
el('audit-event').onchange = loadAudit;

bootstrapAdminPage({
    onReady: async () => {
        if (isCollaborator()) { showCollaboratorGate('审计与回溯仅管理员可用。'); return; }
        await loadAudit();
    },
});
