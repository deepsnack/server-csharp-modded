'use strict';
/*
 * 通行证统一奖励编辑组件（约定组件）
 * ------------------------------------------------------------------
 * 奖励卡的渲染 / 收集 / 类型切换 / 奖池下拉 / BpPicker 选择器，全部收敛到这里。
 * 奖励轨(script.js)、任务(tasks.js)、激活码、批量增删统一引用，避免各写一套、键名分歧。
 * 依赖 picker.js 的全局 BpPicker，须在其之后加载。
 *
 * 用法：
 *   RewardEditor.fill(containerEl, rewards, pools);      // 渲染并绑定一组奖励卡（container 直接持有 .rw-card）
 *   RewardEditor.addCard(containerEl, reward, pools);    // 追加一张卡
 *   const { rewards, invalid } = RewardEditor.collect(containerEl, { dropInvalid });
 *   RewardEditor.poolOptions(pools, selected);           // 奖池下拉（仅抽奖券奖池）
 *
 * 奖励对象形状（= 后端 BpReward）：
 *   { type, tpl, count, name, featured, foundInRaid, offerId, recipeId, titleId, suitId, poolId }
 */
(function (global) {
    const ICON_API = '/battlepass/api/icons/';
    const TOKEN_TYPES = ['lotteryGlobalTickets', 'lotteryPoolTickets', 'lotteryExchangeCoins'];
    const REF_ROWS = ['.rw-offerid-row', '.rw-recipeid-row', '.rw-titleid-row', '.rw-suitid-row', '.rw-token-row', '.rw-poolid-row'];
    const REF_ROW_BY_TYPE = { purchaseRight: '.rw-offerid-row', recipe: '.rw-recipeid-row', title: '.rw-titleid-row', clothing: '.rw-suitid-row' };

    function esc(s) { return (s || '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
    function isTpl(s) { return /^[a-fA-F0-9]{24}$/.test((s || '').trim()); }

    // 奖池下拉：仅显示抽奖券奖池(costType==='lotteryTickets')；保留已选但不在列表的旧值。
    function poolOptions(pools, selected) {
        selected = selected || '';
        const sorted = (pools || []).filter(p => p.costType === 'lotteryTickets').sort((a, b) => (a.sortOrder || 0) - (b.sortOrder || 0));
        const hasSelected = selected && sorted.some(p => p.id === selected);
        const options = sorted.map(p => `<option value="${esc(p.id)}">${esc(p.name || p.id)} (${esc(p.id)})</option>`).join('');
        const legacy = selected && !hasSelected ? `<option value="${esc(selected)}">${esc(selected)}（当前奖池列表未找到）</option>` : '';
        return `<option value="">请选择奖池</option>${legacy}${options}`;
    }

    function cardHtml(r, pools) {
        r = r || {};
        const type = r.type || 'item';
        const iconUrl = isTpl(r.tpl) ? ICON_API + r.tpl : '';
        const show = t => type === t ? '' : 'display:none';
        const itemStyle = show('item');
        const extraStyle = type !== 'item' ? '' : 'display:none';
        const tokenStyle = TOKEN_TYPES.includes(type) ? '' : 'display:none';
        return `<div class="rw-card">
        <div class="rw-card-main">
            <div class="rw-icon-wrap"><img src="${iconUrl}" class="rw-icon" style="${iconUrl ? '' : 'display:none'}" alt="" onerror="this.style.display='none'" /></div>
            <div class="rw-fields">
                <div class="rw-row">
                    <select class="rw-type" style="width:90px">
                        <option value="item" ${type === 'item' ? 'selected' : ''}>物品</option>
                        <option value="purchaseRight" ${type === 'purchaseRight' ? 'selected' : ''}>购买权</option>
                        <option value="recipe" ${type === 'recipe' ? 'selected' : ''}>配方</option>
                        <option value="title" ${type === 'title' ? 'selected' : ''}>称号</option>
                        <option value="clothing" ${type === 'clothing' ? 'selected' : ''}>服装</option>
                        <option value="lotteryGlobalTickets" ${type === 'lotteryGlobalTickets' ? 'selected' : ''}>通用券</option>
                        <option value="lotteryPoolTickets" ${type === 'lotteryPoolTickets' ? 'selected' : ''}>限定券</option>
                        <option value="lotteryExchangeCoins" ${type === 'lotteryExchangeCoins' ? 'selected' : ''}>兑换币</option>
                    </select>
                    <input class="rw-name" value="${esc(r.name || '')}" placeholder="展示名（可空）" style="flex:1" />
                    <label class="with-cb" style="white-space:nowrap"><input class="rw-featured" type="checkbox" ${r.featured ? 'checked' : ''} /> 大奖</label>
                    <button type="button" class="mini del rw-del" title="删除" onclick="this.closest('.rw-card').remove()">×</button>
                </div>
                <div class="rw-row rw-item-row" style="${itemStyle}">
                    <input class="rw-tpl" value="${esc(r.tpl || '')}" placeholder="搜索物品名称或 tpl…" autocomplete="off" />
                    <input class="rw-count" type="number" value="${r.count || 1}" min="1" style="width:58px" title="数量" />
                    <label class="with-cb" style="white-space:nowrap" title="标记战局内找到（SpawnedInSession）"><input class="rw-fir" type="checkbox" ${r.foundInRaid ? 'checked' : ''} /> FIR</label>
                    <div class="rw-tpl-results ip-results" style="display:none"></div>
                </div>
                <div class="rw-extra" style="${extraStyle}">
                    <div class="rw-row rw-offerid-row" style="${show('purchaseRight')}">
                        <label style="font-size:10px">购买权</label>
                        <input class="rw-offerid" value="${esc(r.offerId || '')}" placeholder="聚焦列出 / 搜索货架项…" style="flex:1" autocomplete="off" />
                        <div class="rw-offerid-results rw-tpl-results ip-results" style="display:none"></div>
                    </div>
                    <div class="rw-row rw-recipeid-row" style="${show('recipe')}">
                        <label style="font-size:10px">配方</label>
                        <input class="rw-recipeid" value="${esc(r.recipeId || '')}" placeholder="搜索配方产物名称或 id…" style="flex:1" autocomplete="off" />
                        <div class="rw-recipeid-results rw-tpl-results ip-results" style="display:none"></div>
                    </div>
                    <div class="rw-row rw-titleid-row" style="${show('title')}">
                        <label style="font-size:10px">称号</label>
                        <input class="rw-titleid" value="${esc(r.titleId || '')}" placeholder="聚焦列出 / 搜索称号…" style="flex:1" autocomplete="off" />
                        <div class="rw-titleid-results rw-tpl-results ip-results" style="display:none"></div>
                    </div>
                    <div class="rw-row rw-suitid-row" style="${show('clothing')}">
                        <label style="font-size:10px">服装</label>
                        <input class="rw-suitid" value="${esc(r.suitId || '')}" placeholder="聚焦列出 / 搜索服装、suiteId、offerId…" style="flex:1" autocomplete="off" />
                        <div class="rw-suitid-results rw-tpl-results ip-results" style="display:none"></div>
                    </div>
                    <div class="rw-row rw-token-row" style="${tokenStyle}">
                        <label style="font-size:10px">数量</label>
                        <input class="rw-token-count" type="number" min="1" value="${r.count || 1}" style="width:96px" />
                        <span class="muted" style="font-size:10px">发放到抽奖钱包，不进入玩家仓库</span>
                    </div>
                    <div class="rw-row rw-poolid-row" style="${show('lotteryPoolTickets')}">
                        <label style="font-size:10px">奖池</label>
                        <select class="rw-poolid" style="flex:1">${poolOptions(pools, r.poolId || '')}</select>
                    </div>
                </div>
            </div>
        </div>
    </div>`;
    }

    function onTypeChange(card, type) {
        const q = s => card.querySelector(s);
        const itemRow = q('.rw-item-row');
        if (itemRow) itemRow.style.display = type === 'item' ? '' : 'none';
        q('.rw-extra').style.display = type === 'item' ? 'none' : '';
        REF_ROWS.forEach(s => { const e = q(s); if (e) e.style.display = 'none'; });
        if (REF_ROW_BY_TYPE[type]) { const e = q(REF_ROW_BY_TYPE[type]); if (e) e.style.display = ''; }
        if (TOKEN_TYPES.includes(type)) { const e = q('.rw-token-row'); if (e) e.style.display = ''; }
        if (type === 'lotteryPoolTickets') { const e = q('.rw-poolid-row'); if (e) e.style.display = ''; }
    }

    // 引用类选择器（购买权→货架 / 配方→配方 / 称号 / 服装）。货架/称号/服装数据少：聚焦即列；配方多：输 2 字。
    function wireRef(card, inputSel, kind) {
        const input = card.querySelector(inputSel);
        if (!input) return;
        const results = input.parentElement.querySelector('.rw-tpl-results');
        if (!results) return;
        const icon = card.querySelector('.rw-icon');
        const attach = { offer: BpPicker.attachOffer, recipe: BpPicker.attachRecipe, title: BpPicker.attachTitle, clothing: BpPicker.attachClothing }[kind];
        if (!attach) return;
        attach(input, results, ds => {
            input.value = ds.suitId || ds.id;
            card.classList.remove('rw-invalid');
            const nameInput = card.querySelector('.rw-name');
            if (ds.name && nameInput && !nameInput.value.trim()) nameInput.value = ds.name;
            if (ds.tpl && BpPicker.isTpl(ds.tpl)) { icon.src = BpPicker.ICON_API + ds.tpl; icon.style.display = ''; icon.onerror = () => { icon.style.display = 'none'; }; }
        }, { limit: 8, minChars: kind === 'recipe' ? 2 : 0 });
        if (kind === 'recipe') {
            input.addEventListener('focus', () => setTimeout(() => {
                if (results.querySelector('.sr-new-link')) return;
                const a = document.createElement('a');
                a.className = 'sr-new-link'; a.textContent = '＋ 新建自定义配方（配方管理页）'; a.href = 'recipes.html'; a.target = '_blank';
                results.appendChild(a);
            }, 350));
        }
    }

    function wire(card) {
        const tplInput = card.querySelector('.rw-tpl');
        const icon = card.querySelector('.rw-icon');
        if (tplInput) {
            BpPicker.attachItem(tplInput, card.querySelector('.rw-tpl-results'), ds => {
                tplInput.value = ds.tpl;
                icon.src = BpPicker.ICON_API + ds.tpl; icon.style.display = ''; icon.onerror = () => { icon.style.display = 'none'; };
            }, { limit: 8 });
            tplInput.addEventListener('input', () => {
                const v = tplInput.value.trim();
                if (BpPicker.isTpl(v)) { icon.src = BpPicker.ICON_API + v; icon.style.display = ''; }
            });
        }
        wireRef(card, '.rw-offerid', 'offer');
        wireRef(card, '.rw-recipeid', 'recipe');
        wireRef(card, '.rw-titleid', 'title');
        wireRef(card, '.rw-suitid', 'clothing');
        const typeSel = card.querySelector('.rw-type');
        if (typeSel) typeSel.addEventListener('change', e => onTypeChange(card, e.target.value));
    }

    function addCard(container, reward, pools) {
        const empty = container.querySelector('.rw-empty');
        if (empty) empty.remove();
        const wrap = document.createElement('div');
        wrap.innerHTML = cardHtml(reward || { tpl: '', count: 1, name: null, featured: false, type: 'item' }, pools);
        const card = wrap.firstElementChild;
        container.appendChild(card);
        wire(card);
        return card;
    }

    function fill(container, rewards, pools) {
        rewards = rewards || [];
        if (!rewards.length) { container.innerHTML = '<div class="muted rw-empty">暂无，点「＋ 添加」</div>'; return; }
        container.innerHTML = rewards.map(r => cardHtml(r, pools)).join('');
        container.querySelectorAll('.rw-card').forEach(wire);
    }

    function collect(container, opts) {
        opts = opts || {};
        const rewards = [];
        let invalid = 0;
        container.querySelectorAll('.rw-card').forEach(card => {
            const g = s => card.querySelector(s)?.value?.trim() || '';
            const type = card.querySelector('.rw-type')?.value || 'item';
            const tpl = g('.rw-tpl');
            const name = g('.rw-name') || null;
            const offerId = g('.rw-offerid') || null;
            const recipeId = g('.rw-recipeid') || null;
            const titleId = g('.rw-titleid') || null;
            const suitId = g('.rw-suitid') || null;
            const poolId = g('.rw-poolid') || null;
            const isToken = TOKEN_TYPES.includes(type);
            const count = Math.max(1, +((isToken ? card.querySelector('.rw-token-count')?.value : card.querySelector('.rw-count')?.value) || 1));
            const keyField = { item: tpl, purchaseRight: offerId, recipe: recipeId, title: titleId, clothing: suitId, lotteryGlobalTickets: true, lotteryPoolTickets: poolId, lotteryExchangeCoins: true }[type];

            if (type === 'item' && !tpl && !name) return; // 全空卡 = 未填的新卡
            if (!keyField) {
                card.classList.add('rw-invalid');
                invalid++;
                if (opts.dropInvalid) return;
            } else {
                card.classList.remove('rw-invalid');
            }

            rewards.push({
                type,
                tpl: type === 'item' ? tpl : (tpl || null),
                count: (type === 'item' || isToken) ? count : 1,
                name,
                featured: card.querySelector('.rw-featured')?.checked || false,
                foundInRaid: type === 'item' ? (card.querySelector('.rw-fir')?.checked || false) : false,
                offerId: type === 'purchaseRight' ? offerId : null,
                recipeId: type === 'recipe' ? recipeId : null,
                titleId: type === 'title' ? titleId : null,
                suitId: type === 'clothing' ? suitId : null,
                poolId: type === 'lotteryPoolTickets' ? poolId : null,
            });
        });
        return { rewards, invalid };
    }

    global.RewardEditor = { ICON_API, isTpl, TOKEN_TYPES, poolOptions, cardHtml, onTypeChange, wire, addCard, fill, collect };
})(window);
