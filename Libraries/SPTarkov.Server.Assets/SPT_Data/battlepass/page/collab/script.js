'use strict';

const MODULES = {
    shop: {
        title: '网页商店', kicker: 'SHOP CONFIGURATION', description: '维护商品、删除货架项或调整全局刷新周期。',
        readCapability: 'shop.read', submitCapability: 'shop.submit',
        operations: [['shop.upsert', '新增 / 编辑商品'], ['shop.delete', '删除商品'], ['shop.refreshPeriod', '修改刷新周期']],
    },
    tasks: {
        title: '任务模板', kicker: 'TASK TEMPLATES', description: '新增、编辑、删除任务模板，或调整任务生成规格。',
        readCapability: 'tasks.read', submitCapability: 'tasks.submit',
        operations: [['task.upsert', '新增 / 编辑任务'], ['task.delete', '删除任务'], ['task.genSpec', '修改生成规格']],
    },
    tracks: {
        title: '奖励轨', kicker: 'REWARD TRACKS', description: '以完整 JSON 草稿提交双轨奖励配置。',
        readCapability: 'tracks.read', submitCapability: 'tracks.submit', operations: [['tracks.save', '保存完整奖励轨']],
    },
    lottery: {
        title: '抽奖配置', kicker: 'LOTTERY DRAFTS', description: '维护全局设置、奖池草稿和兑换商品草稿；运营动作不向协管开放。',
        readCapability: 'lottery.read', submitCapability: 'lottery.submit',
        operations: [
            ['lottery.settings', '修改全局设置'], ['lottery.pool.upsert', '新增 / 编辑奖池草稿'],
            ['lottery.pool.delete', '删除奖池草稿'], ['lottery.shop.upsert', '新增 / 编辑兑换商品草稿'],
            ['lottery.shop.delete', '删除兑换商品草稿'],
        ],
    },
};

const MODULE_ORDER = ['shop', 'tasks', 'tracks', 'lottery'];
const MODULE_NAMES = { shop: '商店', tasks: '任务', tracks: '奖励轨', lottery: '抽奖' };
const STATUS_NAMES = { pending: '待审核', applying: '应用中', applied: '已通过', rejected: '已驳回', conflict: '有冲突', failed: '应用失败', withdrawn: '已撤回' };
let principal = null;
let currentModule = '';
let currentState = null;
let selectedRecord = null;

