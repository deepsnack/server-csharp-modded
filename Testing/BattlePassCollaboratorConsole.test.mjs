import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const root = new URL('../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/', import.meta.url);
const [playerSource, legacyHtml, adminAuth, adminScript, myChanges, reviews] = await Promise.all([
    readFile(new URL('script.js', root), 'utf8'),
    readFile(new URL('collab/index.html', root), 'utf8'),
    readFile(new URL('admin/auth.js', root), 'utf8'),
    readFile(new URL('admin/script.js', root), 'utf8'),
    readFile(new URL('admin/my-changes.js', root), 'utf8'),
    readFile(new URL('admin/reviews.js', root), 'utf8'),
]);

test('retired collaborator workspace redirects to the player entry without a password form', () => {
    assert.match(legacyHtml, /\/battlepass\/index\.html/);
    assert.match(legacyHtml, /协管工作台已并入通行证管理后台/);
    assert.doesNotMatch(legacyHtml, /type=["']password["']/i);
});

test('player entry exchanges the player session and enters the shared administrator UI', () => {
    assert.match(playerSource, /\/battlepass\/api\/admin\/session\/exchange/);
    assert.match(playerSource, /X-BP-Token/);
    assert.match(playerSource, /bp_admin_token/);
    assert.match(playerSource, /bp_actor_type', 'collaborator/);
    assert.match(playerSource, /\/battlepass\/admin\/index\.html/);
    assert.doesNotMatch(playerSource, /location\.href = '\/battlepass\/collab\/index\.html'/);
});

test('shared administrator auth routes collaborator writes through the review queue', () => {
    assert.match(adminAuth, /async function submitChange/);
    assert.match(adminAuth, /\/battlepass\/api\/admin\/reviews\/submit/);
    assert.match(adminAuth, /BP_PENDING_CHANGE_EDIT/);
    assert.match(adminAuth, /expectedVersion/);
    assert.match(adminAuth, /showCollaboratorGate/);
});

test('collaborator can edit or withdraw only pending own submissions', () => {
    assert.match(myChanges, /c\.status === 'pending'/);
    assert.match(myChanges, />编辑</);
    assert.match(myChanges, />撤回</);
    assert.match(myChanges, /editChange=/);
});

test('administrator review batches send cross-module items with optimistic versions', () => {
    assert.match(reviews, /map\(item => \(\{ id: item\.id, expectedVersion:/);
    assert.match(reviews, /JSON\.stringify\(\{ items \}\)/);
    assert.match(reviews, /batchSummary/);
});

test('shared collaborator console exposes trader-quest management when authorized', () => {
    assert.match(adminScript, /\['quests\.html', 'quests\.read'\]/);
    assert.match(myChanges, /quests: '商人任务'/);
    assert.match(myChanges, /quests: 'quests\.html'/);
    assert.match(reviews, /quests: '商人任务'/);
});
