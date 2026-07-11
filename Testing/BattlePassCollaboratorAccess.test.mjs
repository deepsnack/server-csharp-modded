import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const root = new URL('../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/', import.meta.url);
const accessSource = await readFile(new URL('admin/access.js', root), 'utf8');
const accessHtml = await readFile(new URL('admin/access.html', root), 'utf8');
const playerHtml = await readFile(new URL('index.html', root), 'utf8');
const collaboratorHtml = await readFile(new URL('collab/index.html', root), 'utf8');
const accessController = await readFile(new URL(
    '../Libraries/SPTarkov.Server.Core/BattlePass/Administration/BattlePassAccessController.cs',
    import.meta.url,
), 'utf8');

test('revoke and permanent delete are separate administrator actions', () => {
    assert.match(accessSource, /撤销权限/);
    assert.match(accessSource, /async function toggleGrant/);
    assert.match(accessSource, /method: 'PATCH'/);
    assert.match(accessSource, /async function deleteGrant/);
    assert.match(accessSource, /method: 'DELETE'/);
    assert.doesNotMatch(accessSource, /async function revokeGrant/);
    assert.match(accessSource, /cache: 'no-store'/);
});

test('server DELETE removes records instead of retaining a disabled grant', () => {
    const deleteMethod = accessController.match(/public object DeleteGrant[\s\S]*?\n    \}/u)?.[0] || '';
    assert.match(deleteMethod, /BattlePassCollaboratorGrantPolicy\.RemoveAll/);
    assert.match(deleteMethod, /changeStore\.SaveGrants\(grants\)/);
    assert.doesNotMatch(deleteMethod, /grant\.Enabled\s*=\s*false/);
});

test('collaborator entry assets are cache-busted and the retired workspace redirects safely', () => {
    assert.match(playerHtml, /script\.js\?v=20260706-collab-reuse-v4/);
    assert.match(accessHtml, /auth\.js\?v=20260707-pending-edit-v1/);
    assert.match(accessHtml, /access\.js\?v=20260706-collab-access-v3/);
    assert.match(collaboratorHtml, /location\.replace\('\/battlepass\/index\.html'\)/);
    assert.doesNotMatch(collaboratorHtml, /type=["']password["']/i);
});
