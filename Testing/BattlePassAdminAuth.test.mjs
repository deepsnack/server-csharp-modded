import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const authSource = await readFile(new URL(
    '../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/auth.js',
    import.meta.url,
), 'utf8');
const indexSource = await readFile(new URL(
    '../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/index.html',
    import.meta.url,
), 'utf8');

function response(status, body) {
    return {
        status,
        ok: status >= 200 && status < 300,
        async json() { return body; },
    };
}

function createBrowser(hash, fetchImpl, initialStorage = {}) {
    const storage = new Map(Object.entries(initialStorage));
    const location = {
        hash,
        pathname: '/battlepass/admin/index.html',
        search: '',
    };
    const context = {
        URLSearchParams,
        Object,
        String,
        console,
        fetch: fetchImpl,
        location,
        history: {
            replaceState(_state, _unused, url) {
                const hashIndex = url.indexOf('#');
                location.hash = hashIndex >= 0 ? url.slice(hashIndex) : '';
            },
        },
        sessionStorage: {
            getItem(key) { return storage.has(key) ? storage.get(key) : null; },
            setItem(key, value) { storage.set(key, String(value)); },
            removeItem(key) { storage.delete(key); },
        },
    };

    vm.createContext(context);
    vm.runInContext(authSource, context, { filename: 'admin/auth.js' });
    return { context, location, storage };
}

test('Portal admin fragment is stored directly without an exchange request', async () => {
    let called = false;
    const browser = createBrowser('#sso=portal-token', async () => {
        called = true;
        return response(500, {});
    });

    assert.equal(await browser.context.ensureAdminSession(), true);
    assert.equal(called, false);
    assert.equal(browser.storage.get('bp_admin_token'), 'portal-token');
    assert.equal(browser.storage.get('bp_actor_type'), 'admin');
    assert.equal(browser.location.hash, '');
});

test('unified console accepts and validates collaborator fragments', async () => {
    let called = false;
    const browser = createBrowser('#bpsso=player-token', async (url, options) => {
        called = true;
        assert.equal(url, '/battlepass/api/admin/me');
        assert.equal(options.headers['X-BP-Admin-Token'], 'player-token');
        return response(200, { success: true, actorType: 'collaborator', capabilities: ['items.read'] });
    });

    assert.equal(await browser.context.ensureAdminSession(), true);
    assert.equal(called, true);
    assert.equal(browser.storage.get('bp_admin_token'), 'player-token');
    assert.equal(browser.storage.get('bp_actor_type'), 'collaborator');
    assert.equal(browser.location.hash, '');
});

test('stored administrator token is accepted without requiring a new backend route', async () => {
    let called = false;
    const browser = createBrowser('', async () => {
        called = true;
        return response(500, {});
    }, { bp_admin_token: 'stored-session' });

    assert.equal(await browser.context.ensureAdminSession(), true);
    assert.equal(called, false);
    assert.equal(browser.storage.get('bp_actor_type'), 'admin');
});

test('unified console revalidates a stored collaborator principal', async () => {
    const browser = createBrowser('', async () => response(200, {
        success: true, actorType: 'collaborator', capabilities: ['trader.read'],
    }), {
        bp_admin_token: 'collaborator-session',
        bp_actor_type: 'collaborator',
    });

    assert.equal(await browser.context.ensureAdminSession(), true);
    assert.equal(browser.storage.get('bp_admin_token'), 'collaborator-session');
    assert.equal(browser.storage.get('bp_actor_type'), 'collaborator');
});

test('password login stores the WebRegister administrator token directly', async () => {
    const calls = [];
    const browser = createBrowser('', async (url, options) => {
        calls.push({ url, options });
        return response(200, { success: true, token: 'login-token' });
    });

    const result = await browser.context.adminLogin('secret');
    assert.equal(result.success, true);
    assert.equal(result.actorType, 'admin');
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/register/api/admin/login');
    assert.equal(browser.storage.get('bp_admin_token'), 'login-token');
    assert.equal(browser.storage.get('bp_actor_type'), 'admin');
});

test('admin index captures Portal SSO before external scripts and cache-busts auth assets', () => {
    const bootstrap = indexSource.indexOf('capturePortalAdminToken');
    const authScript = indexSource.indexOf('auth.js?v=20260707-pending-edit-v1');
    const mainScript = indexSource.search(/script\.js\?v=/);
    const inlineSource = indexSource.match(/<script>\s*([\s\S]*?)<\/script>/)?.[1];

    assert.ok(bootstrap >= 0);
    assert.ok(authScript > bootstrap);
    assert.ok(mainScript > authScript);
    assert.match(indexSource, /sessionStorage\.setItem\('bp_admin_token', token\)/);
    assert.ok(inlineSource);

    const storage = new Map();
    const location = {
        hash: '#sso=early-token&tab=tracks',
        pathname: '/battlepass/admin/index.html',
        search: '?from=portal',
    };
    vm.runInNewContext(inlineSource, {
        URLSearchParams,
        location,
        sessionStorage: {
            setItem(key, value) { storage.set(key, String(value)); },
        },
        history: {
            replaceState(_state, _unused, url) {
                const hashIndex = url.indexOf('#');
                location.hash = hashIndex >= 0 ? url.slice(hashIndex) : '';
            },
        },
    }, { filename: 'admin/index.inline.js' });

    assert.equal(storage.get('bp_admin_token'), 'early-token');
    assert.equal(storage.get('bp_actor_type'), 'admin');
    assert.equal(location.hash, '#tab=tracks');
});
