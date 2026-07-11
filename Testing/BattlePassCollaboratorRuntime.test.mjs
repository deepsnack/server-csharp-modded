import assert from 'node:assert/strict';
import https from 'node:https';
import test from 'node:test';

const baseUrl = new URL(process.env.BP_TEST_BASE_URL || 'https://127.0.0.1:6969');
const adminPassword = process.env.BP_TEST_ADMIN_PASSWORD || '';
const playerUsername = process.env.BP_TEST_PLAYER_USERNAME || '';
const playerPassword = process.env.BP_TEST_PLAYER_PASSWORD || '';
const profileId = process.env.BP_TEST_PROFILE_ID || '';

for (const [name, value] of Object.entries({ adminPassword, playerUsername, playerPassword, profileId })) {
    if (!value) throw new Error(`Missing required runtime test setting: ${name}`);
}

function request(path, { method = 'GET', headers = {}, body } = {}) {
    return new Promise((resolve, reject) => {
        const payload = body === undefined ? undefined : JSON.stringify(body);
        const req = https.request(new URL(path, baseUrl), {
            method,
            rejectUnauthorized: false,
            headers: {
                ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}),
                ...headers,
            },
        }, res => {
            const chunks = [];
            res.on('data', chunk => chunks.push(chunk));
            res.on('end', () => resolve({
                status: res.statusCode || 0,
                text: Buffer.concat(chunks).toString('utf8'),
            }));
        });
        req.on('error', reject);
        if (payload) req.write(payload);
        req.end();
    });
}

async function json(path, options) {
    const response = await request(path, options);
    assert.ok(response.status >= 200 && response.status < 300, `${path} returned HTTP ${response.status}`);
    try {
        return JSON.parse(response.text);
    } catch {
        assert.fail(`${path} did not return JSON`);
    }
}

function adminHeaders(token) {
    return { 'X-Admin-Token': token, 'X-BP-Admin-Token': token };
}

