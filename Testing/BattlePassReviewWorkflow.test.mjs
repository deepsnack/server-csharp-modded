import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import vm from 'node:vm';

const root = new URL('../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/', import.meta.url);
const [authSource, pickerSource, reviewsSource, reviewsHtml, reviewsCss, myChangesSource, queryController] = await Promise.all([
    readFile(new URL('auth.js', root), 'utf8'),
    readFile(new URL('picker.js', root), 'utf8'),
    readFile(new URL('reviews.js', root), 'utf8'),
    readFile(new URL('reviews.html', root), 'utf8'),
    readFile(new URL('style.css', root), 'utf8'),
    readFile(new URL('my-changes.js', root), 'utf8'),
    readFile(new URL(
        '../Libraries/SPTarkov.Server.Core/BattlePass/Controllers/BattlePassQueryController.cs',
        import.meta.url,
    ), 'utf8'),
]);

function response(status, body) {
    return { status, ok: status >= 200 && status < 300, async json() { return body; } };
}

test('pending collaborator edit updates the same review id with optimistic version', async () => {
    const calls = [];
    const storage = new Map([
        ['bp_admin_token', 'collaborator-token'],
        ['bp_actor_type', 'collaborator'],
    ]);
    const context = {
        URLSearchParams,
        console,
        location: { hash: '', pathname: '/battlepass/admin/items.html', search: '' },
        history: { replaceState() {} },
        sessionStorage: {
            getItem: key => storage.get(key) || null,
            setItem: (key, value) => storage.set(key, String(value)),
            removeItem: key => storage.delete(key),
        },
        fetch: async (url, options) => {
            calls.push({ url, options });
            return response(200, { success: true, changeId: 'change-1', version: 5 });
        },
    };
    vm.createContext(context);
    vm.runInContext(authSource, context, { filename: 'admin/auth.js' });
    vm.runInContext("BP_PENDING_CHANGE_EDIT = { id: 'change-1', module: 'items', commandType: 'item.override.delete', version: 4 }", context);

    const result = await context.submitChange('items', 'item.override.delete', { id: 'override-2' });

    assert.equal(result.success, true);
    assert.equal(result.editedExisting, true);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/battlepass/api/admin/reviews/change-1/edit');
    assert.deepEqual(JSON.parse(calls[0].options.body), {
        input: { id: 'override-2' },
        expectedVersion: 4,
    });
    assert.equal(vm.runInContext('BP_PENDING_CHANGE_EDIT.version', context), 5);
});

test('review page supports sticky detail, mobile inline detail, and cross-module versioned batches', () => {
    assert.match(reviewsHtml, /class="review-workspace"/);
    assert.match(reviewsHtml, /id="review-detail-host"/);
    assert.match(reviewsCss, /review-detail-host[\s\S]*position:\s*sticky/);
    assert.match(reviewsSource, /mobileReviewLayout\.matches\s*&&\s*card/);
    assert.match(reviewsSource, /card\.after\(detail\)/);
    assert.match(reviewsSource, /JSON\.stringify\(\{\s*items\s*\}\)/);
    assert.match(reviewsSource, /expectedVersion:\s*item\.version/);
    assert.doesNotMatch(reviewsSource, /JSON\.stringify\(\{\s*module:\s*currentModule,\s*ids/);
    assert.match(reviewsSource, /selectAll\.indeterminate/);
    assert.match(reviewsSource, /条失败/);
});

test('my submissions exposes edit only for pending changes and routes to the source module form', () => {
    assert.match(myChangesSource, /c\.status === 'pending'/);
    assert.match(myChangesSource, /编辑/);
    assert.match(myChangesSource, /editChange=/);
    assert.match(myChangesSource, /items:\s*'items\.html'/);
    assert.match(myChangesSource, /撤回/);
});

test('shared item picker surfaces authorization errors and accepts one Chinese character', async () => {
    const calls = [];
    const context = {
        window: {},
        console,
        sessionStorage: { getItem: () => 'collaborator-token' },
        fetch: async (url, options) => {
            calls.push({ url, options });
            return response(401, { success: false, message: '未授权' });
        },
    };
    vm.createContext(context);
    vm.runInContext(pickerSource, context, { filename: 'admin/picker.js' });

    await assert.rejects(() => context.window.BpPicker.queryItems('盐'), /未授权/);
    assert.match(calls[0].url, /q=%E7%9B%90/);
    assert.equal(calls[0].options.headers['X-Admin-Token'], 'collaborator-token');
    assert.match(pickerSource, /requiredChars\s*=\s*\/\[\\u3400-\\u9fff/);
    assert.doesNotMatch(pickerSource, /catch\s*\([^)]*\)\s*\{\s*return\s*\[\]/);
});

test('unified query authorizes a valid collaborator session without items.read', () => {
    assert.match(queryController, /sessionService\.ValidateToken\(token\) is not null/);
    assert.doesNotMatch(queryController, /items\.read/);
});
