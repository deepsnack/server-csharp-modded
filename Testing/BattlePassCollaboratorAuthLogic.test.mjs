import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

// 无需运行服务器 / 凭据：在 vm 沙箱里加载共享鉴权模块 auth.js（浏览器全局函数声明），
// stub 浏览器环境后真实调用其函数，守护协管鉴权关键不变量（尤以「铁律」为重）。
const HERE = dirname(fileURLToPath(import.meta.url));
const AUTH_PATH = resolve(HERE, '../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/auth.js');
const AUTH = readFileSync(AUTH_PATH, 'utf8');

function makeSandbox({ meResponse } = {}) {
    const store = new Map();
    const gates = [];
    const doc = {
        _gate: null,
        querySelectorAll: () => ({ forEach: () => {} }),
        getElementById: (id) => {
            if (id === 'collab-gate') return doc._gate;
            if (id === 'collab-gate-message') return doc._gate ? doc._gate._msg : null;
            return null;
        },
        createElement: () => {
            const e = { id: '', className: '', _html: '', classList: { add() {}, remove() {} } };
            Object.defineProperty(e, 'innerHTML', { get() { return e._html; }, set(v) { e._html = v; } });
            return e;
        },
        body: { appendChild: (e) => { doc._gate = e; doc._gate._msg = { textContent: '' }; gates.push(e); } },
    };
    const sandbox = {
        sessionStorage: {
            getItem: (k) => (store.has(k) ? store.get(k) : null),
            setItem: (k, v) => store.set(k, String(v)),
            removeItem: (k) => store.delete(k),
        },
        location: { hash: '', pathname: '/battlepass/admin/index.html', search: '' },
        history: { replaceState() {} },
        document: doc,
        URLSearchParams,
        fetch: async () => ({ ok: meResponse.ok, json: async () => meResponse.body }),
        console,
        _store: store, _gates: gates,
    };
    vm.createContext(sandbox);
    vm.runInContext(AUTH, sandbox);
    return sandbox;
}

test('铁律：被撤销的协管会话失效后走无密码 gate，绝不回落管理员密码框', async () => {
    const sb = makeSandbox({ meResponse: { ok: true, body: { success: false, message: '协管授权已被撤销' } } });
    sb.sessionStorage.setItem('bp_admin_token', 'stale-collab-token');
    sb.sessionStorage.setItem('bp_actor_type', 'collaborator');

    const ok = await vm.runInContext('ensureAdminSession()', sb);
    assert.equal(ok, false, 'ensureAdminSession 应因会话失效返回 false');

    const handled = vm.runInContext('handleSessionFailure()', sb);
    assert.equal(handled, true, '协管失效必须由 handleSessionFailure 接管（否则页面会显示管理员密码框）');

    assert.equal(sb.sessionStorage.getItem('bp_actor_type'), 'collaborator', 'actorType 应保留为 collaborator 以便 gate 判定');
    assert.equal(sb.sessionStorage.getItem('bp_admin_token'), null, '失效 token 应被清除');
    assert.equal(sb._gates.length, 1, '应创建一个无密码失效 gate');
    assert.match(sb._gates[0]._html, /不接受管理员密码/);
    assert.doesNotMatch(sb._gates[0]._html, /type=["']password["']/i);
});

test('管理员（无历史协管标记）会话为空时应回落密码框，不误弹 gate', async () => {
    const sb = makeSandbox({ meResponse: { ok: true, body: { success: true, actorType: 'admin' } } });
    const ok = await vm.runInContext('ensureAdminSession()', sb);
    assert.equal(ok, false);
    const handled = vm.runInContext('handleSessionFailure()', sb);
    assert.equal(handled, false, '非协管失效应返回 false，交页面显示管理员密码框');
    assert.equal(sb._gates.length, 0, '不应为管理员创建协管 gate');
});

test('有效协管会话：#bpsso 落地 + /admin/me 通过 → 落地成功并按能力门控', async () => {
    const sb = makeSandbox({ meResponse: { ok: true, body: { success: true, actorType: 'collaborator', capabilities: ['shop.read', 'shop.submit', 'tasks.read'] } } });
    sb.location.hash = '#bpsso=fresh-collab-token';
    const ok = await vm.runInContext('ensureAdminSession()', sb);
    assert.equal(ok, true, '有效协管会话应落地成功');
    assert.equal(sb.sessionStorage.getItem('bp_actor_type'), 'collaborator');
    assert.equal(sb.sessionStorage.getItem('bp_admin_token'), 'fresh-collab-token');
    assert.equal(vm.runInContext("hasCapability('shop.read')", sb), true, '应有 shop.read');
    assert.equal(vm.runInContext("hasCapability('lottery.read')", sb), false, '不应有 lottery.read');
});

test('管理员身份 hasCapability 恒为 true（不受能力清单约束）', async () => {
    const sb = makeSandbox({ meResponse: { ok: true, body: { success: true, actorType: 'admin' } } });
    sb.sessionStorage.setItem('bp_actor_type', 'admin');
    assert.equal(vm.runInContext("hasCapability('lottery.read')", sb), true);
    assert.equal(vm.runInContext("hasCapability('anything.at.all')", sb), true);
});