test('deployed collaborator authorization, authentication, revoke, restore and delete flow', async t => {
    let adminToken = '';
    let playerToken = '';
    let firstCollaboratorToken = '';
    let restoredCollaboratorToken = '';

    await t.test('real administrator and player logins succeed', async () => {
        const admin = await json('/register/api/admin/login', {
            method: 'POST',
            body: { password: adminPassword },
        });
        assert.equal(admin.success, true, admin.message);
        assert.ok(admin.token);
        adminToken = admin.token;

        const player = await json('/battlepass/api/login', {
            method: 'POST',
            body: { username: playerUsername, password: playerPassword },
        });
        assert.equal(player.success, true, player.message);
        assert.ok(player.token);
        playerToken = player.token;
    });

    await t.test('test profile starts without a collaborator grant', async () => {
        const grants = await json('/battlepass/api/admin/access/grants', { headers: adminHeaders(adminToken) });
        assert.equal(grants.success, true, grants.message);
        const existing = (grants.grants || []).find(g => String(g.profileId).toLowerCase() === profileId.toLowerCase());
        if (existing) {
            const cleanup = await json(`/battlepass/api/admin/access/grants/${encodeURIComponent(profileId)}`, {
                method: 'DELETE',
                headers: adminHeaders(adminToken),
            });
            assert.equal(cleanup.success, true, cleanup.message);
        }

        const state = await json('/battlepass/api/state', { headers: { 'X-BP-Token': playerToken } });
        assert.equal(state.success, true, state.message);
        assert.equal(state.management, null);
    });

    await t.test('administrator grant immediately enables the player entry and session exchange', async () => {
        const created = await json('/battlepass/api/admin/access/grants', {
            method: 'POST',
            headers: adminHeaders(adminToken),
            body: { profileId },
        });
        assert.equal(created.success, true, created.message);
        assert.equal(created.grant.profileId.toLowerCase(), profileId.toLowerCase());
        assert.equal(created.grant.enabled, true);

        const state = await json('/battlepass/api/state', { headers: { 'X-BP-Token': playerToken } });
        assert.equal(state.success, true, state.message);
        assert.equal(state.management?.canEnterAdmin, true);
        assert.ok(state.management.capabilities.includes('shop.read'));

        const exchanged = await json('/battlepass/api/admin/session/exchange', {
            method: 'POST',
            headers: { 'X-BP-Token': playerToken },
        });
        assert.equal(exchanged.success, true, exchanged.message);
        assert.equal(exchanged.actorType, 'collaborator');
        assert.ok(exchanged.token);
        firstCollaboratorToken = exchanged.token;

        const me = await json('/battlepass/api/admin/me', {
            headers: { 'X-BP-Admin-Token': firstCollaboratorToken },
        });
        assert.equal(me.success, true, me.message);
        assert.equal(me.actorType, 'collaborator');
        assert.equal(me.actorId.toLowerCase(), profileId.toLowerCase());
        assert.ok(me.capabilities.includes('shop.read'));

        const shop = await json('/battlepass/api/admin/shop', {
            // 业务管理控制器沿用现有 X-Admin-Token；独立协管页 collaboratorFetchJson 也使用该头。
            headers: { 'X-Admin-Token': firstCollaboratorToken },
        });
        assert.equal(shop.success, true, shop.message);
    });

    await t.test('deployed admin pages accept collaborators and never expose a password box on failure', async () => {
        // 协管现复用管理后台页面：auth.js 需带新版本、接受 #bpsso 免密落地、提供无密码失效 gate。
        const authScript = await request('/battlepass/admin/auth.js?v=20260706-collab-reuse-v4');
        assert.equal(authScript.status, 200);
        assert.match(authScript.text, /showCollaboratorGate/);
        assert.match(authScript.text, /bootstrapAdminPage/);
        assert.match(authScript.text, /validateCollaboratorSession/);

        // 主后台页面：引用新版本 auth.js，失效时走协管 gate（handleSessionFailure），而非直接暴露密码框。
        const adminPage = await request('/battlepass/admin/index.html');
        assert.equal(adminPage.status, 200);
        assert.match(adminPage.text, /auth\.js\?v=20260706-collab-reuse-v4/);
        const adminScript = await request('/battlepass/admin/script.js?v=20260706-collab-reuse-v4');
        assert.equal(adminScript.status, 200);
        assert.match(adminScript.text, /handleSessionFailure/);

        // 玩家页协管入口跳转到管理后台（不再是独立 collab 工作台）。
        const playerPage = await request('/battlepass/script.js?v=20260706-collab-reuse-v4');
        assert.equal(playerPage.status, 200);
        assert.match(playerPage.text, /\/battlepass\/admin\/index\.html/);

        // 旧独立工作台已退役：collab/index.html 变为重定向兜底。
        const legacy = await request('/battlepass/collab/index.html');
        assert.equal(legacy.status, 200);
        assert.match(legacy.text, /\/battlepass\/index\.html/);
        assert.doesNotMatch(legacy.text, /type=["']password["']/i);

        const accessPage = await request('/battlepass/admin/access.html');
        assert.equal(accessPage.status, 200);
        assert.match(accessPage.text, /永久移除记录/);
    });

    await t.test('revoking permission immediately invalidates entry and existing session', async () => {
        const revoked = await json(`/battlepass/api/admin/access/grants/${encodeURIComponent(profileId)}`, {
            method: 'PATCH',
            headers: adminHeaders(adminToken),
            body: { enabled: false },
        });
        assert.equal(revoked.success, true, revoked.message);
        assert.equal(revoked.grant.enabled, false);

        const me = await json('/battlepass/api/admin/me', {
            headers: { 'X-BP-Admin-Token': firstCollaboratorToken },
        });
        assert.equal(me.success, false);

        const state = await json('/battlepass/api/state', { headers: { 'X-BP-Token': playerToken } });
        assert.equal(state.management, null);

        const exchange = await json('/battlepass/api/admin/session/exchange', {
            method: 'POST',
            headers: { 'X-BP-Token': playerToken },
        });
        assert.equal(exchange.success, false);
    });

    await t.test('restoring permission allows a fresh authenticated collaborator session', async () => {
        const restored = await json(`/battlepass/api/admin/access/grants/${encodeURIComponent(profileId)}`, {
            method: 'PATCH',
            headers: adminHeaders(adminToken),
            body: { enabled: true },
        });
        assert.equal(restored.success, true, restored.message);
        assert.equal(restored.grant.enabled, true);

        const exchange = await json('/battlepass/api/admin/session/exchange', {
            method: 'POST',
            headers: { 'X-BP-Token': playerToken },
        });
        assert.equal(exchange.success, true, exchange.message);
        assert.equal(exchange.actorType, 'collaborator');
        restoredCollaboratorToken = exchange.token;

        const me = await json('/battlepass/api/admin/me', {
            headers: { 'X-BP-Admin-Token': restoredCollaboratorToken },
        });
        assert.equal(me.success, true, me.message);
        assert.equal(me.actorType, 'collaborator');
    });

    await t.test('permanent delete removes the row and invalidates the restored session', async () => {
        const deleted = await json(`/battlepass/api/admin/access/grants/${encodeURIComponent(profileId)}`, {
            method: 'DELETE',
            headers: adminHeaders(adminToken),
        });
        assert.equal(deleted.success, true, deleted.message);
        assert.ok(deleted.deleted >= 1);

        const grants = await json('/battlepass/api/admin/access/grants', { headers: adminHeaders(adminToken) });
        assert.equal(grants.success, true, grants.message);
        assert.equal((grants.grants || []).some(g => String(g.profileId).toLowerCase() === profileId.toLowerCase()), false);

        const me = await json('/battlepass/api/admin/me', {
            headers: { 'X-BP-Admin-Token': restoredCollaboratorToken },
        });
        assert.equal(me.success, false);

        const state = await json('/battlepass/api/state', { headers: { 'X-BP-Token': playerToken } });
        assert.equal(state.management, null);

        const exchange = await json('/battlepass/api/admin/session/exchange', {
            method: 'POST',
            headers: { 'X-BP-Token': playerToken },
        });
        assert.equal(exchange.success, false);
    });
});
