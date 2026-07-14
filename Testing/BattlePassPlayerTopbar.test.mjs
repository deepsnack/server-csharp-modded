import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const root = new URL(
    '../Libraries/SPTarkov.Server.Assets/SPT_Data/battlepass/page/',
    import.meta.url,
);
const [style, topbarScript, ...pages] = await Promise.all([
    readFile(new URL('style.css', root), 'utf8'),
    readFile(new URL('player-topbar.js', root), 'utf8'),
    ...['index.html', 'quest-skip.html', 'lottery.html', 'titles.html']
        .map(name => readFile(new URL(name, root), 'utf8')),
]);

test('player navigation remains fixed at the viewport top', () => {
    const rule = style.slice(style.indexOf('.player-topbar {'), style.indexOf('.player-nav {'));
    assert.match(rule, /position:\s*fixed/);
    assert.match(rule, /top:\s*0/);
    assert.match(rule, /right:\s*0/);
    assert.match(rule, /left:\s*0/);
    assert.doesNotMatch(rule, /position:\s*sticky/);
});

test('fixed navigation reserves its measured height on every player page', () => {
    assert.match(topbarScript, /ResizeObserver/);
    assert.match(topbarScript, /getBoundingClientRect\(\)\.height/);
    assert.match(topbarScript, /view\.style\.paddingTop/);
    assert.match(topbarScript, /MutationObserver/);

    for (const page of pages) {
        assert.match(page, /style\.css\?v=20260714-fixed-player-topbar-v1/);
        assert.match(page, /player-topbar\.js\?v=20260714-fixed-player-topbar-v1/);
    }
});