function el(id) { return document.getElementById(id); }
function esc(value) { return String(value ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
function pretty(value) { return JSON.stringify(value, null, 2); }
function hasCapability(capability) { const caps = new Set(principal?.capabilities || []); return caps.has('*') || caps.has(capability); }
function toast(message, ok = true) {
    const node = el('toast'); node.textContent = message; node.className = `toast show ${ok ? 'ok' : 'error'}`;
    setTimeout(() => { node.className = 'toast'; }, 3000);
}
function showModuleError(message) { const node = el('module-error'); node.textContent = message || ''; node.classList.toggle('hidden', !message); }

function renderNavigation() {
    const allowed = MODULE_ORDER.filter(key => hasCapability(MODULES[key].readCapability));
    el('module-nav').innerHTML = allowed.map(key => `<button class="nav-item" type="button" data-module="${key}"><span>${MODULES[key].title}</span><small>${MODULES[key].kicker}</small></button>`).join('');
    document.querySelectorAll('[data-module]').forEach(button => { button.onclick = () => selectModule(button.dataset.module); });
    if (allowed.length) selectModule(allowed[0]); else showModuleError('当前授权未包含任何可读取模块。');
}

async function loadState(moduleKey) {
    if (moduleKey === 'shop') return collaboratorFetchJson('/battlepass/api/admin/shop');
    if (moduleKey === 'tasks') return collaboratorFetchJson('/battlepass/api/admin/tasks');
    if (moduleKey === 'tracks') return collaboratorFetchJson('/battlepass/api/admin/tracks');
    if (moduleKey === 'lottery') {
        const [settings, pools, shop] = await Promise.all([
            collaboratorFetchJson('/battlepass/api/admin/lottery/settings'),
            collaboratorFetchJson('/battlepass/api/admin/lottery/pools'),
            collaboratorFetchJson('/battlepass/api/admin/lottery/shop'),
        ]);
        return { success: true, settings: settings.settings || {}, pools: pools.pools || [], items: shop.items || [] };
    }
    throw new Error('未知模块');
}

async function selectModule(moduleKey) {
    if (!MODULES[moduleKey]) return;
    currentModule = moduleKey; selectedRecord = null;
    el('changes-view').classList.add('hidden'); el('module-view').classList.remove('hidden');
    document.querySelectorAll('[data-module]').forEach(node => node.classList.toggle('active', node.dataset.module === moduleKey));
    el('changes-nav').classList.remove('active');
    const config = MODULES[moduleKey];
    el('module-kicker').textContent = config.kicker; el('module-title').textContent = config.title; el('module-description').textContent = config.description;
    el('operation-select').innerHTML = config.operations.map(([value, label]) => `<option value="${value}">${label}</option>`).join('');
    el('submit-btn').disabled = !hasCapability(config.submitCapability); showModuleError('');
    await refreshCurrentModule();
}

async function refreshCurrentModule() {
    if (!currentModule) return;
    el('record-list').innerHTML = '<div class="empty">正在读取当前配置…</div>';
    try { currentState = await loadState(currentModule); renderRecords(); startNewDraft(); }
    catch (error) { currentState = null; el('record-list').innerHTML = '<div class="empty">读取失败</div>'; showModuleError(error.message); }
}

function recordButton(kind, index, title, subtitle) {
    return `<button class="record" type="button" data-kind="${kind}" data-index="${index}"><strong>${esc(title)}</strong><span>${esc(subtitle || '')}</span></button>`;
}

function renderRecords() {
    let html = ''; let count = 0;
    if (currentModule === 'shop') {
        const offers = currentState.offers || [];
        html = offers.map((item, i) => recordButton('offer', i, item.name || item.id, item.id)).join('');
        html += recordButton('refresh', 0, '全局刷新周期', `${currentState.refreshSeconds || 0} 秒`); count = offers.length + 1;
    } else if (currentModule === 'tasks') {
        const tasks = currentState.tasks || [];
        html = tasks.map((item, i) => recordButton('task', i, item.title || item.id, item.id)).join(''); count = tasks.length;
    } else if (currentModule === 'tracks') {
        const tracks = currentState.tracks || {}; html = recordButton('tracks', 0, '完整双轨配置', `${Object.keys(tracks).length} 个等级`); count = Object.keys(tracks).length;
    } else if (currentModule === 'lottery') {
        const pools = currentState.pools || []; const items = currentState.items || [];
        html = recordButton('settings', 0, '全局设置', 'Lottery settings');
        html += pools.map((item, i) => recordButton('pool', i, item.name || item.id, `奖池 · ${item.status || 'draft'}`)).join('');
        html += items.map((item, i) => recordButton('shopItem', i, item.name || item.id, `兑换商品 · ${item.status || 'draft'}`)).join(''); count = 1 + pools.length + items.length;
    }
    el('record-count').textContent = String(count); el('record-list').innerHTML = html || '<div class="empty">当前没有配置项，可新建草稿。</div>';
    document.querySelectorAll('.record').forEach(button => { button.onclick = () => selectRecord(button.dataset.kind, Number(button.dataset.index)); });
}

function selectRecord(kind, index) {
    if (currentModule === 'shop') {
        selectedRecord = kind === 'refresh' ? { kind, value: { seconds: currentState.refreshSeconds || 0 } } : { kind, value: (currentState.offers || [])[index] };
        el('operation-select').value = kind === 'refresh' ? 'shop.refreshPeriod' : 'shop.upsert';
    } else if (currentModule === 'tasks') {
        selectedRecord = { kind, value: (currentState.tasks || [])[index] }; el('operation-select').value = 'task.upsert';
    } else if (currentModule === 'tracks') {
        selectedRecord = { kind, value: currentState.tracks || {} }; el('operation-select').value = 'tracks.save';
    } else if (currentModule === 'lottery') {
        const value = kind === 'settings' ? currentState.settings : kind === 'pool' ? (currentState.pools || [])[index] : (currentState.items || [])[index];
        selectedRecord = { kind, value };
        el('operation-select').value = kind === 'settings' ? 'lottery.settings' : kind === 'pool' ? 'lottery.pool.upsert' : 'lottery.shop.upsert';
    }
    document.querySelectorAll('.record').forEach(node => node.classList.toggle('selected', node.dataset.kind === kind && Number(node.dataset.index) === index));
    fillEditorForOperation();
}

function newPayload(commandType) {
    const id = Date.now().toString(36);
    switch (commandType) {
        case 'shop.upsert': return { id: `offer_${id}`, name: '', rewardType: 'item', tpl: '', count: 1, cost: [], stock: -1, buyLimit: 0 };
        case 'shop.delete': return { id: selectedRecord?.value?.id || '' };
        case 'shop.refreshPeriod': return selectedRecord?.kind === 'refresh' ? selectedRecord.value : { seconds: 0 };
        case 'task.upsert': return { id: `task_${id}`, title: '', description: '', conditionType: 'Kills', count: 1, xp: 100 };
        case 'task.delete': return { id: selectedRecord?.value?.id || '' };
        case 'task.genSpec': return { daily: {}, weekly: {}, season: {} };
        case 'tracks.save': return currentState?.tracks || {};
        case 'lottery.settings': return currentState?.settings || {};
        case 'lottery.pool.upsert': return { id: '', name: '', status: 'draft', rewards: [], sortOrder: 0 };
        case 'lottery.pool.delete': return { id: selectedRecord?.value?.id || '' };
        case 'lottery.shop.upsert': return { id: '', name: '', status: 'draft', rewards: [], sortOrder: 0 };
        case 'lottery.shop.delete': return { id: selectedRecord?.value?.id || '' };
        default: return {};
    }
}

function fillEditorForOperation() {
    const commandType = el('operation-select').value;
    const compatible = selectedRecord && ((commandType === 'shop.upsert' && selectedRecord.kind === 'offer') || (commandType === 'task.upsert' && selectedRecord.kind === 'task') ||
        (commandType === 'tracks.save' && selectedRecord.kind === 'tracks') || (commandType === 'lottery.settings' && selectedRecord.kind === 'settings') ||
        (commandType === 'lottery.pool.upsert' && selectedRecord.kind === 'pool') || (commandType === 'lottery.shop.upsert' && selectedRecord.kind === 'shopItem'));
    const payload = compatible ? selectedRecord.value : newPayload(commandType);
    el('payload-editor').value = pretty(payload);
    el('selection-label').textContent = compatible ? `正在编辑：${selectedRecord.value.name || selectedRecord.value.title || selectedRecord.value.id || '当前配置'}` : '新建变更草稿';
}

function startNewDraft() { selectedRecord = null; document.querySelectorAll('.record').forEach(node => node.classList.remove('selected')); fillEditorForOperation(); }

async function submitDraft() {
    const config = MODULES[currentModule];
    if (!config || !hasCapability(config.submitCapability)) return toast('当前模块没有提交权限', false);
    let input;
    try { input = JSON.parse(el('payload-editor').value); } catch (error) { return toast(`JSON 格式错误：${error.message}`, false); }
    const commandType = el('operation-select').value;
    if (!confirm(`确认提交“${el('operation-select').selectedOptions[0].textContent}”审核？\n\n本次操作不会直接生效。`)) return;
    el('submit-btn').disabled = true;
    try {
        const result = await collaboratorFetchJson('/battlepass/api/admin/reviews/submit', { method: 'POST', body: JSON.stringify({ module: currentModule, commandType, input }) });
        toast(`已提交审核：${result.changeId}`, true);
    } catch (error) { toast(error.message, false); }
    finally { el('submit-btn').disabled = false; }
}

async function loadChanges(showView = true) {
    if (showView) {
        currentModule = ''; el('module-view').classList.add('hidden'); el('changes-view').classList.remove('hidden');
        document.querySelectorAll('[data-module]').forEach(node => node.classList.remove('active')); el('changes-nav').classList.add('active');
    }
    el('changes-list').innerHTML = '<div class="empty">正在读取提交记录…</div>';
    try {
        const result = await collaboratorFetchJson('/battlepass/api/admin/reviews?limit=100'); const items = result.items || [];
        el('changes-list').innerHTML = items.map(change => {
            const status = change.status || 'pending'; const time = change.submittedUtc ? new Date(change.submittedUtc * 1000).toLocaleString() : '—';
            return `<article class="change-card"><div class="change-top"><span class="module-pill">${esc(MODULE_NAMES[change.module] || change.module)}</span><span class="status ${esc(status)}">${esc(STATUS_NAMES[status] || status)}</span></div><h3>${esc(change.summary || change.commandType || change.id)}</h3><p>${esc(time)} · ${esc(change.commandType || '')}</p>${change.review?.reason ? `<div class="review-reason">原因：${esc(change.review.reason)}</div>` : ''}${status === 'pending' ? `<button class="text-button withdraw" type="button" data-id="${esc(change.id)}">撤回提交</button>` : ''}</article>`;
        }).join('') || '<div class="empty">还没有提交记录。</div>';
        document.querySelectorAll('.withdraw').forEach(button => { button.onclick = () => withdrawChange(button.dataset.id); });
    } catch (error) { el('changes-list').innerHTML = `<div class="notice error">${esc(error.message)}</div>`; }
}

async function withdrawChange(id) {
    if (!confirm('确认撤回这条待审核变更？')) return;
    try { await collaboratorFetchJson(`/battlepass/api/admin/reviews/${encodeURIComponent(id)}/withdraw`, { method: 'POST' }); toast('已撤回', true); await loadChanges(false); }
    catch (error) { toast(error.message, false); }
}

async function bootstrap() {
    const session = await ensureCollaboratorSession(); if (!session.success) return;
    principal = session.principal; el('actor-name').textContent = principal.displayName || principal.actorId;
    el('session-gate').classList.add('hidden'); el('workspace').classList.remove('hidden'); renderNavigation();
}

el('logout-btn').onclick = logoutCollaborator;
el('changes-nav').onclick = () => loadChanges(true);
el('refresh-changes-btn').onclick = () => loadChanges(false);
el('refresh-btn').onclick = refreshCurrentModule;
el('new-draft-btn').onclick = startNewDraft;
el('operation-select').onchange = fillEditorForOperation;
el('submit-btn').onclick = submitDraft;
el('copy-json-btn').onclick = async () => { await navigator.clipboard.writeText(el('payload-editor').value); toast('JSON 已复制', true); };
bootstrap();
