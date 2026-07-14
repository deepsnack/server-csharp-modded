import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const read = path => fs.readFileSync(new URL('../' + path, import.meta.url), 'utf8');
const script = read('Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/script.js');
const style = read('Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/style.css');
const html = read('Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/admin/index.html');

test('reward track levels render as native details and are closed by default', () => {
    const template = script.slice(script.indexOf('function levelBlockHtml'), script.indexOf('function renderLevelBlock'));
    assert.match(template, /<details class="level-block/);
    assert.match(template, /<summary class="level-head" aria-expanded="false">/);
    assert.doesNotMatch(template, /<details[^>]*\sopen(?:\s|>)/);
    assert.match(template, /level-reward-summary/);
});

test('each level toggles independently without changing track save semantics', () => {
    assert.match(script, /block\.addEventListener\('toggle'/);
    assert.match(script, /summary\?\.setAttribute\('aria-expanded'/);
    assert.match(script, /collectAllFromDom\(\)/);
    assert.match(script, /submitChange\('tracks', 'tracks\.save', diff\)/);
    assert.match(script, /event\.preventDefault\(\)/);
    assert.match(script, /event\.stopPropagation\(\)/);
});

test('collapsed summaries show reward counts and use cache-busted assets', () => {
    assert.match(script, /updateLevelSummary/);
    assert.match(style, /\.level-block\[open\] \.level-head/);
    assert.match(style, /\.level-block\[open\] \.level-chevron/);
    assert.match(html, /style\.css\?v=20260708-track-collapse-v1/);
    assert.match(html, /script\.js\?v=20260713-task-hub-v1/);
});
