import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const adminRoot = new URL(
    '../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/',
    import.meta.url,
);
const [quests, picker, questController, queryController] = await Promise.all([
    readFile(new URL('quests.js', adminRoot), 'utf8'),
    readFile(new URL('picker.js', adminRoot), 'utf8'),
    readFile(new URL(
        '../Libraries/SPTarkov.Server.Core/BattlePass/Controllers/BattlePassQuestAdminController.cs',
        import.meta.url,
    ), 'utf8'),
    readFile(new URL(
        '../Libraries/SPTarkov.Server.Core/BattlePass/Controllers/BattlePassQueryController.cs',
        import.meta.url,
    ), 'utf8'),
]);

test('quest editor exposes vanilla advanced objective controls', () => {
    assert.match(quests, /value="weaponAssembly">上交指定改装枪械/);
    assert.match(quests, /value="transit">地图转移/);
    assert.match(quests, /QUEST_CATALOG\.killTargets\.filter/);
    assert.match(quests, /指定 Scav\/Boss 角色（可多选）/);
    assert.match(quests, /指定地图（可多选）/);
    assert.match(quests, /必须安装的配件/);
    assert.match(quests, /击杀距离/);
    assert.match(quests, /游戏内时段/);
    assert.match(quests, /一命\/同一战局完成/);
    assert.match(quests, /dependsOnPrevious/);
    assert.match(questController, /\[HttpGet\("catalog"\)\]/);
});

test('started rewards and vanilla unlock rewards share searchable editors', () => {
    assert.match(quests, /startedRewards:\s*\[\]/);
    assert.match(quests, /接取任务发放（Started）/);
    assert.match(quests, /value="assortmentUnlock">商人直购权/);
    assert.match(quests, /value="productionScheme">藏身处配方/);
    assert.match(quests, /api\(`\/assorts\?traderId=/);
    assert.match(quests, /questUnlockOnly:\s*true/);
    assert.match(picker, /questUnlockOnly=true/);
    assert.match(queryController, /questUnlockOnly/);
});

test('custom and collaborator edit paths retain the same normalized payload', () => {
    assert.match(quests, /applyPendingQuestEdit/);
    assert.match(quests, /\[\.\.\.d\.startedRewards, \.\.\.d\.rewards\]/);
    assert.match(quests, /targets:\s*\[\], locations:\s*\[\], weapons:\s*\[\], weaponMods:\s*\[\]/);
    assert.doesNotMatch(quests, /目标\(如 Savage\/Any\)/);
});

test('every custom objective exposes and preserves localized display text', () => {
    assert.match(quests, /textZh:\s*null,\s*textEn:\s*null/);
    assert.match(quests, /中文目标文本/);
    assert.match(quests, /英文目标文本（可选）/);
    assert.match(quests, /textZh:\s*o\.textZh\s*\|\|\s*null/);
    assert.match(quests, /textEn:\s*o\.textEn\s*\|\|\s*null/);
    assert.match(quests, /留空也不会向客户端显示 tpl/);
});
